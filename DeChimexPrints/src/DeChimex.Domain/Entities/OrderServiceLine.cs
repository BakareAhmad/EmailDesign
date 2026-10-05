using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A service line on an order (e.g. "Logo engraving on customer-supplied flask").
/// Never affects inventory.
/// </summary>
public sealed class OrderServiceLine : Entity
{
    private OrderServiceLine() { } // EF

    private OrderServiceLine(Guid orderId, Guid serviceId, string serviceName, Money unitPrice, int quantity, string? notes)
    {
        OrderId = orderId;
        ServiceId = serviceId;
        ServiceName = serviceName;
        UnitPrice = unitPrice;
        Quantity = quantity;
        Notes = notes;
    }

    public Guid OrderId { get; private set; }
    public Guid ServiceId { get; private set; }
    public string ServiceName { get; private set; } = default!;
    public Money UnitPrice { get; private set; } = default!;
    public int Quantity { get; private set; }
    public string? Notes { get; private set; }

    public Money LineTotal => UnitPrice.Multiply(Quantity);

    internal static OrderServiceLine Create(Guid orderId, Guid serviceId, string serviceName, Money unitPrice, int quantity, string? notes)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("orderservice.quantity", "Quantity must be positive.");
        return new OrderServiceLine(orderId, serviceId, serviceName, unitPrice, quantity, notes);
    }
}
