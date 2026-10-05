using DeChimex.Application.Common;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Categories;
using DeChimex.Contracts.Products;
using DeChimex.Contracts.Services;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

public sealed class CategoryQueries(DeChimexDbContext db, ICacheService cache)
    : Application.Features.Categories.Queries.ICategoryQueries
{
    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(10);

    // MED-004: categories rarely change but are read on most storefront pages -> cache the list.
    public Task<IReadOnlyList<CategoryDto>> GetAllAsync(bool activeOnly, CancellationToken ct)
        => cache.GetOrCreateAsync(CacheKeys.CategoriesAll(activeOnly),
            token => QueryAllAsync(activeOnly, token), ListTtl, ct);

    private async Task<IReadOnlyList<CategoryDto>> QueryAllAsync(bool activeOnly, CancellationToken ct)
    {
        var q = db.Categories.AsNoTracking();
        if (activeOnly) q = q.Where(c => c.IsActive);
        return await q.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Slug.Value, c.Description,
                c.DisplayOrder, c.IsActive, db.Products.Count(p => p.CategoryId == c.Id), c.MajorCategoryId, c.MajorCategory == null ? null : c.MajorCategory.Name))
            .ToListAsync(ct);
    }

    public async Task<CategoryDto?> GetByIdAsync(Guid id, CancellationToken ct)
        => await db.Categories.AsNoTracking().Where(c => c.Id == id)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Slug.Value, c.Description,
                c.DisplayOrder, c.IsActive, db.Products.Count(p => p.CategoryId == c.Id), c.MajorCategoryId, c.MajorCategory == null ? null : c.MajorCategory.Name))
            .FirstOrDefaultAsync(ct);
}

public sealed class ProductQueries(DeChimexDbContext db) : IProductQueries
{
    public async Task<PagedResult<ProductSummaryDto>> SearchAsync(ProductFilter filter, CancellationToken ct)
    {
        var q = db.Products.AsNoTracking()
            .Join(db.Categories, p => p.CategoryId, c => c.Id, (p, c) => new { p, c })
            .GroupJoin(db.Inventories, x => x.p.Id, i => i.ProductId, (x, invs) => new { x.p, x.c, invs })
            .SelectMany(x => x.invs.DefaultIfEmpty(), (x, inv) => new { x.p, x.c, inv });

        // ActiveOnly is the public-storefront mode: hide inactive products AND products
        // whose category has been deactivated (the category nav is already hidden via
        // GetCategoriesQuery(ActiveOnly: true), so the grid must agree).
        if (filter.ActiveOnly) q = q.Where(x => x.p.IsActive && x.c.IsActive);
        if (filter.MajorCategoryId is not null)
            q = q.Where(x => x.c.MajorCategoryId == filter.MajorCategoryId && db.MajorCategories.Any(m => m.Id == x.c.MajorCategoryId));
        if (filter.CategoryId is not null) q = q.Where(x => x.p.CategoryId == filter.CategoryId);
        if (filter.IsFeatured is not null) q = q.Where(x => x.p.IsFeatured == filter.IsFeatured);
        if (filter.MinPrice is not null) q = q.Where(x => x.p.SellingPrice.Amount >= filter.MinPrice);
        if (filter.MaxPrice is not null) q = q.Where(x => x.p.SellingPrice.Amount <= filter.MaxPrice);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(x => EF.Functions.Like(x.p.Name, $"%{s}%")
                          || EF.Functions.Like(x.p.Sku, $"%{s}%")
                          || (x.p.Description != null && EF.Functions.Like(x.p.Description, $"%{s}%")));
        }

        q = (filter.SortBy?.ToLowerInvariant()) switch
        {
            "price" => filter.SortDescending ? q.OrderByDescending(x => x.p.SellingPrice.Amount).ThenBy(x => x.p.Id) : q.OrderBy(x => x.p.SellingPrice.Amount).ThenBy(x => x.p.Id),
            "name" => filter.SortDescending ? q.OrderByDescending(x => x.p.Name) : q.OrderBy(x => x.p.Name),
            _ => q.OrderByDescending(x => x.p.IsFeatured).ThenByDescending(x => x.p.CreatedDateUtc)
        };

