using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Marketing;
using DeChimex.Domain.Repositories;

namespace DeChimex.Application.Features.Marketing.Queries;

public sealed record GetMarketingBroadcastsQuery(PagedRequest Request) : IQuery<PagedResult<MarketingBroadcastDto>>;

public sealed class GetMarketingBroadcastsHandler(IMarketingQueries queries)
    : IQueryHandler<GetMarketingBroadcastsQuery, PagedResult<MarketingBroadcastDto>>
{
    public Task<PagedResult<MarketingBroadcastDto>> Handle(GetMarketingBroadcastsQuery query, CancellationToken ct)
        => queries.GetBroadcastsAsync(query.Request, ct);
}

public sealed record GetMarketingBroadcastDetailsQuery(Guid Id) : IQuery<MarketingBroadcastDetailsDto?>;

public sealed class GetMarketingBroadcastDetailsHandler(IMarketingQueries queries)
    : IQueryHandler<GetMarketingBroadcastDetailsQuery, MarketingBroadcastDetailsDto?>
{
    public Task<MarketingBroadcastDetailsDto?> Handle(GetMarketingBroadcastDetailsQuery query, CancellationToken ct)
        => queries.GetDetailsAsync(query.Id, ct);
}

/// <summary>Eligible-recipient count shown on the compose form BEFORE sending.</summary>
public sealed record GetMarketingRecipientCountQuery : IQuery<int>;

public sealed class GetMarketingRecipientCountHandler(IMarketingQueries queries)
    : IQueryHandler<GetMarketingRecipientCountQuery, int>
{
    public Task<int> Handle(GetMarketingRecipientCountQuery query, CancellationToken ct)
        => queries.GetEligibleRecipientCountAsync(ct);
}

/// <summary>
/// Server-rendered preview of a broadcast exactly as recipients will receive it
/// (sample name + a harmless unsubscribe link placeholder).
/// </summary>
public sealed record PreviewMarketingBroadcastQuery(Guid Id) : IQuery<string>;

public sealed class PreviewMarketingBroadcastHandler(
    IMarketingRepository broadcasts,
    IEmailTemplateRenderer renderer,
    IAppUrlProvider urls)
    : IQueryHandler<PreviewMarketingBroadcastQuery, string>
{
    public async Task<string> Handle(PreviewMarketingBroadcastQuery query, CancellationToken ct)
    {
        var broadcast = await broadcasts.GetByIdAsync(query.Id, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing broadcast", query.Id);

        var content = renderer.RenderMarketing(broadcast.Subject, broadcast.HtmlContent,
            new Dictionary<string, string>
            {
                ["FirstName"] = "Alex",
                ["UnsubscribeLink"] = urls.BuildMarketingUnsubscribeLink(Guid.Empty)
            });
        return content.HtmlBody;
    }
}
