using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Inventory;

namespace DeChimex.Application.Features.Inventory.Queries;

public sealed record SearchInventoryQuery(PagedRequest Request) : IQuery<PagedResult<InventoryDto>>;
public sealed class SearchInventoryHandler(IInventoryQueries queries)
    : IQueryHandler<SearchInventoryQuery, PagedResult<InventoryDto>>
{
    public Task<PagedResult<InventoryDto>> Handle(SearchInventoryQuery query, CancellationToken ct)
        => queries.SearchAsync(query.Request, ct);
}

public sealed record GetInventoryHistoryQuery(Guid ProductId) : IQuery<IReadOnlyList<InventoryTransactionDto>>;
public sealed class GetInventoryHistoryHandler(IInventoryQueries queries)
    : IQueryHandler<GetInventoryHistoryQuery, IReadOnlyList<InventoryTransactionDto>>
{
    public Task<IReadOnlyList<InventoryTransactionDto>> Handle(GetInventoryHistoryQuery query, CancellationToken ct)
        => queries.GetHistoryAsync(query.ProductId, ct);
}

public sealed record GetLowStockQuery : IQuery<IReadOnlyList<InventoryDto>>;
public sealed class GetLowStockHandler(IInventoryQueries queries)
    : IQueryHandler<GetLowStockQuery, IReadOnlyList<InventoryDto>>
{
    public Task<IReadOnlyList<InventoryDto>> Handle(GetLowStockQuery query, CancellationToken ct)
        => queries.GetLowStockAsync(ct);
}
