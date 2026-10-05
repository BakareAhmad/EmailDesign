using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.DevPortal;

/// <summary>Upload storage: the directory exists, a probe file can be written and deleted, and free disk space.
/// Tagged "dev" (Developer Portal only; excluded from the public /health payload).</summary>
public sealed class StorageHealthCheck(IOptions<StorageOptions> options, IWebHostEnvironment env) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var root = ResolveRoot(options.Value, env);
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".devportal-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            var totalGb = drive.TotalSize / 1024d / 1024d / 1024d;
            var data = new Dictionary<string, object>
            {
                ["freeGb"] = Math.Round(freeGb, 2),
                ["totalGb"] = Math.Round(totalGb, 2),
                ["writable"] = true
            };
            if (freeGb < 1)
                return Task.FromResult(HealthCheckResult.Degraded($"Upload storage writable but low on disk ({freeGb:0.00} GB free).", data: data));
            return Task.FromResult(HealthCheckResult.Healthy($"Upload storage writable; {freeGb:0.0} GB free of {totalGb:0.0} GB.", data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Upload storage is not writable: " + ex.GetType().Name));
        }
    }

    /// <summary>Same resolution as LocalFileStorageService (relative to the process working directory).</summary>
    public static string ResolveRoot(StorageOptions o, IWebHostEnvironment env)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(o.LocalRootPath) ? "wwwroot/uploads" : o.LocalRootPath);
}

/// <summary>SignalR: degraded when hub failures were recorded in the last 15 minutes.</summary>
public sealed class RealtimeHealthCheck(RealtimeMonitor monitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var open = monitor.Hubs.Sum(h => Interlocked.Read(ref h.Value.Open));
        var failures = monitor.FailuresSince(DateTime.UtcNow.AddMinutes(-15));
        var data = new Dictionary<string, object> { ["openConnections"] = open, ["failures15m"] = failures };
        return Task.FromResult(failures > 0
            ? HealthCheckResult.Degraded($"{failures} realtime failure(s) in the last 15 minutes; {open} open connection(s).", data: data)
            : HealthCheckResult.Healthy($"SignalR hubs mapped; {open} open connection(s).", data));
    }
}

/// <summary>Background jobs: degraded when any job is failing or stale.</summary>
public sealed class BackgroundJobsHealthCheck(IJobMonitor jobs) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var snapshot = jobs.Snapshot();
        var bad = snapshot.Where(j => j.State is "Failing" or "Stale").ToList();
        var data = new Dictionary<string, object> { ["jobs"] = snapshot.Count, ["problems"] = bad.Count };
        return Task.FromResult(bad.Count > 0
            ? HealthCheckResult.Degraded($"{bad.Count} job(s) need attention: {string.Join(", ", bad.Select(b => $"{b.Name} ({b.State})"))}.", data: data)
            : HealthCheckResult.Healthy($"{snapshot.Count} job(s) monitored; none failing or stale.", data));
    }
}
