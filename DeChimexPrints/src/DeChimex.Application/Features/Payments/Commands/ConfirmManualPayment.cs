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
/// Staff-triggered manual confirmation for bank-transfer payments.
/// Applies the same post-payment fulfilment rules as gateway verification via the shared
/// <see cref="IPaymentConfirmationService"/>.
/// </summary>
public sealed record ConfirmManualPaymentCommand(Guid PaymentId) : ICommand<PaymentDto>;

public sealed class ConfirmManualPaymentValidator : AbstractValidator<ConfirmManualPaymentCommand>
{
    public ConfirmManualPaymentValidator() => RuleFor(x => x.PaymentId).NotEmpty();
}

public sealed class ConfirmManualPaymentHandler(
    IPaymentRepository payments,
    IPaymentConfirmationService confirmation,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<ConfirmManualPaymentCommand, PaymentDto>
{
    public async Task<PaymentDto> Handle(ConfirmManualPaymentCommand command, CancellationToken ct)
    {
        var payment = await payments.GetByIdAsync(command.PaymentId, ct)
            ?? throw new NotFoundException("Payment", command.PaymentId);

        if (payment.Status is PaymentStatus.Succeeded or PaymentStatus.Refunded)
            return Map(payment);

        payment.MarkSucceeded($"manual-{payment.Reference}", "Manually confirmed by staff.");
        payments.Update(payment);

        var transitioned = await confirmation.ConfirmPaidOrderAsync(payment.OrderId, ct); // idempotent on the order

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
        p.Reference, p.GatewayReference, p.PaidAtUtc, p.CreatedDateUtc);
}
