using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Health;

/// <summary>
/// MED-007: Reports Paystack readiness. Validates configuration and performs a lightweight,
/// time-boxed connectivity probe. A failed probe degrades (rather than fails) the report so a
/// payment-provider outage does not, by itself, take down the app's readiness probe.
/// </summary>
public sealed class PaystackHealthCheck(IOptions<PaystackOptions> options, IHttpClientFactory httpClientFactory)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.SecretKey) || string.IsNullOrWhiteSpace(opts.PublicKey))
            return HealthCheckResult.Unhealthy("Paystack keys are not configured.");

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(opts.ApiBaseUrl, ct);
            return HealthCheckResult.Healthy($"Paystack reachable (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Paystack connectivity probe failed.", ex);
        }
    }
}
