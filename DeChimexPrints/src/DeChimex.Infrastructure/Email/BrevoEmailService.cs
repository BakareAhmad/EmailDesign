using System.Net.Http.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Email;

/// <summary>
/// Sends transactional email via the Brevo (Sendinblue) v3 API. The API key is read from
/// configuration and secret storage. They are never hardcoded. When disabled, sends are logged and skipped
/// (useful for local/dev without credentials).
/// </summary>
public sealed class BrevoEmailService(
    HttpClient httpClient,
    IOptions<BrevoOptions> options,
    ILogger<BrevoEmailService> logger,
    IIntegrationCallRecorder integrationCalls) : IEmailService
{
    private readonly BrevoOptions _options = options.Value;

    /// <summary>Developer Portal email diagnostics: outcome + template + recipient category and
    /// a MASKED recipient. Message subject/body are never recorded.</summary>
    private void Track(EmailMessage m, string outcome, bool ok, long ms, int? status, string? detail)
        => integrationCalls.Record(new IntegrationCallEntry(IntegrationNames.Brevo, "send", outcome, ok, ms,
            HttpStatus: status, Template: m.Template ?? "Unspecified", RecipientCategory: m.Category ?? "unspecified",
            RecipientEmail: m.ToEmail, Detail: detail));

    /// <summary>Plain-language cause for a Brevo HTTP failure (shown to the developer).</summary>
    public static string ExplainStatus(int status) => status switch
    {
        401 => "Unauthorized: the API key is invalid/revoked, or this server's IP is not authorised in Brevo (Security > Authorised IPs).",
        400 => "Bad request: payload rejected (commonly an unverified sender address or an invalid recipient).",
        402 => "Payment required: the Brevo plan has no remaining email credits.",
        403 => "Forbidden: the account is not allowed to send (suspended or not activated for transactional email).",
        429 => "Rate limited by Brevo: too many requests.",
        >= 500 => "Brevo service error (provider outage).",
        _ => "Unexpected response from Brevo."
    };

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
        => await TrySendAsync(message, ct);

    /// <summary>
    /// Sends and RETURNS the outcome (used by the marketing broadcast worker, which must
    /// distinguish delivered from failed per recipient). Never throws for provider
    /// failures; only for programming errors.
    /// </summary>
    public async Task<bool> TrySendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            Track(message, "Skipped", false, 0, null,
                !_options.Enabled ? "Email sending is disabled (Brevo:Enabled=false)." : "Brevo API key is not configured.");
            // Local/dev convenience: log the plain-text body too (it carries the email
            // verification OTP and account links) so a developer can complete flows
            // without a real inbox. This path only runs when sending is disabled or no
            // credentials are configured - never on the live Brevo path.
            logger.LogInformation("Email sending disabled or unconfigured. Would send '{Subject}' to {To}. Body: {Body}",
                message.Subject, message.ToEmail, message.PlainTextBody ?? message.HtmlBody);

            // DEV-ONLY outbox: append a machine-readable copy so local E2E automation can
            // read verification tokens without a real inbox. Only active on this disabled path.
            if (!string.IsNullOrWhiteSpace(_options.DevOutboxPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(_options.DevOutboxPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(_options.DevOutboxPath,
                        $"TO:{message.ToEmail}|SUBJECT:{message.Subject}|BODY:{message.PlainTextBody ?? message.HtmlBody}{Environment.NewLine}");
                }
                catch
                {
                    // Never break a send because the dev outbox failed.
                }
            }
            return false;
        }

        var payload = new
        {
            sender = new { name = _options.SenderName, email = _options.SenderEmail },
            to = new[] { new { email = message.ToEmail, name = message.ToName } },
            subject = message.Subject,
            htmlContent = message.HtmlBody,
            textContent = message.PlainTextBody ?? string.Empty
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl}/smtp/email");
        request.Headers.Add("api-key", _options.ApiKey);
        request.Content = JsonContent.Create(payload);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Track(message, "Exception", false, sw.ElapsedMilliseconds, null,
                $"Could not reach Brevo ({ex.GetType().Name}): network/DNS/TLS problem or timeout.");
            throw;
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Brevo send failed ({Status}) for {To}: {Body}", response.StatusCode, message.ToEmail, body);
            var status = (int)response.StatusCode;
            Track(message, "Failure", false, sw.ElapsedMilliseconds, status,
                ExplainStatus(status) + " Provider said: " + (body.Length > 300 ? body[..300] : body));
            return false;
        }

        Track(message, "Success", true, sw.ElapsedMilliseconds, (int)response.StatusCode, null);
        logger.LogInformation("Email '{Subject}' sent to {To}.", message.Subject, message.ToEmail);
        return true;
    }
}
