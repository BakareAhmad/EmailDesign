using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Categories;

namespace DeChimex.Application.Features.Categories.Queries;

public sealed record GetCategoryByIdQuery(Guid Id) : IQuery<CategoryDto?>;

public sealed class GetCategoryByIdHandler(ICategoryQueries queries)
    : IQueryHandler<GetCategoryByIdQuery, CategoryDto?>
{
    public Task<CategoryDto?> Handle(GetCategoryByIdQuery query, CancellationToken ct)
        => queries.GetByIdAsync(query.Id, ct);
}
