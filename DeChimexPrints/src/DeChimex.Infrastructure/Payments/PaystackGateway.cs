using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Domain.Enums;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Payments;

/// <summary>
/// Paystack payment gateway integration. Amounts are converted to the minor unit (kobo).
/// Webhook signatures are verified with HMAC-SHA512 using the secret key.
/// </summary>
public sealed class PaystackGateway(
    HttpClient httpClient,
    IOptions<PaystackOptions> options,
    ILogger<PaystackGateway> logger) : IPaymentGateway
{
    private readonly PaystackOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<PaymentInitializationResult> InitializeAsync(PaymentInitializationRequest request, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["email"] = request.Email,
            ["amount"] = ToMinorUnit(request.Amount),
            ["currency"] = request.Currency,
            ["reference"] = request.Reference,
            ["callback_url"] = request.CallbackUrl
        };
        // Optional channel restriction (Paystack:Channels). Omitted entirely when empty so
        // Paystack shows every channel enabled on the integration (documented default).
        if (_options.Channels is { Length: > 0 })
        {
            payload["channels"] = _options.Channels
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToArray();
        }
        // Paystack only persists transaction metadata sent as custom_fields entries.
        if (request.Metadata is { Count: > 0 })
        {
            payload["metadata"] = new
            {
                custom_fields = request.Metadata.Select(kv => new
                {
                    display_name = kv.Key,
                    variable_name = new string(kv.Key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(),
                    value = kv.Value
                }).ToArray()
            };
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl}/transaction/initialize");
        Authorize(msg);
        msg.Content = JsonContent(payload);

        var response = await httpClient.SendAsync(msg, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Paystack init failed ({Status}): {Body}", response.StatusCode, body);
            return new PaymentInitializationResult(false, null, null, "Failed to initialize transaction.");
        }

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        return new PaymentInitializationResult(true,
            data.GetProperty("authorization_url").GetString(),
            data.GetProperty("reference").GetString(),
            "Initialized");
    }

    public async Task<PaymentVerificationResult> VerifyAsync(string reference, CancellationToken ct = default)
    {
        string body;
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl}/transaction/verify/{Uri.EscapeDataString(reference)}");
            Authorize(msg);

            var response = await httpClient.SendAsync(msg, ct);
            body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // The verification request itself could not be answered (transport error,
                // 4xx/5xx). This is NOT evidence that the payment failed — the charge may
                // still be in flight or already settled. Report Pending so the caller keeps
                // the payment retryable and unconfirmed.
                logger.LogWarning("Paystack verify request failed ({Status}) for {Reference}.", response.StatusCode, reference);
                return new PaymentVerificationResult(PaymentVerificationOutcome.Pending, null, 0, null, body,
                    "The payment could not be verified right now.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogWarning(ex, "Paystack verify request errored for {Reference}.", reference);
            return new PaymentVerificationResult(PaymentVerificationOutcome.Pending, null, 0, null, null,
                "The payment could not be verified right now.");
        }

        string? status;
        long amountMinor;
        long? feesMinor = null;
        string? gatewayRef;
        string? currency;
        string? gatewayResponse;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            status = data.TryGetProperty("status", out var s) ? s.GetString() : null;
            amountMinor = data.TryGetProperty("amount", out var a) ? a.GetInt64() : 0;
            // Authoritative transaction fee (verify-payments `fees`, kobo) — snapshotted
            // onto the payment record so history reflects what Paystack actually charged.
            feesMinor = data.TryGetProperty("fees", out var f) && f.ValueKind is JsonValueKind.Number
                ? f.GetInt64() : null;
            gatewayRef = data.TryGetProperty("reference", out var r) ? r.GetString() : reference;
            currency = data.TryGetProperty("currency", out var c) ? c.GetString() : null;
            gatewayResponse = data.TryGetProperty("gateway_response", out var g) ? g.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Malformed/unexpected body: cannot confirm anything — stay pending, never
            // paid, never a hard failure (a webhook can still settle the payment).
            logger.LogWarning(ex, "Paystack verify response could not be parsed for {Reference}.", reference);
            return new PaymentVerificationResult(PaymentVerificationOutcome.Pending, null, 0, null, body,
                "The payment could not be verified right now.");
        }

        // Paystack's documented transaction statuses (verify-payments):
        // success | failed | abandoned | ongoing | pending | processing | queued | reversed.
        // In-flight statuses (and anything unknown) map to Pending: async channels such as
        // bank transfer, USSD and mobile money sit in ongoing/pending/processing while the
        // customer completes the payment. Only terminal unpaid outcomes map to Failed.
        var outcome = status?.Trim().ToLowerInvariant() switch
        {
            "success" => PaymentVerificationOutcome.Succeeded,
            "failed" or "abandoned" or "reversed" => PaymentVerificationOutcome.Failed,
            _ => PaymentVerificationOutcome.Pending
        };

        var message = outcome switch
        {
            PaymentVerificationOutcome.Succeeded => null,
            PaymentVerificationOutcome.Failed => SanitizeFailureMessage(gatewayResponse, status),
            _ => "The payment is still being confirmed."
        };

        return new PaymentVerificationResult(outcome, gatewayRef, FromMinorUnit(amountMinor), currency, body, message,
            feesMinor is null ? null : FromMinorUnit(feesMinor.Value));
    }

    /// <summary>
    /// Customer-safe failure copy derived from the gateway response; raw API/error bodies
    /// are kept in RawResponse for the audit trail, never shown to customers.
    /// </summary>
    private static string SanitizeFailureMessage(string? gatewayResponse, string? status)
    {
        if (!string.IsNullOrWhiteSpace(gatewayResponse))
        {
            var trimmed = gatewayResponse.Trim();
            if (trimmed.Length <= 120) return trimmed;
        }
        return string.Equals(status, "abandoned", StringComparison.OrdinalIgnoreCase)
            ? "The payment was not completed."
            : "Payment was not successful.";
    }

    public bool VerifyWebhookSignature(string requestBody, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.SecretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(requestBody));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signatureHeader.Trim().ToLowerInvariant()));
    }

    public async Task<RefundResult> RefundAsync(string reference, decimal? amount, CancellationToken ct = default)
    {
        var payload = amount is null
            ? (object)new { transaction = reference }
            : new { transaction = reference, amount = ToMinorUnit(amount.Value) };

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl}/refund");
        Authorize(msg);
        msg.Content = JsonContent(payload);

        var response = await httpClient.SendAsync(msg, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        
        if (response.IsSuccessStatusCode)
        {
            // HTTP 2xx alone is not a successful provider result, and acceptance is not settlement.
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("status", out var ok) || ok.ValueKind != JsonValueKind.True
                    || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return new RefundResult(false, "The gateway did not confirm refund acceptance.", null);
                if (data.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String
                    && state.GetString() is "failed" or "rejected")
                    return new RefundResult(false, "The gateway rejected the refund.", null);
                var refundReference = data.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                return new RefundResult(true, "Refund request accepted; settlement is not confirmed here.", refundReference);
            }
            catch (JsonException)
            {
                return new RefundResult(false, "The gateway refund response could not be verified.", null);
            }
        }
        else
        {
            logger.LogError("Paystack refund failed ({Status}): {Body}", response.StatusCode, body);
            return new RefundResult(false, body, null);
        }
    }

    private void Authorize(HttpRequestMessage msg)
        => msg.Headers.Add("Authorization", $"Bearer {_options.SecretKey}");

    private static StringContent JsonContent(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static long ToMinorUnit(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
    private static decimal FromMinorUnit(long minor) => minor / 100m;
}

/// <summary>Parses Paystack webhook JSON into a provider-agnostic <see cref="WebhookEvent"/>.</summary>
public sealed class PaystackWebhookParser : IWebhookPayloadParser
{
    public WebhookEvent? Parse(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var evt = root.TryGetProperty("event", out var e) ? e.GetString() : null;
            var data = root.TryGetProperty("data", out var d) ? d : default;

            var isSuccess = string.Equals(evt, "charge.success", StringComparison.OrdinalIgnoreCase);
            var reference = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reference", out var r) ? r.GetString() : null;
            var amount = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("amount", out var a) ? a.GetInt64() / 100m : 0m;
            var currency = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("currency", out var c) ? c.GetString() : null;

            // Refund lifecycle events (AUD-005): the refund object carries its own
            // refund_reference; the originating charge shows up as transaction_reference.
            string? refundReference = null, transactionReference = null;
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("refund_reference", out var rr)) refundReference = rr.GetString();
                if (data.TryGetProperty("transaction_reference", out var tr)) transactionReference = tr.GetString();
                if (data.TryGetProperty("reference", out var ref2) && reference is null) reference = ref2.GetString();
            }
            // For refund events, "reference" (if any) refers to the refund — keep the
            // transaction reference out of the charge-reference slot.
            if (evt is not null && evt.StartsWith("refund.", StringComparison.OrdinalIgnoreCase))
            {
                refundReference ??= reference;
                reference = transactionReference;
            }

            return new WebhookEvent(evt ?? string.Empty, isSuccess, reference, reference, amount, currency, refundReference);
        }
        catch
        {
            return null;
        }
    }
}
