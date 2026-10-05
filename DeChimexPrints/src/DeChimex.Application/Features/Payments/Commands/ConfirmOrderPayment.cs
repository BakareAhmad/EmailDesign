using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentValidation;

namespace DeChimex.Application.Features.Payments.Commands;

/// <summary>
/// Staff marks an order as paid directly (bank transfer, cash, or a Paystack reference
/// the customer shared). Uses the latest open payment for the order when one
/// exists, otherwise creates one, then runs the shared post-payment fulfilment pipeline
/// (inventory reservation, procurement flag, invoice, spending, notification).
/// Idempotent: confirming an already-paid order is a no-op.
/// </summary>
public sealed record ConfirmOrderPaymentCommand(Guid OrderId, string Method, string? Note = null) : ICommand<PaymentDto>;

public sealed class ConfirmOrderPaymentValidator : AbstractValidator<ConfirmOrderPaymentCommand>
{
    public ConfirmOrderPaymentValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Method).NotEmpty();
    }
}

public sealed class ConfirmOrderPaymentHandler(
    IOrderRepository orders,
    IPaymentRepository payments,
    IReferenceGenerator references,
    IPaymentConfirmationService confirmation,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<ConfirmOrderPaymentCommand, PaymentDto>
{
    public async Task<PaymentDto> Handle(ConfirmOrderPaymentCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithLinesAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        if (order.Status is not (OrderStatus.PendingPayment or OrderStatus.Draft))
            throw new BusinessRuleValidationException("payment.order.state", "Only orders awaiting payment can be marked paid.");

        if (!Enum.TryParse<PaymentMethod>(command.Method, ignoreCase: true, out var method))
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["Method"] = new[] { "Unsupported payment method." }
            });

        var existing = (await payments.GetByOrderIdAsync(order.Id, ct))
            .FirstOrDefault(p => p.Status is PaymentStatus.Pending or PaymentStatus.Initialized);

        // AUD-012: never reuse a gateway attempt for a different method. An open Paystack
        // attempt carries the customer-borne service fee in its Amount ("order + fee" at
        // the gateway); recording that amount as a Cash/transfer confirmation would write
        // "Cash, ₦(order+fee)" into the ledger. A method mismatch (or any Paystack attempt
        // confirmed by hand) gets a FRESH attempt for the bare order total instead.
        var reuseExisting = existing is not null
            && existing.Method == method
            && method != PaymentMethod.Paystack;

        var payment = reuseExisting ? existing! : Payment.Initialize(order.Id, order.CustomerId, order.GrandTotal.Amount, method, references.NewPaymentReference());
        if (!reuseExisting)
            await payments.AddAsync(payment, ct);

        if (payment.Status != PaymentStatus.Succeeded)
        {
            payment.MarkSucceeded($"manual-{payment.Reference}",
                string.IsNullOrWhiteSpace(command.Note) ? "Manually confirmed by staff." : command.Note.Trim());
            payments.Update(payment);
            // Persist the Succeeded payment BEFORE the confirmation pipeline runs:
            // the service re-queries the database for a Succeeded payment (to build
            // the receipt), so an unsaved state would be invisible to it.
            // AUD-002: a concurrent gateway confirmation may have won the write — if the
            // payment is already Succeeded the manual confirmation is done.
            try
            {
                await uow.SaveChangesAsync(ct);
            }
            catch (ConcurrencyConflictException)
            {
                var winner = (await payments.GetByOrderIdAsync(order.Id, ct))
                    .FirstOrDefault(x => x.Id == payment.Id);
                if (winner is { Status: PaymentStatus.Succeeded or PaymentStatus.Refunded })
                    return Map(winner);
                throw;
            }

            var transitioned = await confirmation.ConfirmPaidOrderAsync(order.Id, ct); // idempotent on the order

            await uow.SaveChangesAsync(ct);

            // Live: open workspaces flip to Paid without reloading (best-effort).
            if (transitioned)
                await chat.PushOrderEventAsync(order.Id, "orderStatus", new
                {
                    status = "Paid",
                    timelineType = "payment",
                    timelineDescription = "Payment confirmed."
                }, ct);
        }
        else
        {
            await uow.SaveChangesAsync(ct);
        }

        return Map(payment);
    }

    private static PaymentDto Map(Payment p) => new(
        p.Id, p.OrderId, p.Amount.Amount, p.Amount.Currency,
        p.Method.ToString(), p.Status.ToString(), p.Reference,
        p.GatewayReference, p.PaidAtUtc, p.CreatedDateUtc,
        p.FailureReason, p.OrderAmount, p.ServiceFeePercent,
        p.CustomerBorneCharge, p.PaystackFeeExpected, p.PaystackFeeActual);
}
