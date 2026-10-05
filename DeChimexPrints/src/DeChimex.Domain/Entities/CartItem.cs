using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>A line in a shopping cart (product or service). Child of ShoppingCart.</summary>
public sealed class CartItem : Entity
{
    private CartItem() { } // EF
    private CartItem(Guid cartId, OrderLineType lineType, Guid referenceId, int quantity, string? notes)
    {
        CartId = cartId;
        LineType = lineType;
        ReferenceId = referenceId;
        Quantity = quantity;
        Notes = notes;
    }

    public Guid CartId { get; private set; }
    public OrderLineType LineType { get; private set; }
    public Guid ReferenceId { get; private set; } // ProductId or ServiceId
    public int Quantity { get; private set; }
    public string? Notes { get; private set; }

    internal static CartItem Create(Guid cartId, OrderLineType lineType, Guid referenceId, int quantity, string? notes)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("cartitem.quantity", "Quantity must be positive.");
        return new CartItem(cartId, lineType, referenceId, quantity, notes);
    }

    internal void SetQuantity(int quantity)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("cartitem.quantity", "Quantity must be positive.");
        Quantity = quantity;
    }
}
