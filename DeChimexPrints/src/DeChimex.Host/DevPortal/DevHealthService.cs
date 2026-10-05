using DeChimex.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DeChimex.Host.DevPortal;

/// <summary>Developer-facing status vocabulary.</summary>
public static class DevStatus
{
    public const string Healthy = "Healthy";
    public const string Warning = "Warning";
    public const string Degraded = "Degraded";
    public const string Unavailable = "Unavailable";

    public static int Rank(string s) => s switch { Unavailable => 3, Degraded => 2, Warning => 1, _ => 0 };
    public static string Worst(IEnumerable<string> states) => states.OrderByDescending(Rank).FirstOrDefault() ?? Healthy;
    public static string From(HealthStatus s) => s switch
    {
        HealthStatus.Healthy => Healthy,
        HealthStatus.Degraded => Degraded,
        _ => Unavailable
    };

    /// <summary>
    /// Developer view of one check. The external provider checks (Paystack, Brevo) are registered
    /// with failureStatus=Degraded so a provider outage never fails the public readiness probe;
    /// that stays as-is. For the developer, though, a probe that could not connect at all means
    /// the provider is unreachable, so it is shown as Unavailable rather than Degraded.
    /// </summary>
    public static string From(HealthReportEntry e)
    {
        var status = From(e.Status);
        if (status == Degraded && e.Tags.Contains("external") && IsConnectivityFailure(e.Exception))
            return Unavailable;
        return status;
    }

    public static bool IsConnectivityFailure(Exception? ex)
    {
        for (var x = ex; x is not null; x = x.InnerException)
            if (x is HttpRequestException or System.Net.Sockets.SocketException or TimeoutException or TaskCanceledException)
                return true;
        return false;
    }
}

public sealed record DevHealthEntry(string Name, string Status, string? Description, double DurationMs, string? Error,
    IReadOnlyDictionary<string, object> Data, IEnumerable<string> Tags);

public sealed record DevHealthSnapshot(DateTime CheckedAtUtc, string Overall, double TotalMs, IReadOnlyList<DevHealthEntry> Entries)
{
    public DevHealthEntry? Get(string name) => Entries.FirstOrDefault(e => e.Name == name);
}

/// <summary>
/// Runs ALL registered health checks (public + "dev"-tagged) for the portal. Results are cached
/// for 30 seconds so dashboard loads never hammer external providers; "Re-run health checks"
/// bypasses the cache. A change of overall status is recorded as a technical event.
/// Exception messages from checks are shown, never exception details that could include secrets.
/// </summary>
public sealed class DevHealthService(HealthCheckService health, IMemoryCache cache, ITechnicalEventRecorder recorder)
{
    private const string CacheKey = "devportal:health";
    private static string? _lastOverall;

    public async Task<DevHealthSnapshot> GetAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && cache.TryGetValue(CacheKey, out DevHealthSnapshot? cached) && cached is not null)
            return cached;

        var report = await health.CheckHealthAsync(ct);
        var entries = report.Entries.Select(e => new DevHealthEntry(
            e.Key, DevStatus.From(e.Value), e.Value.Description,
            Math.Round(e.Value.Duration.TotalMilliseconds, 1),
            e.Value.Exception is null ? null : Shared.Diagnostics.SensitiveDataRedactor.Redact($"{e.Value.Exception.GetType().Name}: {e.Value.Exception.Message}", 300),
            e.Value.Data, e.Value.Tags)).OrderBy(e => e.Name).ToList();
        var snapshot = new DevHealthSnapshot(DateTime.UtcNow, DevStatus.Worst(entries.Select(e => e.Status).Append(DevStatus.From(report.Status))),
            Math.Round(report.TotalDuration.TotalMilliseconds, 1), entries);
        cache.Set(CacheKey, snapshot, TimeSpan.FromSeconds(30));

        var previous = Interlocked.Exchange(ref _lastOverall, snapshot.Overall);
        if (previous is not null && previous != snapshot.Overall)
        {
            var worse = DevStatus.Rank(snapshot.Overall) > DevStatus.Rank(previous);
            recorder.Record(new TechnicalEventEntry(
                worse ? (snapshot.Overall == DevStatus.Unavailable ? TechnicalSeverity.Error : TechnicalSeverity.Warning) : TechnicalSeverity.Information,
                TechnicalCategory.System, TechnicalEventType.HealthChanged,
                $"Overall health changed from {previous} to {snapshot.Overall}. " +
                string.Join("; ", entries.Where(e => e.Status != DevStatus.Healthy).Select(e => $"{e.Name}: {e.Status}")),
                Source: nameof(DevHealthService), MessageTemplate: $"Overall health changed to {snapshot.Overall}"));
        }
        return snapshot;
    }

    public void Invalidate() => cache.Remove(CacheKey);
}
