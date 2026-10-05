using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Orders;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DeChimex.Application.Features.Orders.Commands;

public sealed record CancelOrderCommand(Guid OrderId, CancelOrderRequest Request) : ICommand<Unit>;

/// <summary>
/// Cancels an order and records the RESOLUTION OF ANY HELD PAYMENT next to it (AUD-018):
/// cancelling a paid order keeps the money with the business, so the caller must say what
/// happens to it — <c>RefundLater</c> (default: the refund is still owed and surfaces on
/// the dashboard's outstanding-refunds tile), <c>StoreCredit</c> (resolved without a money
/// movement), or <c>NotRequired</c> (nothing was ever paid). The outcome lands on the
/// customer-visible timeline and, for RefundLater on a paid order, raises a staff
/// notification so the owed refund cannot silently disappear with the order.
/// </summary>
public sealed class CancelOrderHandler(IOrderRepository orders, IPaymentRepository payments,
    INotificationService notifications,
    IUnitOfWork uow)
    : ICommandHandler<CancelOrderCommand, Unit>
{
    public async Task<Unit> Handle(CancelOrderCommand command, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);
        order.Cancel(command.Request.Reason);

        var outcome = command.Request.RefundOutcome ?? RefundOutcomes.RefundLater;
        var heldPayment = (await payments.GetByOrderIdAsync(order.Id, ct))
            .FirstOrDefault(p => p.Status == PaymentStatus.Succeeded);

        if (heldPayment is null)
            outcome = RefundOutcomes.NotRequired;

        order.AddTimelineEntry("cancel", outcome switch
        {
            RefundOutcomes.RefundLater => "Order cancelled. A payment is still held — refund owed to the customer.",
            RefundOutcomes.StoreCredit => "Order cancelled. The held payment is resolved as store credit.",
            _ => "Order cancelled. No payment was held; nothing to refund."
        });
        orders.Update(order);

        if (heldPayment is not null && outcome == RefundOutcomes.RefundLater)
        {
            await notifications.NotifyAsync(
                NotificationType.System,
                "Cancelled order still holds a payment",
                $"Order {order.OrderNumber} was cancelled while a payment of {heldPayment.Amount.Amount:N2} {heldPayment.Amount.Currency} remains unrefunded. Issue the refund or record store credit.",
                link: $"/admin/orders/{order.Id}",
                requiredPermission: Shared.Constants.Permissions.Orders.Refund,
                ct: ct);
        }

        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Accepted cancel-time payment resolutions (AUD-018).</summary>
public static class RefundOutcomes
{
    public const string RefundLater = "RefundLater";
    public const string StoreCredit = "StoreCredit";
    public const string NotRequired = "NotRequired";
}

public sealed record RefundOrderCommand(Guid OrderId) : ICommand<Unit>;

public sealed class RefundOrderHandler(
    IOrderRepository orders, IInvoiceRepository invoices, IReceiptRepository receipts,
    ICustomerRepository customers,
    IPaymentGatewayRefundCoordinator refundCoordinator, IUnitOfWork uow)
    : ICommandHandler<RefundOrderCommand, Unit>
{
    public async Task<Unit> Handle(RefundOrderCommand command, CancellationToken ct)
    {
        // Load WITH lines: GrandTotal is computed from the order lines, and the refund
        // must reverse exactly the lifetime-spending amount recorded at confirmation.
        var order = await orders.GetWithLinesAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        order.EnsureCanRefund(); // Validate BEFORE irreversible provider calls.

        // First, attempt to refund all payments through Paystack
        // This will throw ConflictException if any Paystack refund fails
        await refundCoordinator.RefundOrderPaymentsAsync(order.Id, ct);

        // Only mark order/invoice/receipt as refunded AFTER Paystack confirms all refunds
        order.Refund();
        orders.Update(order);

        var invoice = await invoices.GetByOrderIdAsync(order.Id, ct);
        invoice?.MarkRefunded();
        if (invoice is not null) invoices.Update(invoice);

        var receipt = await receipts.GetByOrderIdAsync(order.Id, ct);
        if (receipt is not null)
        {
            receipt.MarkRefunded();
            receipts.Update(receipt);
        }

        // Business accounting: lifetime spending is NET of refunds (reverses the amount
        // recorded at payment confirmation). Runs in the same SaveChanges as the
        // Paid -> Refunded transition, and EnsureCanRefund() blocks any second refund,
        // so the reversal applies exactly once per order.
        var customer = await customers.GetByIdAsync(order.CustomerId, ct);
        if (customer is not null)
        {
            customer.ReverseSpending(order.GrandTotal);
            customers.Update(customer);
        }

        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
