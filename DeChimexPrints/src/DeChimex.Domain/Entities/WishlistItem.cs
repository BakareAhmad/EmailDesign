using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>A saved (favourite) product for a customer.</summary>
public sealed class WishlistItem : AggregateRoot
{
    private WishlistItem() { } // EF
    private WishlistItem(Guid customerId, Guid productId)
    {
        CustomerId = customerId;
        ProductId = productId;
    }

    public Guid CustomerId { get; private set; }
    public Guid ProductId { get; private set; }

    public static WishlistItem Create(Guid customerId, Guid productId) => new(customerId, productId);
}
