using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities.Diagnostics;

/// <summary>
/// One call to / from an external integration (Paystack initialize / verify / refund /
/// webhook, Brevo email send). Technical diagnostics only: never stores request/response
/// bodies, secrets, card data or email content. Recipients are stored masked
/// ("a***@example.com"); error excerpts are redacted and truncated.
/// </summary>
public sealed class IntegrationCall : Entity
{
    private IntegrationCall() { } // EF

    public DateTime TimestampUtc { get; private set; }
    /// <summary>Paystack | Brevo.</summary>
    public string Integration { get; private set; } = default!;
    /// <summary>initialize | verify | refund | webhook | send.</summary>
    public string Operation { get; private set; } = default!;
    /// <summary>Success | Failure | Skipped | Rejected | Duplicate | Ignored | Mismatch | Pending.</summary>
    public string Outcome { get; private set; } = default!;
    public bool Succeeded { get; private set; }
    public int? HttpStatus { get; private set; }
    public long DurationMs { get; private set; }
    /// <summary>Payment reference (safe business identifier) when applicable.</summary>
    public string? Reference { get; private set; }
    public Guid? OrderId { get; private set; }
    /// <summary>Email template name (Brevo) — never the content.</summary>
    public string? Template { get; private set; }
    /// <summary>customer | staff | contact | marketing | internal.</summary>
    public string? RecipientCategory { get; private set; }
    public string? RecipientMasked { get; private set; }
    /// <summary>Short, redacted provider message / error description.</summary>
    public string? Detail { get; private set; }
    public string? CorrelationId { get; private set; }

    public static IntegrationCall Create(DateTime timestampUtc, string integration, string operation, string outcome,
        bool succeeded, long durationMs, int? httpStatus = null, string? reference = null, Guid? orderId = null,
        string? template = null, string? recipientCategory = null, string? recipientMasked = null,
        string? detail = null, string? correlationId = null)
        => new()
        {
            TimestampUtc = timestampUtc,
            Integration = integration,
            Operation = operation,
            Outcome = outcome,
            Succeeded = succeeded,
            DurationMs = durationMs,
            HttpStatus = httpStatus,
            Reference = reference,
            OrderId = orderId,
            Template = template,
            RecipientCategory = recipientCategory,
            RecipientMasked = recipientMasked,
            Detail = detail,
            CorrelationId = correlationId
        };
}
