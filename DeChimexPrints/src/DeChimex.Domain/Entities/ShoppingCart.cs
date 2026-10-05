using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;

namespace DeChimex.Domain.Entities;

/// <summary>Persistent shopping cart aggregate for a customer. Owns its cart items.</summary>
public sealed class ShoppingCart : AggregateRoot
{
    private readonly List<CartItem> _items = new();

    private ShoppingCart() { } // EF
    private ShoppingCart(Guid customerId) => CustomerId = customerId;

    public Guid CustomerId { get; private set; }
    public IReadOnlyCollection<CartItem> Items => _items.AsReadOnly();

    public static ShoppingCart Create(Guid customerId) => new(customerId);

    public CartItem AddOrUpdate(OrderLineType lineType, Guid referenceId, int quantity, string? notes = null)
    {
        var existing = _items.FirstOrDefault(i => i.LineType == lineType && i.ReferenceId == referenceId);
        if (existing is not null)
        {
            // "Add to cart" semantics: top up an existing line (mirrors the guest localStorage cart,
            // which increments). Explicit quantity edits use UpdateQuantity (set) on the cart page.
            existing.SetQuantity(existing.Quantity + quantity);
            return existing;
        }
        var item = CartItem.Create(Id, lineType, referenceId, quantity, notes);
        _items.Add(item);
        return item;
    }

    public void UpdateQuantity(Guid itemId, int quantity)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        item?.SetQuantity(quantity);
    }

    public void Remove(Guid itemId)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item is not null) _items.Remove(item);
    }

    public void Clear() => _items.Clear();
}
