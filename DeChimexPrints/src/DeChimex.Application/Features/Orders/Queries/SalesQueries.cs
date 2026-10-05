using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Orders;

namespace DeChimex.Application.Features.Orders.Queries;

public sealed record SearchSalesInboxQuery(SalesInboxFilter Filter) : IQuery<PagedResult<SalesInboxItemDto>>;
public sealed class SearchSalesInboxHandler(ISalesInboxQueries queries)
    : IQueryHandler<SearchSalesInboxQuery, PagedResult<SalesInboxItemDto>>
{
    public Task<PagedResult<SalesInboxItemDto>> Handle(SearchSalesInboxQuery q, CancellationToken ct)
        => queries.GetInboxAsync(q.Filter, ct);
}

public sealed record GetSalesBucketCountsQuery(Guid? StaffUserId) : IQuery<SalesBucketCounts>;
public sealed class GetSalesBucketCountsHandler(ISalesInboxQueries queries)
    : IQueryHandler<GetSalesBucketCountsQuery, SalesBucketCounts>
{
    public Task<SalesBucketCounts> Handle(GetSalesBucketCountsQuery q, CancellationToken ct)
        => queries.GetBucketCountsAsync(q.StaffUserId, ct);
}

public sealed record GetEligibleSalesStaffQuery() : IQuery<IReadOnlyList<SalesStaffOptionDto>>;
public sealed class GetEligibleSalesStaffHandler(ISalesInboxQueries queries)
    : IQueryHandler<GetEligibleSalesStaffQuery, IReadOnlyList<SalesStaffOptionDto>>
{
    public Task<IReadOnlyList<SalesStaffOptionDto>> Handle(GetEligibleSalesStaffQuery q, CancellationToken ct)
        => queries.GetEligibleSalesStaffAsync(ct);
}
