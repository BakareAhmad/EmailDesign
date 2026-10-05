using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities.Diagnostics;

/// <summary>
/// One persisted TECHNICAL event (Developer Portal technical audit log / error center).
/// Conceptually distinct from <see cref="AuditLog"/> (business activity): this records what
/// happened technically (exceptions, integration failures, security events, job failures,
/// maintenance actions). Append-only, retention-pruned. Derives from <see cref="Entity"/>
/// (not AggregateRoot) so it never generates business audit rows. All free-text fields are
/// redacted before they reach this entity.
/// </summary>
public sealed class TechnicalEvent : Entity
{
    private TechnicalEvent() { } // EF

    public DateTime TimestampUtc { get; private set; }
    /// <summary>Information | Warning | Error | Critical.</summary>
    public string Severity { get; private set; } = default!;
    public string Category { get; private set; } = default!;
    public string EventType { get; private set; } = default!;
    public string Message { get; private set; } = default!;
    /// <summary>Stable message template (Serilog) used for grouping identical errors.</summary>
    public string? MessageTemplate { get; private set; }
    /// <summary>SHA-1 of category + source + exception type + template: the error-group key.</summary>
    public string Fingerprint { get; private set; } = default!;
    public string? ExceptionType { get; private set; }
    public string? StackTrace { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? RequestId { get; private set; }
    public Guid? UserId { get; private set; }
    public string? Route { get; private set; }
    public Guid? OrderId { get; private set; }
    public string? Integration { get; private set; }
    public string? Source { get; private set; }
    public string? Environment { get; private set; }
    /// <summary>Small redacted JSON object of extra diagnostic metadata.</summary>
    public string? Properties { get; private set; }

    public static TechnicalEvent Create(DateTime timestampUtc, string severity, string category, string eventType,
        string message, string fingerprint, string? messageTemplate = null, string? exceptionType = null,
        string? stackTrace = null, string? correlationId = null, string? requestId = null, Guid? userId = null,
        string? route = null, Guid? orderId = null, string? integration = null, string? source = null,
        string? environment = null, string? properties = null)
        => new()
        {
            TimestampUtc = timestampUtc,
            Severity = severity,
            Category = category,
            EventType = eventType,
            Message = message,
            Fingerprint = fingerprint,
            MessageTemplate = messageTemplate,
            ExceptionType = exceptionType,
            StackTrace = stackTrace,
            CorrelationId = correlationId,
            RequestId = requestId,
            UserId = userId,
            Route = route,
            OrderId = orderId,
            Integration = integration,
            Source = source,
            Environment = environment,
            Properties = properties
        };
}
