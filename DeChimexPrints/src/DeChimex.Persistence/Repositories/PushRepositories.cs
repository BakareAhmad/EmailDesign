using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Repositories;

/// <summary>PWA Web Push subscription storage (one row per user browser/device).</summary>
public sealed class PushSubscriptionRepository(DeChimexDbContext context)
    : RepositoryBase<PushSubscription>(context), IPushSubscriptionRepository
{
    public Task<PushSubscription?> GetByUserAndEndpointAsync(Guid userId, string endpoint, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == endpoint, ct);

    public async Task<IReadOnlyList<PushSubscription>> ListByUserAsync(Guid userId, CancellationToken ct = default)
        => await Set.Where(s => s.UserId == userId).ToListAsync(ct);

    public Task<PushSubscription?> GetByEndpointAsync(string endpoint, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
}
