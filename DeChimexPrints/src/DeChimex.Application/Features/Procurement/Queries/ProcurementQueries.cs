using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Procurement;

namespace DeChimex.Application.Features.Procurement.Queries;

public sealed record SearchProcurementsQuery(PagedRequest Request) : IQuery<PagedResult<ProcurementDto>>;
public sealed class SearchProcurementsHandler(IProcurementQueries q) : IQueryHandler<SearchProcurementsQuery, PagedResult<ProcurementDto>>
{
    public Task<PagedResult<ProcurementDto>> Handle(SearchProcurementsQuery query, CancellationToken ct) => q.SearchAsync(query.Request, ct);
}

public sealed record GetProcurementByIdQuery(Guid Id) : IQuery<ProcurementDto?>;
public sealed class GetProcurementByIdHandler(IProcurementQueries q) : IQueryHandler<GetProcurementByIdQuery, ProcurementDto?>
{
    public Task<ProcurementDto?> Handle(GetProcurementByIdQuery query, CancellationToken ct) => q.GetByIdAsync(query.Id, ct);
}
