using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Services;

namespace DeChimex.Application.Features.Services.Queries;

public sealed record GetServicesQuery(bool ActiveOnly = false) : IQuery<IReadOnlyList<ServiceDto>>;
public sealed class GetServicesHandler(IServiceQueries queries)
    : IQueryHandler<GetServicesQuery, IReadOnlyList<ServiceDto>>
{
    public Task<IReadOnlyList<ServiceDto>> Handle(GetServicesQuery query, CancellationToken ct)
        => queries.GetAllAsync(query.ActiveOnly, ct);
}

public sealed record GetServiceByIdQuery(Guid Id) : IQuery<ServiceDto?>;
public sealed class GetServiceByIdHandler(IServiceQueries queries)
    : IQueryHandler<GetServiceByIdQuery, ServiceDto?>
{
    public Task<ServiceDto?> Handle(GetServiceByIdQuery query, CancellationToken ct)
        => queries.GetByIdAsync(query.Id, ct);
}
