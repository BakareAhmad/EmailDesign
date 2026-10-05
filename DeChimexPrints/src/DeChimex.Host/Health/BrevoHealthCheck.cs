using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Health;

/// <summary>
/// MED-007: Reports Brevo (email) readiness. When email is disabled the check is healthy; when
/// enabled it validates the API key and performs a time-boxed connectivity probe (degrading on
/// failure rather than failing the overall report).
/// </summary>
public sealed class BrevoHealthCheck(IOptions<BrevoOptions> options, IHttpClientFactory httpClientFactory)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled)
            return HealthCheckResult.Healthy("Brevo email sending is disabled.");

        if (string.IsNullOrWhiteSpace(opts.ApiKey))
            return HealthCheckResult.Unhealthy("Brevo is enabled but the API key is not configured.");

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(opts.ApiBaseUrl, ct);
            return HealthCheckResult.Healthy($"Brevo reachable (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Brevo connectivity probe failed.", ex);
        }
    }
}
