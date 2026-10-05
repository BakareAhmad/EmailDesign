namespace DeChimex.Application.Common.Interfaces;

/// <summary>Coordinates refunding all succeeded payments for an order (gateway + local state).</summary>
public interface IPaymentGatewayRefundCoordinator
{
    Task RefundOrderPaymentsAsync(Guid orderId, CancellationToken ct = default);
}
