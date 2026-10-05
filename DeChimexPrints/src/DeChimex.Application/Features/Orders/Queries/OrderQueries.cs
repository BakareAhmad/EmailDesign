using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Orders;

namespace DeChimex.Application.Features.Orders.Queries;

public sealed record SearchOrdersQuery(OrderFilter Filter) : IQuery<PagedResult<OrderSummaryDto>>;
public sealed class SearchOrdersHandler(IOrderQueries queries)
    : IQueryHandler<SearchOrdersQuery, PagedResult<OrderSummaryDto>>
{
    public Task<PagedResult<OrderSummaryDto>> Handle(SearchOrdersQuery q, CancellationToken ct)
        => queries.SearchAsync(q.Filter, ct);
}

public sealed record GetOrderByIdQuery(Guid Id) : IQuery<OrderDto?>;
public sealed class GetOrderByIdHandler(IOrderQueries queries)
    : IQueryHandler<GetOrderByIdQuery, OrderDto?>
{
    public Task<OrderDto?> Handle(GetOrderByIdQuery q, CancellationToken ct) => queries.GetByIdAsync(q.Id, ct);
}

public sealed record GetOrderByNumberQuery(string OrderNumber) : IQuery<OrderDto?>;
public sealed class GetOrderByNumberHandler(IOrderQueries queries)
    : IQueryHandler<GetOrderByNumberQuery, OrderDto?>
{
    public Task<OrderDto?> Handle(GetOrderByNumberQuery q, CancellationToken ct) => queries.GetByNumberAsync(q.OrderNumber, ct);
}

public sealed record GetMyOrdersQuery(Guid CustomerId) : IQuery<IReadOnlyList<OrderSummaryDto>>;
public sealed class GetMyOrdersHandler(IOrderQueries queries)
    : IQueryHandler<GetMyOrdersQuery, IReadOnlyList<OrderSummaryDto>>
{
    public Task<IReadOnlyList<OrderSummaryDto>> Handle(GetMyOrdersQuery q, CancellationToken ct)
        => queries.GetForCustomerAsync(q.CustomerId, ct);
}
