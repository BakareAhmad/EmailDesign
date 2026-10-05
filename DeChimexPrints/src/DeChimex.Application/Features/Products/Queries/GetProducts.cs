using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Products;

namespace DeChimex.Application.Features.Products.Queries;

public sealed record SearchProductsQuery(ProductFilter Filter) : IQuery<PagedResult<ProductSummaryDto>>;
public sealed class SearchProductsHandler(IProductQueries queries)
    : IQueryHandler<SearchProductsQuery, PagedResult<ProductSummaryDto>>
{
    public Task<PagedResult<ProductSummaryDto>> Handle(SearchProductsQuery query, CancellationToken ct)
        => queries.SearchAsync(query.Filter, ct);
}

public sealed record GetProductByIdQuery(Guid Id) : IQuery<ProductDto?>;
public sealed class GetProductByIdHandler(IProductQueries queries)
    : IQueryHandler<GetProductByIdQuery, ProductDto?>
{
    public Task<ProductDto?> Handle(GetProductByIdQuery query, CancellationToken ct)
        => queries.GetByIdAsync(query.Id, ct);
}

public sealed record GetFeaturedProductsQuery(int Take = 8) : IQuery<IReadOnlyList<ProductSummaryDto>>;
public sealed class GetFeaturedProductsHandler(IProductQueries queries)
    : IQueryHandler<GetFeaturedProductsQuery, IReadOnlyList<ProductSummaryDto>>
{
    public Task<IReadOnlyList<ProductSummaryDto>> Handle(GetFeaturedProductsQuery query, CancellationToken ct)
        => queries.GetFeaturedAsync(query.Take, ct);
}

/// <summary>Returns every existing product SKU (uppercase), used by the admin create form to
/// keep live SKU suggestions unique before submission.</summary>
public sealed record GetAllProductSkusQuery : IQuery<IReadOnlyList<string>>;
public sealed class GetAllProductSkusHandler(IProductQueries queries)
    : IQueryHandler<GetAllProductSkusQuery, IReadOnlyList<string>>
{
    public Task<IReadOnlyList<string>> Handle(GetAllProductSkusQuery query, CancellationToken ct)
        => queries.GetSkusAsync(ct);
}
