using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Categories;

namespace DeChimex.Application.Features.Categories.Queries;

public sealed record GetCategoriesQuery(bool ActiveOnly = false) : IQuery<IReadOnlyList<CategoryDto>>;

/// <summary>Read side reads through a dedicated query service to avoid leaking IQueryable.</summary>
public interface ICategoryQueries
{
    Task<IReadOnlyList<CategoryDto>> GetAllAsync(bool activeOnly, CancellationToken ct);
    Task<CategoryDto?> GetByIdAsync(Guid id, CancellationToken ct);
}

public sealed class GetCategoriesHandler(ICategoryQueries queries)
    : IQueryHandler<GetCategoriesQuery, IReadOnlyList<CategoryDto>>
{
    public Task<IReadOnlyList<CategoryDto>> Handle(GetCategoriesQuery query, CancellationToken ct)
        => queries.GetAllAsync(query.ActiveOnly, ct);
}
