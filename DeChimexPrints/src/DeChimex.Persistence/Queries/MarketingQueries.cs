using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Marketing;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

public sealed class MarketingQueries(DeChimexDbContext db) : IMarketingQueries
{
    public async Task<PagedResult<MarketingBroadcastDto>> GetBroadcastsAsync(PagedRequest request, CancellationToken ct)
    {
        var query = db.MarketingBroadcasts.AsNoTracking().OrderByDescending(b => b.CreatedDateUtc);

        var total = await query.CountAsync(ct);
        // Materialize the page (max 20 rows) then map in memory: enum.ToString() in an
        // EF projection is not reliably translatable across providers.
        var page = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Include(b => b.Recipients)
            .ToListAsync(ct);

        var items = page.Select(b => new MarketingBroadcastDto(
            b.Id, b.Subject, b.Status.ToString(),
            b.Recipients.Count,
            b.Recipients.Count(r => r.Status == MarketingRecipientStatus.Sent),
            b.Recipients.Count(r => r.Status == MarketingRecipientStatus.Failed),
            b.CreatedDateUtc, b.CreatedBy, b.QueuedAtUtc, b.CompletedAtUtc)).ToList();

        return new PagedResult<MarketingBroadcastDto>(items, total, request.Page, request.PageSize);
    }

    public async Task<MarketingBroadcastDetailsDto?> GetDetailsAsync(Guid id, CancellationToken ct)
    {
        var b = await db.MarketingBroadcasts.AsNoTracking()
            .Include(x => x.Recipients)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return null;

        return new MarketingBroadcastDetailsDto(
            new MarketingBroadcastDto(b.Id, b.Subject, b.Status.ToString(),
                b.RecipientCount, b.SentCount, b.FailedCount,
                b.CreatedDateUtc, b.CreatedBy, b.QueuedAtUtc, b.CompletedAtUtc),
            b.HtmlContent,
            b.Recipients
                .OrderBy(r => r.Email)
                .Select(r => new MarketingRecipientDto(r.Email, r.FirstName, r.Status.ToString(), r.SentAtUtc, r.FailureReason))
                .ToList());
    }

    public Task<int> GetEligibleRecipientCountAsync(CancellationToken ct)
        => db.Customers.AsNoTracking()
            .CountAsync(c => !c.IsGuest && c.Email != null && c.MarketingSubscribed, ct);
}
