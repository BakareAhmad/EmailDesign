using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Shared.Constants;
using DeChimex.Application.Features.Orders;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;

namespace DeChimex.Application.Features.Payments;

/// <summary>
/// Single source of truth for the post-payment order workflow shared by gateway verification
/// (<see cref="Commands.VerifyPaymentHandler"/>), the Paystack webhook
/// (<see cref="Commands.HandlePaystackWebhookHandler"/>) and staff manual confirmation
/// (<see cref="Commands.ConfirmManualPaymentHandler"/>). It marks the order paid, reserves
/// inventory (products only), issues or marks the invoice, records lifetime spending and notifies customers when needed.
/// and is idempotent (a no-op once the order is already paid). Extracted to remove the three
/// near-identical copies of this logic.
/// </summary>
public interface IPaymentConfirmationService
{
    /// <returns>True when the order transitioned to paid by this call; false when it
    /// was already paid (idempotent no-op) or the order is missing.</returns>
    Task<bool> ConfirmPaidOrderAsync(Guid orderId, CancellationToken ct);
}

public sealed class PaymentConfirmationService(
    IOrderRepository orders,
    IInvoiceRepository invoices,
    IReceiptRepository receipts,
    IPaymentRepository payments,
    ICustomerRepository customers,
    IOrderFulfilmentService fulfilment,
    IReferenceGenerator references,
    INotificationService notifications,
    IEmailQueue emailQueue,
    IEmailTemplateRenderer emailRenderer,
    IAppUrlProvider urls,
    ICurrentUser currentUser) : IPaymentConfirmationService
{
    public async Task<bool> ConfirmPaidOrderAsync(Guid orderId, CancellationToken ct)
    {
        // AUD-002: Paystack delivers the charge.success webhook and the browser's return
        // verify at nearly the same moment, so two of these can run concurrently. The
        // Order/Payment rowversion tokens make one of them lose the database write
        // (DbUpdateConcurrencyException); the loser retries, observes the order already
        // paid and returns a clean no-op instead of double-recording spending, emails
        // and receipts. Retries are bounded; after the last one the idempotency re-check
        // decides the outcome.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ConfirmOnceAsync(orderId, ct);
            }
            catch (ConcurrencyConflictException) when (attempt < 3)
            {
                // A concurrent confirmation committed first; reload and re-check.
            }
        }
    }

    private async Task<bool> ConfirmOnceAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.GetWithLinesAsync(orderId, ct);
        if (order is null) return false; // defensive: a payment always references a real order
        if (order.Status is OrderStatus.Paid or OrderStatus.Completed or OrderStatus.Delivered) return false; // idempotent

        order.MarkPaid(); // -> Paid, raises OrderPaidEvent
        order.AddTimelineEntry("payment", "Payment confirmed.", currentUser.UserId, currentUser.Email ?? "Customer");
        await fulfilment.ReserveInventoryForPaidOrderAsync(order, ct); // products only; shortages flag procurement
        orders.Update(order);

        var customer = await customers.GetByIdAsync(order.CustomerId, ct);

        // The invoice was issued when the order was placed (before payment); here it is
        // simply marked paid. The defensive fallback covers orders that predate that flow.
        var invoice = await invoices.GetByOrderIdAsync(order.Id, ct);
        if (invoice is null)
        {
            // New invoice is issued already marked paid; Add stages it for insert (do NOT also call
            // Update. EF InMemory throws DbUpdateConcurrencyException updating an unsaved Add).
            invoice = Domain.Entities.Invoice.Issue(await references.NextInvoiceNumberAsync(ct), order,
                customer?.FullName ?? "");
            invoice.SetShareToken(references.NewShareToken());
            invoice.MarkPaid();
            await invoices.AddAsync(invoice, ct);
        }
        else
        {
            invoice.MarkPaid();
            invoices.Update(invoice);
        }

        // The receipt is the proof-of-payment document, generated AFTER payment.
        var succeededPayment = (await payments.GetByOrderIdAsync(order.Id, ct))
            .FirstOrDefault(p => p.Status == PaymentStatus.Succeeded);
        if (succeededPayment is not null && await receipts.GetByOrderIdAsync(order.Id, ct) is null)
        {
            var receipt = Domain.Entities.Receipt.CreateForPayment(
                await references.NextReceiptNumberAsync(ct), order, succeededPayment, customer?.FullName ?? "");
            receipt.SetShareToken(references.NewShareToken());
            await receipts.AddAsync(receipt, ct);

            // Email the customer their payment receipt (the PaymentReceipt template previously
            // existed but was never triggered - dead code until now). Best-effort via the queue.
            if (customer is { Email: not null })
            {
                var content = emailRenderer.Render(EmailTemplate.PaymentReceipt, new Dictionary<string, string>
                {
                    ["FirstName"] = customer.FirstName,
                    ["OrderNumber"] = order.OrderNumber,
                    ["Amount"] = DeChimex.Application.Common.MoneyText.Format(order.GrandTotal.Amount),
                    ["TrackingLink"] = urls.BuildOrderTrackingLink(order.OrderNumber)
                });
                await emailQueue.EnqueueAsync(new EmailMessage(customer.Email.Value, customer.FullName,
                    content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.PaymentReceipt), Category: "customer"), ct);
            }
        }

        if (customer is not null)
        {
            customer.RecordSpending(order.GrandTotal);
            customers.Update(customer);
        }

        await notifications.NotifyAsync(
            NotificationType.PaymentReceived,
            "Payment received",
            $"Payment for order {order.OrderNumber} of {order.GrandTotal} was confirmed.",
            link: $"/admin/orders/{order.Id}",
            requiredPermission: Permissions.Orders.UpdateStatus,
            ct: ct);
        return true;
    }
}
