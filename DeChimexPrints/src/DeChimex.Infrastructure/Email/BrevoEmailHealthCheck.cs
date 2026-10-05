using System.Net.Http.Json;
using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Email;

/// <summary>
/// Diagnoses why emails do or do not leave the server. Pings the Brevo account endpoint
/// with the configured API key: Brevo answers unauthorised IPs with a 401 whose message
/// names the rejected IP, which is exactly the information an admin needs (the account's
/// "Authorised IPs" security setting silently blocks every send otherwise). Optionally
/// sends a test email to confirm end-to-end acceptance. Never throws: a broken provider
/// must not break the settings page.
/// </summary>
public sealed class BrevoEmailHealthCheck(
    HttpClient httpClient,
    IOptions<BrevoOptions> options,
    IEmailService emailService,
    IEmailTemplateRenderer emailRenderer,
    ILogger<BrevoEmailHealthCheck> logger) : IEmailHealthCheck
{
    private readonly BrevoOptions _options = options.Value;

    public async Task<EmailHealthResult> CheckAsync(string? testRecipient, CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return new EmailHealthResult(false, "Email sending is disabled or not configured",
                "Set Brevo:Enabled=true and provide Brevo:ApiKey, then restart the app.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl}/account");
            request.Headers.Add("api-key", _options.ApiKey);
            using var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var providerMessage = TryExtractMessage(body) ?? body;
                logger.LogWarning("Email health check: Brevo rejected the API call ({Status}): {Body}",
                    response.StatusCode, body);

                // The 401 from Brevo's IP authorisation names the rejected IP - surface it
                // verbatim plus the dashboard page where the restriction is managed.
                var isIpBlock = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    && providerMessage.Contains("IP address", StringComparison.OrdinalIgnoreCase);
                return new EmailHealthResult(false,
                    isIpBlock ? "Brevo is blocking this server's IP address" : $"Brevo rejected the request ({(int)response.StatusCode})",
                    providerMessage,
                    isIpBlock ? "https://app.brevo.com/security/authorised_ips" : null);
            }

            // The account response carries the send quota. The free plan gets a daily
            // limit; when it hits 0 Brevo still ACCEPTS sends with HTTP 2xx but silently
            // holds them - so a plain "connected" status would be a lie. Surface it.
            var account = await response.Content.ReadFromJsonAsync<AccountInfo>(cancellationToken: ct);
            var quota = account?.Plan is { Count: > 0 } p ? p[0] : null;
            var outOfQuota = quota is { CreditsType: "sendLimit", Credits: 0 };
            var quotaNote = quota?.CreditsType == "sendLimit" && quota.Credits > 0
                ? $" {quota.Credits} sends remaining today."
                : null;

            if (outOfQuota)
            {
                return new EmailHealthResult(false, "Daily email limit reached (0 sends left today)",
                    "Brevo's free plan includes 300 emails per day. Emails are accepted but NOT delivered until the quota resets at midnight UTC. Wait for the reset, or upgrade the plan at brevo.com/pricing.",
                    "https://www.brevo.com/pricing/");
            }

            if (string.IsNullOrWhiteSpace(testRecipient))
            {
                return new EmailHealthResult(true, "Connected to Brevo",
                    "The API key works from this server." + (quotaNote ?? string.Empty) + " Send a test email to confirm delivery end-to-end.");
            }

            var content = emailRenderer.Render(EmailTemplate.ProviderTestEmail,
                new Dictionary<string, string> { ["RawMessage"] = "" });
            var sent = await emailService.TrySendAsync(new EmailMessage(
                testRecipient, "DE-CHIMEX staff",
                content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.ProviderTestEmail), Category: "internal"), ct);

            return sent
                ? new EmailHealthResult(true, $"Test email accepted for {testRecipient}",
                    "Brevo accepted the test email - check the inbox (and the spam folder) to confirm delivery.")
                : new EmailHealthResult(false, $"Test email to {testRecipient} was rejected",
                    "The provider refused the test send - see the app log for the exact provider response.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email health check could not reach Brevo.");
            return new EmailHealthResult(false, "Could not reach Brevo",
                $"Network error: {ex.Message}");
        }
    }

    private static string? TryExtractMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
        }
        catch (JsonException) { /* not JSON - return null and use the raw body */ }
        return null;
    }

    private sealed record AccountInfo(List<PlanInfo>? Plan);
    private sealed record PlanInfo(string? Type, long Credits, string? CreditsType);
}
