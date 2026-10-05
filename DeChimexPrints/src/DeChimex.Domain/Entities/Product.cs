using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A finished product sold by the business. Aggregate root owning its images.
/// Stock levels live on the associated <see cref="Inventory"/> aggregate so that
/// inventory movements remain the single source of truth.
/// </summary>
public sealed class Product : AggregateRoot
{
    private readonly List<ProductImage> _images = new();

    private Product() { } // EF

    private Product(string name, string sku, string? description, Guid categoryId,
        Money sellingPrice, bool isCustomizable, bool isFeatured, Slug? slug = null)
    {
        Name = name;
        Sku = sku;
        // The caller may pass an already uniqueness-resolved slug (see the create/update
        // handlers); otherwise it is derived from the name.
        Slug = slug ?? Slug.FromText(name);
        Description = description;
        CategoryId = categoryId;
        SellingPrice = sellingPrice;
        IsCustomizable = isCustomizable;
        IsFeatured = isFeatured;
        IsActive = true;
    }

    public string Name { get; private set; } = default!;
    public string Sku { get; private set; } = default!;
    public Slug Slug { get; private set; } = default!;
    public string? Description { get; private set; }
    public Guid CategoryId { get; private set; }
    public Category? Category { get; private set; }
    public Money SellingPrice { get; private set; } = default!;
    public bool IsCustomizable { get; private set; }
    public bool IsFeatured { get; private set; }
    public bool IsActive { get; private set; }

    public IReadOnlyCollection<ProductImage> Images => _images.AsReadOnly();

    public static Product Create(string name, string sku, string? description, Guid categoryId,
        decimal sellingPrice, bool isCustomizable = false, bool isFeatured = false, Slug? slug = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("product.name", "Product name is required.");
        if (string.IsNullOrWhiteSpace(sku))
            throw new BusinessRuleValidationException("product.sku", "SKU is required.");
        if (categoryId == Guid.Empty)
            throw new BusinessRuleValidationException("product.category", "A valid category is required.");
        if (sellingPrice <= 0)
            throw new BusinessRuleValidationException("product.price", "Selling price must be greater than zero.");

        return new Product(name.Trim(), sku.Trim().ToUpperInvariant(), description?.Trim(), categoryId,
            Money.Of(sellingPrice), isCustomizable, isFeatured, slug);
    }

    public void Update(string name, string? description, Guid categoryId, decimal sellingPrice,
        bool isCustomizable, bool isFeatured, Slug? slug = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("product.name", "Product name is required.");
        if (sellingPrice <= 0)
            throw new BusinessRuleValidationException("product.price", "Selling price must be greater than zero.");
        var trimmedName = name.Trim();
        // Slug rule: an unchanged name KEEPS the current slug (an ordinary price/detail
        // edit must never churn the product URL, let alone collide with another row);
        // a changed name uses the caller-resolved slug when provided, else derives one.
        Slug = slug ?? (trimmedName == Name ? Slug : Slug.FromText(trimmedName));
        Name = trimmedName;
        Description = description?.Trim();
        CategoryId = categoryId;
        SellingPrice = Money.Of(sellingPrice);
        IsCustomizable = isCustomizable;
        IsFeatured = isFeatured;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;

    public ProductImage AddImage(string url, string? altText = null, bool isPrimary = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new BusinessRuleValidationException("product.image.url", "Image URL is required.");

        // First image is always primary; enforce single primary.
        var makePrimary = isPrimary || _images.Count == 0;
        if (makePrimary)
            foreach (var img in _images) img.SetPrimary(false);

        var image = ProductImage.Create(Id, url.Trim(), altText, makePrimary, _images.Count);
        _images.Add(image);
        return image;
    }

    public void RemoveImage(Guid imageId)
    {
        var image = _images.FirstOrDefault(i => i.Id == imageId);
        if (image is null) return;
        // A product must never be left without an image (the storefront has no
        // placeholder fallback), so the last remaining image cannot be removed.
        if (_images.Count <= 1)
            throw new BusinessRuleValidationException(
                "product.image.last", "A product must keep at least one image. Add another image before removing this one.");
        _images.Remove(image);
        if (image.IsPrimary && _images.Count > 0)
            _images[0].SetPrimary(true);
    }

    public void SetImagePrimary(Guid imageId)
    {
        var image = _images.FirstOrDefault(i => i.Id == imageId);
        if (image is null)
            throw new BusinessRuleValidationException("product.image.notfound", "Image not found on this product.");
        foreach (var img in _images)
            img.SetPrimary(img.Id == imageId);
    }

    /// <summary>
    /// Re-applies the image order (carousel order). The first image in the new order
    /// becomes the primary/thumbnail image everywhere the product is shown.
    /// </summary>
    public void ReorderImages(IReadOnlyList<Guid> orderedImageIds)
    {
        if (orderedImageIds is null || orderedImageIds.Count != _images.Count
            || orderedImageIds.Distinct().Count() != _images.Count
            || orderedImageIds.Any(id => _images.All(i => i.Id != id)))
            throw new BusinessRuleValidationException("product.images.order",
                "The ordered image list must contain every image of this product exactly once.");

        var byId = _images.ToDictionary(i => i.Id);
        var reordered = orderedImageIds.Select(id => byId[id]).ToList();
        _images.Clear();
        for (var i = 0; i < reordered.Count; i++)
        {
            reordered[i].SetDisplayOrder(i);
            reordered[i].SetPrimary(i == 0);
            _images.Add(reordered[i]);
        }
    }
}
