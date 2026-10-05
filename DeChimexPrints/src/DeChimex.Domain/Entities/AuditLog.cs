using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>Immutable audit record of a critical action for traceability/compliance.</summary>
public sealed class AuditLog : Entity
{
    private AuditLog() { } // EF
    private AuditLog(string action, string entityName, string? entityId, string? performedBy,
        string? oldValues, string? newValues, string? ipAddress, string? correlationId)
    {
        Action = action;
        EntityName = entityName;
        EntityId = entityId;
        PerformedBy = performedBy;
        OldValues = oldValues;
        NewValues = newValues;
        IpAddress = ipAddress;
        CorrelationId = correlationId;
        TimestampUtc = DateTime.UtcNow;
    }

    public string Action { get; private set; } = default!;
    public string EntityName { get; private set; } = default!;
    public string? EntityId { get; private set; }
    public string? PerformedBy { get; private set; }
    public string? OldValues { get; private set; }
    public string? NewValues { get; private set; }
    public string? IpAddress { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTime TimestampUtc { get; private set; }

    // Nullable for historical technical audit records. The business feed never returns raw snapshots.
    public string? ActivityType { get; private set; }
    public string? Category { get; private set; }
    public string? Title { get; private set; }
    public string? Summary { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string? ActorName { get; private set; }
    public string? ActorRole { get; private set; }
    public Guid? OrderId { get; private set; }
    public Guid? CustomerId { get; private set; }
    public Guid? ProductId { get; private set; }
    public Guid? StaffUserId { get; private set; }
    public string? Importance { get; private set; }
    public string? SourceKey { get; private set; }
    public bool IsRealtime { get; private set; }
    public DateTime? RealtimeDispatchedAtUtc { get; private set; }
    public void MarkDispatched(DateTime now) => RealtimeDispatchedAtUtc = now;
    public void Describe(string type, string category, string title, string summary,
        Guid? actorId, string actorName, string actorRole, string key, bool realtime,
        Guid? orderId = null, Guid? customerId = null, Guid? productId = null,
        Guid? staffId = null, string importance = "normal", DateTime? occurredUtc = null)
    {
        ActivityType = type; Category = category; Title = title; Summary = summary.Length > 800 ? summary[..799] + "…" : summary;
        ActorUserId = actorId; ActorName = actorName; ActorRole = actorRole.Length > 32 ? actorRole[..31] + "…" : actorRole;
        SourceKey = key; IsRealtime = realtime; OrderId = orderId; CustomerId = customerId;
        ProductId = productId; StaffUserId = staffId; Importance = importance;
        if (occurredUtc.HasValue) TimestampUtc = occurredUtc.Value;
    }
    public void CopyBusinessFields(AuditLog source) => Describe(source.ActivityType!, source.Category!, source.Title!, source.Summary!,
        source.ActorUserId, source.ActorName!, source.ActorRole!, source.SourceKey!, source.IsRealtime,
        source.OrderId, source.CustomerId, source.ProductId, source.StaffUserId, source.Importance!, source.TimestampUtc);

    public static AuditLog Create(string action, string entityName, string? entityId = null, string? performedBy = null,
        string? oldValues = null, string? newValues = null, string? ip = null, string? correlationId = null)
        => new(action, entityName, entityId, performedBy, oldValues, newValues, ip, correlationId);
}
