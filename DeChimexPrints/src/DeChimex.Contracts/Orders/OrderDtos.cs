namespace DeChimex.Contracts.Orders;

public sealed record OrderItemDto(Guid Id, Guid ProductId, string ProductName, string Sku, decimal UnitPrice, int Quantity, decimal LineTotal);
public sealed record OrderServiceDto(Guid Id, Guid ServiceId, string ServiceName, decimal UnitPrice, int Quantity, string? Notes, decimal LineTotal);

public sealed record OrderDto(
    Guid Id, string OrderNumber, Guid CustomerId, string CustomerName, string Type, string Status,
    decimal Subtotal, decimal Discount, decimal DeliveryFee, decimal Tax, decimal GrandTotal, string Currency,
    bool RequiresProcurement, DateTime? PaidAtUtc, DateTime? CompletedAtUtc, DateTime CreatedDateUtc,
    IReadOnlyList<OrderItemDto> Items, IReadOnlyList<OrderServiceDto> Services,
    string Kind = "Direct");

public sealed record OrderSummaryDto(Guid Id, string OrderNumber, string CustomerName, string Type, string Status, decimal GrandTotal, string Currency, DateTime CreatedDateUtc, string Kind = "Direct");

public sealed record CreateOrderItemRequest(Guid ProductId, int Quantity);
public sealed record CreateOrderServiceRequest(Guid ServiceId, int Quantity, string? Notes);
public sealed record AddressRequest(string Street, string City, string State, string Country, string? PostalCode);

public sealed record CreateOrderRequest(
    Guid? CustomerId,
    IReadOnlyList<CreateOrderItemRequest> Items,
    IReadOnlyList<CreateOrderServiceRequest> Services,
    AddressRequest? ShippingAddress,
    AddressRequest? BillingAddress,
    string? CustomerNotes,
    decimal Discount,
    decimal DeliveryFee,
    decimal Tax);

public sealed record UpdateOrderStatusRequest(string Status);
/// <param name="RefundOutcome">What happens to a HELD payment when the order is cancelled
/// (AUD-018): RefundLater (default) | StoreCredit | NotRequired. Null behaves as RefundLater
/// and is normalised to NotRequired when no succeeded payment exists.</param>
public sealed record CancelOrderRequest(string? Reason, string? RefundOutcome = null);
