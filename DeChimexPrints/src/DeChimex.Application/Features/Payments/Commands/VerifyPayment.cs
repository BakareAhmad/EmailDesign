using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using FluentValidation;

namespace DeChimex.Application.Features.Payments.Commands;

/// <summary>
/// Verifies a payment with the gateway. <paramref name="ExpectedCustomerId"/> is the
/// customer making the request (user-facing flows): a mismatch behaves exactly like an
/// unknown reference so callers cannot trigger or probe other customers' payments.
/// The Paystack webhook path passes null (server-to-server).
/// </summary>
public sealed record VerifyPaymentCommand(string Reference, Guid? ExpectedCustomerId = null) : ICommand<PaymentDto>;

public sealed class VerifyPaymentValidator : AbstractValidator<VerifyPaymentCommand>
{
    public VerifyPaymentValidator() => RuleFor(x => x.Reference).NotEmpty();
}

/// <summary>
/// Verifies a payment with the gateway and, on success, confirms the order via the shared
/// <see cref="IPaymentConfirmationService"/>. Idempotent: a duplicate verification/webhook
/// for an already-succeeded payment is a no-op. Outcomes follow Paystack's documented
/// transaction statuses: in-flight/unknown statuses stay pending (retryable, unconfirmed),
/// terminal unpaid statuses mark the attempt failed, and only a confirmed success with the
/// exact expected amount and currency marks anything paid.
/// </summary>
public sealed class VerifyPaymentHandler(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    IPaymentConfirmationService confirmation,
    IOrderChatPusher chat,
    IGatewayPayloadRedactor redactor,
    IUnitOfWork uow) : ICommandHandler<VerifyPaymentCommand, PaymentDto>
{
    public async Task<PaymentDto> Handle(VerifyPaymentCommand command, CancellationToken ct)
    {
        var payment = await payments.GetByReferenceAsync(command.Reference, ct);

        // Ownership: a user-facing verification must belong to the caller. Answer exactly
        // like an unknown reference (no leak, no gateway call, no state change).
        if (payment is null
            || (command.ExpectedCustomerId is Guid owner && payment.CustomerId != owner))
            throw new NotFoundException("Payment", command.Reference);

        // Idempotency: already confirmed -> return current state without side effects.
        if (payment.Status is PaymentStatus.Succeeded or PaymentStatus.Refunded)
            return Map(payment);

        var result = await gateway.VerifyAsync(command.Reference, ct);

        if (result.Outcome == PaymentVerificationOutcome.Pending)
        {
            // Verification could not settle yet (in-flight charge, or the verification
            // request itself failed). The payment stays Initialized and retryable; a
            // webhook can still confirm it later. Never mark paid or failed here.
            return Map(payment);
        }

        if (result.Outcome == PaymentVerificationOutcome.Failed)
        {
            payment.MarkFailed(string.IsNullOrWhiteSpace(result.Message)
                ? "Payment was not successful."
                : result.Message!, redactor.Redact(result.RawResponse));
            payments.Update(payment);
            await uow.SaveChangesAsync(ct);
            return Map(payment);
        }

        // The gateway must have charged exactly what we initialized (order amount plus
        // customer-borne charge) in the expected currency. Never accept an under-,
        // over- or foreign-currency payment as success.
        if (result.AmountPaid != payment.Amount.Amount)
        {
            payment.MarkFailed(
                $"Amount mismatch: expected {payment.Amount.Amount:N2}, gateway reported {result.AmountPaid:N2}.",
                redactor.Redact(result.RawResponse));
            payments.Update(payment);
            await uow.SaveChangesAsync(ct);
            return Map(payment);
        }

        if (!string.IsNullOrWhiteSpace(result.Currency)
            && !string.Equals(result.Currency, payment.Amount.Currency, StringComparison.OrdinalIgnoreCase))
        {
            payment.MarkFailed(
                $"Currency mismatch: expected {payment.Amount.Currency}, gateway reported {result.Currency}.",
                redactor.Redact(result.RawResponse));
            payments.Update(payment);
            await uow.SaveChangesAsync(ct);
            return Map(payment);
        }

        var confirmed = payment.MarkSucceeded(result.GatewayReference ?? command.Reference, redactor.Redact(result.RawResponse), result.Fees);
        payments.Update(payment);
        // Persist the Succeeded payment before the confirmation pipeline: it re-queries
        // the database for the payment in order to generate the receipt.
        // AUD-002: a concurrent webhook may have confirmed first — the rowversion rejects
        // our write. Re-read: if the concurrent writer marked it Succeeded, that IS the
        // verified outcome (same reference, same amount checks), so report current state.
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            var winner = await payments.GetByReferenceAsync(command.Reference, ct);
            if (winner is { Status: PaymentStatus.Succeeded or PaymentStatus.Refunded })
                return Map(winner);
            throw;
        }

        var transitioned = confirmed && await confirmation.ConfirmPaidOrderAsync(payment.OrderId, ct);

        await uow.SaveChangesAsync(ct);

        // Live: open workspaces flip to Paid without reloading (best-effort).
        if (transitioned)
            await chat.PushOrderEventAsync(payment.OrderId, "orderStatus", new
            {
                status = "Paid",
                timelineType = "payment",
                timelineDescription = "Payment confirmed."
            }, ct);

        return Map(payment);
    }

    private static PaymentDto Map(Payment p) => new(
        p.Id, p.OrderId, p.Amount.Amount, p.Amount.Currency, p.Method.ToString(), p.Status.ToString(),
        p.Reference, p.GatewayReference, p.PaidAtUtc, p.CreatedDateUtc, p.FailureReason,
        p.OrderAmount, p.ServiceFeePercent, p.CustomerBorneCharge, p.PaystackFeeExpected, p.PaystackFeeActual);
}
