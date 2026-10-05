using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;

namespace DeChimex.Infrastructure.Payments;

/// <summary>
/// Refunds all succeeded gateway payments for an order and updates local payment state.
/// Manual (bank transfer / cash) payments are marked refunded locally only.
/// Throws ConflictException if any Paystack refund fails.
/// </summary>
public sealed class PaymentGatewayRefundCoordinator(
    IPaymentRepository payments,
    IPaymentGateway gateway, IUnitOfWork uow) : IPaymentGatewayRefundCoordinator
{
    public async Task RefundOrderPaymentsAsync(Guid orderId, CancellationToken ct = default)
    {
        // Scoped to the order (previously this listed every payment in the table and filtered in memory).
        var orderPayments = await payments.GetByOrderIdAsync(orderId, ct);
        var succeeded = orderPayments.Where(p => p.Status == PaymentStatus.Succeeded);

        foreach (var payment in succeeded)
        {
            if (payment.Method == PaymentMethod.Paystack)
            {
                var result = await gateway.RefundAsync(payment.GatewayReference ?? payment.Reference, payment.Amount.Amount, ct);
                if (!result.Success)
                {
                    throw new ConflictException(
                        "payment.refund.failed",
                        string.IsNullOrWhiteSpace(result.Message) 
                            ? "Paystack refund failed for an unknown reason."
                            : result.Message);
                }
                
                // Legacy Refunded means the provider accepted this request, not final settlement.
                payment.MarkRefunded(result.RefundReference);
                payments.Update(payment);
            }
            else
            {
                // Manual payments (BankTransfer, Cash) settle offline: record the refund as
                // accepted AND settled immediately (AUD-005 settlement lifecycle).
                payment.MarkRefunded();
                payment.MarkRefundProcessedLocally();
                payments.Update(payment);
            }
            // Checkpoint each accepted payment before moving to another external call.
            // A later failed request must not erase an earlier accepted refund or cause a retry to resend it.
            await uow.SaveChangesAsync(ct);
        }
    }
}
