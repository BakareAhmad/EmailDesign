using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>A product line on an order. Products deduct inventory after payment.</summary>
public sealed class OrderItem : Entity
{
    private OrderItem() { } // EF

    private OrderItem(Guid orderId, Guid productId, string productName, string sku, Money unitPrice, int quantity)
    {
        OrderId = orderId;
        ProductId = productId;
        ProductName = productName;
        Sku = sku;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }

    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = default!;
    public string Sku { get; private set; } = default!;
    public Money UnitPrice { get; private set; } = default!;
    public int Quantity { get; private set; }

    public Money LineTotal => UnitPrice.Multiply(Quantity);

    internal static OrderItem Create(Guid orderId, Guid productId, string productName, string sku, Money unitPrice, int quantity)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("orderitem.quantity", "Quantity must be positive.");
        return new OrderItem(orderId, productId, productName, sku, unitPrice, quantity);
    }

    /// <summary>Staff-adjusted unit price at finalization (custom orders).</summary>
    internal void SetPricing(decimal unitPrice)
    {
        if (unitPrice < 0)
            throw new BusinessRuleValidationException("orderitem.price", "Unit price cannot be negative.");
        UnitPrice = Money.Of(unitPrice);
    }

    internal void SetQuantity(int quantity)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("orderitem.quantity", "Quantity must be positive.");
        Quantity = quantity;
    }
}
