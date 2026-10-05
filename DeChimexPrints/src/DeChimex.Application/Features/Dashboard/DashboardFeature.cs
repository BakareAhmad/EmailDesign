using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Dashboard;

namespace DeChimex.Application.Features.Dashboard;

public sealed record GetDashboardKpisQuery : IQuery<DashboardKpiDto>;
public sealed class GetDashboardKpisHandler(IDashboardQueries q) : IQueryHandler<GetDashboardKpisQuery, DashboardKpiDto>
{
    public Task<DashboardKpiDto> Handle(GetDashboardKpisQuery query, CancellationToken ct) => q.GetKpisAsync(ct);
}

public sealed record GetRevenueTrendQuery(int Months = 8) : IQuery<IReadOnlyList<RevenuePointDto>>;
public sealed class GetRevenueTrendHandler(IDashboardQueries q) : IQueryHandler<GetRevenueTrendQuery, IReadOnlyList<RevenuePointDto>>
{
    public Task<IReadOnlyList<RevenuePointDto>> Handle(GetRevenueTrendQuery query, CancellationToken ct) => q.GetRevenueTrendAsync(query.Months, ct);
}

public sealed record GetSalesByCategoryQuery : IQuery<IReadOnlyList<CategorySalesDto>>;
public sealed class GetSalesByCategoryHandler(IDashboardQueries q) : IQueryHandler<GetSalesByCategoryQuery, IReadOnlyList<CategorySalesDto>>
{
    public Task<IReadOnlyList<CategorySalesDto>> Handle(GetSalesByCategoryQuery query, CancellationToken ct) => q.GetSalesByCategoryAsync(ct);
}
