using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>An image belonging to a product. Child entity of the Product aggregate.</summary>
public sealed class ProductImage : Entity
{
    private ProductImage() { } // EF

    private ProductImage(Guid productId, string url, string? altText, bool isPrimary, int displayOrder)
    {
        ProductId = productId;
        Url = url;
        AltText = altText;
        IsPrimary = isPrimary;
        DisplayOrder = displayOrder;
    }

    public Guid ProductId { get; private set; }
    public string Url { get; private set; } = default!;
    public string? AltText { get; private set; }
    public bool IsPrimary { get; private set; }
    public int DisplayOrder { get; private set; }

    internal static ProductImage Create(Guid productId, string url, string? altText, bool isPrimary, int displayOrder)
        => new(productId, url, altText, isPrimary, displayOrder);

    internal void SetPrimary(bool primary) => IsPrimary = primary;
    internal void SetDisplayOrder(int displayOrder) => DisplayOrder = displayOrder;
}