        var total = await q.CountAsync(ct);
        var items = await q.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(x => new ProductSummaryDto(
                x.p.Id, x.p.Name, x.p.Sku, x.p.Slug.Value, x.p.SellingPrice.Amount, x.p.SellingPrice.Currency,
                x.p.CategoryId, x.c.Name, x.p.IsFeatured, x.p.IsActive,
                x.inv == null ? 0 : x.inv.QuantityOnHand - x.inv.QuantityReserved,
                x.inv != null && (x.inv.QuantityOnHand - x.inv.QuantityReserved) <= x.inv.LowStockThreshold,
                x.inv == null || (x.inv.QuantityOnHand - x.inv.QuantityReserved) <= 0,
                x.p.Images.Where(im => im.IsPrimary).Select(im => im.Url).FirstOrDefault(),
                x.p.IsCustomizable,
                db.CategoryOptions.Any(o => o.CategoryId == x.p.CategoryId && o.IsActive)))
            .ToListAsync(ct);

        return new PagedResult<ProductSummaryDto>(items, total, filter.Page, filter.PageSize);
    }

    public async Task<ProductDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var result = await db.Products.AsNoTracking()
            .Where(p => p.Id == id)
            .Join(db.Categories, p => p.CategoryId, c => c.Id, (p, c) => new { p, c })
            .GroupJoin(db.Inventories, x => x.p.Id, i => i.ProductId, (x, invs) => new { x.p, x.c, invs })
            .SelectMany(x => x.invs.DefaultIfEmpty(), (x, inv) => new { x.p, x.c, inv })
            .Select(x => new
            {
                x.p, CategoryName = x.c.Name,
                Available = x.inv == null ? 0 : x.inv.QuantityOnHand - x.inv.QuantityReserved,
                Threshold = x.inv == null ? 0 : x.inv.LowStockThreshold,
                Images = x.p.Images.OrderBy(im => im.DisplayOrder)
                    .Select(im => new ProductImageDto(im.Id, im.Url, im.AltText, im.IsPrimary, im.DisplayOrder)).ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (result is null) return null;
        var p = result.p;
        return new ProductDto(p.Id, p.Name, p.Sku, p.Slug.Value, p.Description, p.CategoryId, result.CategoryName,
            p.SellingPrice.Amount, p.SellingPrice.Currency, p.IsCustomizable, p.IsFeatured, p.IsActive,
            result.Available, result.Threshold, result.Available <= result.Threshold, result.Available <= 0, result.Images);
    }

    public async Task<IReadOnlyList<ProductSummaryDto>> GetFeaturedAsync(int take, CancellationToken ct)
    {
        var filter = new ProductFilter { IsFeatured = true, ActiveOnly = true, Page = 1, PageSize = take };
        var page = await SearchAsync(filter, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<string>> GetSkusAsync(CancellationToken ct)
        => await db.Products.AsNoTracking().OrderBy(p => p.Sku).Select(p => p.Sku).ToListAsync(ct);
}

public sealed class ServiceQueries(DeChimexDbContext db, ICacheService cache) : IServiceQueries
{
    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(10);

    // MED-004: services rarely change but are read on the storefront -> cache the list.
    public Task<IReadOnlyList<ServiceDto>> GetAllAsync(bool activeOnly, CancellationToken ct)
        => cache.GetOrCreateAsync(CacheKeys.ServicesAll(activeOnly),
            token => QueryAllAsync(activeOnly, token), ListTtl, ct);

    private async Task<IReadOnlyList<ServiceDto>> QueryAllAsync(bool activeOnly, CancellationToken ct)
    {
        var q = db.Services.AsNoTracking();
        if (activeOnly) q = q.Where(s => s.IsActive);
        return await q.OrderBy(s => s.Name)
            .Select(s => new ServiceDto(s.Id, s.Name, s.Slug.Value, s.Description, s.BasePrice.Amount,
                s.BasePrice.Currency, s.IconColor, s.IsActive, s.IsFeatured)).ToListAsync(ct);
    }

    public async Task<ServiceDto?> GetByIdAsync(Guid id, CancellationToken ct)
        => await db.Services.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new ServiceDto(s.Id, s.Name, s.Slug.Value, s.Description, s.BasePrice.Amount,
                s.BasePrice.Currency, s.IconColor, s.IsActive, s.IsFeatured)).FirstOrDefaultAsync(ct);
}
