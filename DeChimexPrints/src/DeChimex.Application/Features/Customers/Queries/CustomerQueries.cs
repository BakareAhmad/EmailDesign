using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Customers;

namespace DeChimex.Application.Features.Customers.Queries;

public sealed record SearchCustomersQuery(PagedRequest Request) : IQuery<PagedResult<CustomerDto>>;
public sealed class SearchCustomersHandler(ICustomerQueries q) : IQueryHandler<SearchCustomersQuery, PagedResult<CustomerDto>>
{
    public Task<PagedResult<CustomerDto>> Handle(SearchCustomersQuery query, CancellationToken ct) => q.SearchAsync(query.Request, ct);
}

public sealed record GetCustomerByIdQuery(Guid Id) : IQuery<CustomerDto?>;
public sealed class GetCustomerByIdHandler(ICustomerQueries q) : IQueryHandler<GetCustomerByIdQuery, CustomerDto?>
{
    public Task<CustomerDto?> Handle(GetCustomerByIdQuery query, CancellationToken ct) => q.GetByIdAsync(query.Id, ct);
}
