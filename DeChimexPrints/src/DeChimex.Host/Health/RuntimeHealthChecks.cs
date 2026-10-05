using DeChimex.Host.DevPortal;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Health;

/// <summary>
/// AUD-030: one public "ready" probe for the runtime surfaces the data-centric checks never
/// covered — (1) the SignalR real-time channel (hub stats + recent transport failures via
/// RealtimeMonitor) and (2) the on-disk upload storage with a real write/delete round-trip
/// into BOTH roots (public media root and the private uploads root introduced by AUD-021 —
/// if the private root is unwritable, every authorized upload fails even though wwwroot
/// looks healthy). Tagged "ready", so it runs in the public /health endpoint.
/// </summary>
public sealed class RuntimeHealthChecks(
    RealtimeMonitor monitor,
    IOptions<StorageOptions> storageOptions,
    IWebHostEnvironment environment,
    ILogger<RuntimeHealthChecks> logger)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();

        // --- Real-time channel: open connections and recent failures ---
        var open = monitor.Hubs.Sum(h => Interlocked.Read(ref h.Value.Open));
        var failures = monitor.FailuresSince(DateTime.UtcNow.AddMinutes(-15));
        data["realtime.openConnections"] = open;
        data["realtime.failures15m"] = failures;
        if (failures > 0)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{failures} realtime failure(s) in the last 15 minutes; {open} open connection(s).", data: data));

        // --- Upload storage: write + delete a probe in both roots ---
        try
        {
            var o = storageOptions.Value;
            var probeName = $".runtime-probe-{Guid.NewGuid():N}.tmp";
            var publicRoot = StorageHealthCheck.ResolveRoot(o, environment);
            var privateRoot = Path.GetFullPath(Path.Combine(
                environment.ContentRootPath,
                string.IsNullOrWhiteSpace(o.PrivateRootPath) ? "App_Data/private-uploads" : o.PrivateRootPath));
            WriteProbe(publicRoot, probeName);
            WriteProbe(privateRoot, probeName);
            data["storage.publicRoot"] = publicRoot;
            data["storage.privateRoot"] = privateRoot;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upload storage runtime probe failed");
            return Task.FromResult(HealthCheckResult.Degraded("Upload storage is not writable", ex, data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"runtime ok: {open} open realtime connection(s); upload storage writable (both roots).", data));
    }

    private static void WriteProbe(string root, string probeName)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, probeName);
        File.WriteAllText(path, "ok");
        File.Delete(path);
    }
}
