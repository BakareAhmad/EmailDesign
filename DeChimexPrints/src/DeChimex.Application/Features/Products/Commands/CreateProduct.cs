using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Products;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Domain.ValueObjects;
using DeChimex.Shared.Constants;
using FluentValidation;

namespace DeChimex.Application.Features.Products.Commands;

public sealed record CreateProductCommand(CreateProductRequest Request) : ICommand<ProductDto>;

public sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(200);
        // SKU is auto-suggested from the name; when supplied it must match the exact
        // 9-char "XXX-XXXXX" format (a blank value is fine — the handler generates one).
        When(x => !string.IsNullOrWhiteSpace(x.Request.Sku), () =>
        {
            RuleFor(x => x.Request.Sku)
                .Must(ProductSkuGenerator.IsValidFormat)
                .WithMessage("SKU must be exactly 9 characters in the form XXX-XXXXX (e.g. PBC-7K4M2).");
        });
        RuleFor(x => x.Request.Description).MaximumLength(4000);
        RuleFor(x => x.Request.CategoryId).NotEmpty();
        RuleFor(x => x.Request.SellingPrice).GreaterThan(0)
            .WithMessage("Selling price must be greater than zero.");
        RuleFor(x => x.Request.InitialQuantity).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.LowStockThreshold).GreaterThanOrEqualTo(0);
    }
}

public sealed class CreateProductHandler(
    IProductRepository products,
    ICategoryRepository categories,
    IInventoryRepository inventories,
    IUnitOfWork uow,
    DeChimex.Application.Common.Interfaces.ICacheService cache) : ICommandHandler<CreateProductCommand, ProductDto>
{
    public async Task<ProductDto> Handle(CreateProductCommand command, CancellationToken ct)
    {
        var r = command.Request;

        var category = await categories.GetByIdAsync(r.CategoryId, ct)
            ?? throw new NotFoundException("Category", r.CategoryId);

        // Resolve the final SKU: a blank one is auto-generated from the name, and any
        // collision (against a generated or user-supplied value) is auto-resolved with a
        // fresh unique suffix so the user never has to invent a SKU or fix a conflict.
        var requested = r.Sku?.Trim() ?? string.Empty;
        var finalSku = await ResolveSkuAsync(r.Name, requested, null, ct);

        // Same resolution for the slug (unique index on Products.Slug is enforced by SQL
        // Server at INSERT): two same-named products must both be creatable.
        var desiredSlug = Slug.FromText(r.Name);
        var slugCandidate = desiredSlug;
        var slugN = 2;
        while (await products.SlugExistsAsync(slugCandidate.Value, null, ct))
            slugCandidate = Slug.FromText($"{desiredSlug.Value}-{slugN++}");

        var product = Product.Create(r.Name, finalSku, r.Description, r.CategoryId, r.SellingPrice,
            r.IsCustomizable, r.IsFeatured, slugCandidate);
        await products.AddAsync(product, ct);

        // Every product gets a paired inventory record (single source of truth for stock).
        var inventory = DeChimex.Domain.Entities.Inventory.Create(product.Id, product.Name, r.InitialQuantity, r.LowStockThreshold);
        await inventories.AddAsync(inventory, ct);

        await uow.SaveChangesAsync(ct);

        // MED-004: the cached category lists carry a per-category product count, so adding a
        // product changes those counts, so invalidate both list variants exactly as the category
        // command handlers do, otherwise the storefront/admin counts stay stale until TTL expiry.
        foreach (var key in DeChimex.Application.Common.CacheKeys.CategoryLists) await cache.RemoveAsync(key, ct);

        return new ProductDto(product.Id, product.Name, product.Sku, product.Slug.Value, product.Description,
            product.CategoryId, category.Name, product.SellingPrice.Amount, product.SellingPrice.Currency,
            product.IsCustomizable, product.IsFeatured, product.IsActive,
            inventory.Available, inventory.LowStockThreshold, inventory.IsLowStock, inventory.IsOutOfStock,
            Array.Empty<ProductImageDto>());
    }

    private async Task<string> ResolveSkuAsync(string name, string requested, Guid? excludeId, CancellationToken ct)
    {
        // Auto-generate when the caller left it blank.
        if (string.IsNullOrWhiteSpace(requested))
            return await SuggestUniqueAsync(name, excludeId, ct);

        // Whatever arrives here has already passed the strict-format validator, but guard anyway.
        if (!ProductSkuGenerator.IsValidFormat(requested))
            requested = await SuggestUniqueAsync(name, excludeId, ct);

        if (!await products.SkuExistsAsync(requested, excludeId, ct))
            return requested;

        // Collision: keep the name-derived (or supplied) prefix but pick a new unique suffix.
        return await SuggestKeepingPrefixAsync(name, requested, excludeId, ct);
    }

    private async Task<string> SuggestUniqueAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            var candidate = ProductSkuGenerator.Suggest(name);
            if (!await products.SkuExistsAsync(candidate, excludeId, ct))
                return candidate;
        }
        throw new ConflictException("product.sku", "Could not generate a unique SKU. Please try again.");
    }

    private async Task<string> SuggestKeepingPrefixAsync(string name, string preferred, Guid? excludeId, CancellationToken ct)
    {
        var prefix = preferred.Length >= ProductSkuGenerator.PrefixLength
            ? preferred[..ProductSkuGenerator.PrefixLength]
            : ProductSkuGenerator.BuildPrefix(name);
        for (var i = 0; i < 50; i++)
        {
            var candidate = $"{prefix}-{ProductSkuGenerator.GenerateSuffix()}";
            if (!await products.SkuExistsAsync(candidate, excludeId, ct))
                return candidate;
        }
        throw new ConflictException("product.sku", "Could not generate a unique SKU. Please try again.");
    }
}
