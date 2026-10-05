using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// An ad-hoc charge line on an order added by staff at finalization (e.g.
/// "Customization × 10", "Design preparation"). Distinct from OrderServiceLine, which is
/// bound to the catalog; these carry no product/service reference, only a description,
/// unit price and quantity. Included in the order subtotal and its invoice.
/// </summary>
public sealed class OrderCustomLine : Entity
{
    private OrderCustomLine() { } // EF

    private OrderCustomLine(Guid orderId, string description, Money unitPrice, int quantity)
    {
        OrderId = orderId;
        Description = description;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }

    public Guid OrderId { get; private set; }
    public string Description { get; private set; } = default!;
    public Money UnitPrice { get; private set; } = default!;
    public int Quantity { get; private set; }

    public Money LineTotal => UnitPrice.Multiply(Quantity);

    public static OrderCustomLine Create(Guid orderId, string description, decimal unitPrice, int quantity)
    {
        if (orderId == Guid.Empty)
            throw new BusinessRuleValidationException("order.customline.order", "An order is required.");
        if (string.IsNullOrWhiteSpace(description))
            throw new BusinessRuleValidationException("order.customline.description", "A description is required.");
        if (unitPrice < 0)
            throw new BusinessRuleValidationException("order.customline.price", "Unit price cannot be negative.");
        if (quantity < 1)
            throw new BusinessRuleValidationException("order.customline.quantity", "Quantity must be at least 1.");
        return new OrderCustomLine(orderId, description.Trim(), Money.Of(unitPrice), quantity);
    }

    internal void Update(decimal unitPrice, int quantity)
    {
        if (unitPrice < 0)
            throw new BusinessRuleValidationException("order.customline.price", "Unit price cannot be negative.");
        if (quantity < 1)
            throw new BusinessRuleValidationException("order.customline.quantity", "Quantity must be at least 1.");
        UnitPrice = Money.Of(unitPrice);
        Quantity = quantity;
    }
}
