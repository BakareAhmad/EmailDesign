namespace DeChimex.Contracts.Products;

public sealed record ProductImageDto(Guid Id, string Url, string? AltText, bool IsPrimary, int DisplayOrder);

public sealed record ProductDto(
    Guid Id, string Name, string Sku, string Slug, string? Description,
    Guid CategoryId, string CategoryName, decimal SellingPrice, string Currency,
    bool IsCustomizable, bool IsFeatured, bool IsActive,
    int AvailableQuantity, int LowStockThreshold, bool IsLowStock, bool IsOutOfStock,
    IReadOnlyList<ProductImageDto> Images);

public sealed record ProductSummaryDto(
    Guid Id, string Name, string Sku, string Slug, decimal SellingPrice, string Currency,
    Guid CategoryId, string CategoryName, bool IsFeatured, bool IsActive,
    int AvailableQuantity, bool IsLowStock, bool IsOutOfStock, string? PrimaryImageUrl,
    // Capability flags (storefront cards): a product is "made to order" when it is
    // customizable OR its category has active ordering options — the same rule the
    // product page uses to choose "Add to cart" vs "Customize" (UseCustomOrder).
    bool IsCustomizable = false, bool CategoryHasOptions = false)
{
    public bool IsMadeToOrder => IsCustomizable || CategoryHasOptions;
}

public sealed record CreateProductRequest(
    string Name, string Sku, string? Description, Guid CategoryId,
    decimal SellingPrice, bool IsCustomizable, bool IsFeatured,
    int InitialQuantity, int LowStockThreshold);

public sealed record UpdateProductRequest(
    string Name, string? Description, Guid CategoryId,
    decimal SellingPrice, bool IsCustomizable, bool IsFeatured);
