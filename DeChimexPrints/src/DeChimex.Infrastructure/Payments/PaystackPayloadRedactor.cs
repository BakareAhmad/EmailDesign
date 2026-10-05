using System.Text.Json;
using DeChimex.Application.Common.Interfaces;

namespace DeChimex.Infrastructure.Payments;

/// <summary>
/// Redacts customer PII from gateway payloads stored in Payment.RawResponse (AUD-033).
/// Paystack verify/charge.success bodies embed the customer's email/phone and the card
/// authorization block; those have no business value once the dedicated ledger columns
/// (amounts, references, fees) exist, so they are replaced with a fixed marker while the
/// rest of the payload — statuses, gateway_response, amounts, fees, timestamps — is kept
/// verbatim for reconciliation. Keys are matched case-insensitively and recursively, so
/// nested shapes survive provider changes. Non-JSON input is returned unchanged.
/// </summary>
public sealed class PaystackPayloadRedactor : IGatewayPayloadRedactor
{
    public const string Marker = "***redacted***";

    /// <summary>PII-bearing keys: customer contact block, card authorization metadata
    /// and the loose customer/phone fields that can appear at either level.</summary>
    private static readonly HashSet<string> RedactedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "customer", "phone", "customer_phone",
        "customer_email", "metadata"
    };

    public string Redact(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return payload ?? string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteRedacted(writer, doc.RootElement);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return payload; // not JSON: keep as-is (nothing structured to redact)
        }
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (RedactedKeys.Contains(property.Name))
                    {
                        writer.WriteString(property.Name, Marker);
                        continue;
                    }
                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedacted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
