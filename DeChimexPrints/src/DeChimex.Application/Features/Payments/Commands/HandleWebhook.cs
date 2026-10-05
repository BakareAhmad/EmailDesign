using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DeChimex.Application.Features.Payments.Commands;

/// <summary>
/// Processes a verified Paystack webhook. The raw body and signature are validated by the
/// gateway before any state change. Duplicate deliveries are safe (idempotent confirmation).
/// Handles both settlement directions: charge.success (customer paid) and the refund.*
/// lifecycle (AUD-005) so a refund that later fails at the provider surfaces as an
/// attention alert instead of silently disagreeing with the books.
/// </summary>
public sealed record HandlePaystackWebhookCommand(string RawBody, string Signature) : ICommand<Unit>;

public sealed class HandlePaystackWebhookHandler(
    IPaymentGateway gateway,
    IPaymentRepository payments,
    IPaymentConfirmationService confirmation,
    IOrderChatPusher chat,
    IWebhookPayloadParser parser,
    IGatewayPayloadRedactor redactor,
    INotificationService notifications,
    ILogger<HandlePaystackWebhookHandler> logger,
    IUnitOfWork uow,
    IIntegrationCallRecorder integrationCalls) : ICommandHandler<HandlePaystackWebhookCommand, Unit>
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Developer Portal payment diagnostics: one row per webhook delivery outcome.
    /// Never stores the payload (it contains customer data).</summary>
    private void Track(string outcome, bool ok, string? reference, Guid? orderId, string detail)
        => integrationCalls.Record(new IntegrationCallEntry(IntegrationNames.Paystack, "webhook", outcome, ok,
            _sw.ElapsedMilliseconds, Reference: reference, OrderId: orderId, Detail: detail));

    public async Task<Unit> Handle(HandlePaystackWebhookCommand command, CancellationToken ct)
    {
        // 1. Verify signature before trusting the payload otherwise.
        if (!gateway.VerifyWebhookSignature(command.RawBody, command.Signature))
        {
            logger.LogWarning("Rejected Paystack webhook with invalid signature.");
            Track("Rejected", false, null, null, "Invalid signature (x-paystack-signature did not match).");
            return Unit.Value; // ack silently to avoid retries storms, but do nothing
        }

        // 2. Extract event + reference.
        var evt = parser.Parse(command.RawBody);
        if (evt is null || string.IsNullOrWhiteSpace(evt.EventType))
        {
            Track("Ignored", true, evt?.Reference, null, "Unparseable payload.");
            return Unit.Value;
        }

        // AUD-005: refund settlement lifecycle. The payment is located by Paystack's
        // refund reference (captured when the /refund request was accepted). Events are
        // idempotent — a re-delivery re-writes the same status.
        if (evt.IsRefundEvent)
        {
            await HandleRefundEventAsync(evt, ct);
            return Unit.Value;
        }

        if (!evt.IsChargeSuccess || string.IsNullOrWhiteSpace(evt.Reference))
        {
            Track("Ignored", true, evt.Reference, null, $"Event '{evt.EventType}' is not handled; ignored.");
            return Unit.Value;
        }

        var payment = await payments.GetByReferenceAsync(evt.Reference, ct);
        if (payment is null)
        {
            logger.LogWarning("Webhook for unknown payment reference {Reference}.", evt.Reference);
            Track("UnknownReference", false, evt.Reference, null, "No payment with this reference.");
            return Unit.Value;
        }

        if (payment.Status is PaymentStatus.Succeeded or PaymentStatus.Refunded)
        {
            Track("Duplicate", true, evt.Reference, payment.OrderId, $"Payment already {payment.Status}; delivery ignored (idempotent).");
            return Unit.Value; // idempotent
        }

        // The event amount and currency must match what we initialized (order +
        // customer-borne charge); a mismatched event is not applied to this payment.
        if (evt.Amount > 0 && evt.Amount != payment.Amount.Amount)
        {
            logger.LogWarning("Webhook amount {EventAmount} does not match payment {Reference} amount {PaymentAmount}; ignoring.",
                evt.Amount, evt.Reference, payment.Amount.Amount);
            Track("AmountMismatch", false, evt.Reference, payment.OrderId, $"Event amount {evt.Amount:0.00} != expected {payment.Amount.Amount:0.00}; not applied.");
            return Unit.Value;
        }
        if (!string.IsNullOrWhiteSpace(evt.Currency)
            && !string.Equals(evt.Currency, payment.Amount.Currency, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Webhook currency {EventCurrency} does not match payment {Reference} currency {PaymentCurrency}; ignoring.",
                evt.Currency, evt.Reference, payment.Amount.Currency);
            Track("CurrencyMismatch", false, evt.Reference, payment.OrderId, $"Event currency {evt.Currency} != expected {payment.Amount.Currency}; not applied.");
            return Unit.Value;
        }

        // AUD-033: the raw body embeds the customer's contact/authorization data — store
        // it redacted (financial facts survive; PII does not).
        var confirmed = payment.MarkSucceeded(evt.GatewayReference ?? evt.Reference, redactor.Redact(command.RawBody));
        payments.Update(payment);

        // Persist the Succeeded payment before the confirmation pipeline: it re-queries
        // the database for the payment in order to generate the receipt.
        // AUD-002: the concurrent browser-return verify may have committed first; the
        // rowversion rejects our write. A concurrent confirmation of the SAME payment is
        // equivalent to this delivery succeeding — ack without re-running the pipeline.
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            Track("ConcurrentDuplicate", true, evt.Reference, payment.OrderId,
                "A concurrent confirmation already applied this payment (rowversion conflict); nothing re-applied.");
            return Unit.Value;
        }
        var transitioned = confirmed && await confirmation.ConfirmPaidOrderAsync(payment.OrderId, ct);

        await uow.SaveChangesAsync(ct);
        Track("Processed", true, evt.Reference, payment.OrderId, transitioned ? "Payment confirmed; order moved to Paid." : "Payment marked succeeded.");

        // Live: open workspaces flip to Paid without reloading (best-effort).
        if (transitioned)
            await chat.PushOrderEventAsync(payment.OrderId, "orderStatus", new
            {
                status = "Paid",
                timelineType = "payment",
                timelineDescription = "Payment confirmed."
            }, ct);

        return Unit.Value;
    }

    private async Task HandleRefundEventAsync(WebhookEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RefundReference))
        {
            Track("Ignored", true, null, null, $"Refund event '{evt.EventType}' without a refund reference; ignored.");
            return;
        }

        var payment = await payments.GetByRefundReferenceAsync(evt.RefundReference, ct);
        if (payment is null)
        {
            Track("UnknownReference", false, evt.RefundReference, null, $"Refund event '{evt.EventType}' for an unknown refund reference.");
            return;
        }

        switch (evt.EventType.ToLowerInvariant())
        {
            case "refund.processed":
                payment.MarkRefundProcessed();
                payments.Update(payment);
                await uow.SaveChangesAsync(ct);
                Track("RefundProcessed", true, evt.RefundReference, payment.OrderId, "Provider confirmed the refund settled.");
                break;

            case "refund.failed":
                payment.MarkRefundFailed();
                payments.Update(payment);
                await uow.SaveChangesAsync(ct);
                // Attention surface for staff: the order reads Refunded but the money is
                // still with the business. Business-activity capture records the attention
                // row; this notification pages whoever holds the refund permission.
                await notifications.NotifyAsync(
                    NotificationType.System,
                    "Refund FAILED at provider",
                    $"Paystack reported the refund ({evt.RefundReference}) of {payment.Amount.Amount:N2} {payment.Amount.Currency} did NOT settle. Reconcile with the provider and the customer.",
                    link: $"/admin/orders/{payment.OrderId}",
                    requiredPermission: Shared.Constants.Permissions.Orders.Refund,
                    ct: ct);
                Track("RefundFailed", false, evt.RefundReference, payment.OrderId, "Provider reported the refund failed; attention alert raised.");
                break;

            case "refund.pending":
                // Informational only: acceptance is already recorded; nothing to change.
                Track("RefundPending", true, evt.RefundReference, payment.OrderId, "Provider reports the refund is still pending settlement.");
                break;

            default:
                Track("Ignored", true, evt.RefundReference, payment.OrderId, $"Refund event '{evt.EventType}' not mapped; ignored.");
                break;
        }
    }
}

/// <summary>Parses a gateway webhook body into a minimal, provider-agnostic event.</summary>
public interface IWebhookPayloadParser
{
    WebhookEvent? Parse(string rawBody);
}

/// <param name="EventType">Raw provider event name, e.g. "charge.success", "refund.processed".</param>
/// <param name="IsChargeSuccess">True for the customer-paid event.</param>
/// <param name="Reference">Our payment reference (charge events).</param>
/// <param name="GatewayReference">Gateway-side reference for the payment.</param>
/// <param name="Amount">Major-unit amount carried by the event.</param>
/// <param name="Currency">ISO currency carried by the event, if any.</param>
/// <param name="RefundReference">The provider's refund reference (refund.* events, AUD-005).</param>
public sealed record WebhookEvent(
    string EventType,
    bool IsChargeSuccess,
    string? Reference,
    string? GatewayReference,
    decimal Amount,
    string? Currency = null,
    string? RefundReference = null)
{
    /// <summary>True when the event belongs to the refund settlement lifecycle.</summary>
    public bool IsRefundEvent => EventType.StartsWith("refund.", StringComparison.OrdinalIgnoreCase);
}
