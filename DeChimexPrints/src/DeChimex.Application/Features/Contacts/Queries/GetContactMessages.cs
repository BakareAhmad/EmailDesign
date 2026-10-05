using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Contacts;

namespace DeChimex.Application.Features.Contacts.Queries;

public sealed record GetContactMessagesQuery(PagedRequest Request, bool UnresolvedOnly) : IQuery<PagedResult<ContactMessageDto>>;

public sealed class GetContactMessagesHandler(IContactMessageQueries queries)
    : IQueryHandler<GetContactMessagesQuery, PagedResult<ContactMessageDto>>
{
    public Task<PagedResult<ContactMessageDto>> Handle(GetContactMessagesQuery query, CancellationToken ct)
        => queries.SearchAsync(query.Request, query.UnresolvedOnly, ct);
}
