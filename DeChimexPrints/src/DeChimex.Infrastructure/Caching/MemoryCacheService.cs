using DeChimex.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace DeChimex.Infrastructure.Caching;

/// <summary>
/// In-memory cache implementation of the cache abstraction. The contract is Redis-ready:
/// swap this for a distributed implementation without changing callers.
/// </summary>
public sealed class MemoryCacheService(IMemoryCache cache) : ICacheService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        => Task.FromResult(cache.TryGetValue(key, out T? value) ? value : default);

    public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken ct = default)
    {
        cache.Set(key, value, absoluteExpiration ?? DefaultTtl);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        cache.Remove(key);
        return Task.CompletedTask;
    }

    public async Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        TimeSpan? absoluteExpiration = null, CancellationToken ct = default)
    {
        if (cache.TryGetValue(key, out T? existing) && existing is not null) return existing;
        var created = await factory(ct);
        cache.Set(key, created, absoluteExpiration ?? DefaultTtl);
        return created;
    }
}
