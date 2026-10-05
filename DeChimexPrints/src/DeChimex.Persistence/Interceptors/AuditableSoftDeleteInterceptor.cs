using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Common;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DeChimex.Persistence.Interceptors;

/// <summary>
/// Central audit + soft-delete interceptor. On every save it:
///   * stamps the audit fields (CreatedBy/CreatedDateUtc/ModifiedBy/ModifiedDateUtc) on
///     <see cref="IAuditable"/> entities;
///   * converts hard deletes of soft-deletable aggregates into logical (soft) deletes;
///   * writes an <see cref="AuditLog"/> row for each created/updated/deleted aggregate
///     (GAP-01 fix: the write side was previously registered but never invoked, so the
///     AuditLogs table was always empty and the /admin/reports/staff view had no data).
///
/// The audit row is added to the SAME <see cref="DbContext"/>, so it is persisted in the
/// same SaveChanges transaction as the change it describes (atomic, no recursion).
/// </summary>
public sealed class AuditableSoftDeleteInterceptor(ICurrentUser currentUser, IDateTimeProvider clock)
    : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private readonly List<AuditLog> generated = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private void Apply(DbContext context)
    {
        // Remove only our uncommitted rows from a previously failed attempt; keep business changes.
        foreach (var row in generated)
            if (context.Entry(row).State == EntityState.Added) context.Entry(row).State = EntityState.Detached;
        generated.Clear();
        var business = context is DeChimexDbContext activityDb
            ? BusinessActivityCapture.Capture(activityDb, currentUser, clock.UtcNow) : new List<AuditLog>();
        var user = currentUser.Email ?? currentUser.UserId?.ToString() ?? "system";
        var now = clock.UtcNow;
        var ip = currentUser.IpAddress;

        // ---- Capture aggregate changes BEFORE the soft-delete conversion below flips
        //      Deleted -> Modified, so we record the true intent (delete vs update). ----
        var auditLogs = new List<AuditLogSnapshot>();
        var auditEntries = context.ChangeTracker.Entries<AggregateRoot>().ToList();
        foreach (var entry in auditEntries)
        {
            string action;
            switch (entry.State)
            {
                case EntityState.Added:
                    action = "create";
                    break;
                case EntityState.Deleted:
                    action = "delete";
                    break;
                case EntityState.Modified:
                    action = entry.Entity is ISoftDeletable s && s.IsDeleted ? "delete" : "update";
                    break;
                default:
                    continue; // Detached / Unchanged
            }

            var id = entry.Entity.Id;
            // Old/new values are best-effort scalar snapshots; a failure to snapshot must
            // never break the business save, so fall back to null on any serialization issue.
            auditLogs.Add(new AuditLogSnapshot(
                action,
                entry.Metadata.ClrType.Name,
                id.ToString(),
                user,
                ip,
                Snapshot(entry)));
        }

        // ---- Existing audit-field stamping ----
        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedDateUtc = now;
                    entry.Entity.CreatedBy = user;
                    break;
                case EntityState.Modified:
                    entry.Entity.ModifiedDateUtc = now;
                    entry.Entity.ModifiedBy = user;
                    break;
            }
        }

        // ---- Existing soft-delete conversion ----
        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable softDeletable)
            {
                entry.State = EntityState.Modified;
                softDeletable.MarkDeleted(user);
            }
        }

        // ---- Persist the audit rows atomically with the change ----
        var db = context as DeChimexDbContext;
        if (db is null)
            return;

        foreach (var a in auditLogs)
        {
            var row = AuditLog.Create(a.Action, a.EntityName, a.EntityId, a.PerformedBy,
                oldValues: a.OldValues, newValues: a.NewValues, ip: a.IpAddress, correlationId: null);
            var activity = business.FirstOrDefault(x => x.EntityName == a.EntityName && x.EntityId == a.EntityId);
            if (activity is not null) { row.CopyBusinessFields(activity); business.Remove(activity); }
            db.AuditLogs.Add(row); generated.Add(row);
        }
        foreach (var row in business) { db.AuditLogs.Add(row); generated.Add(row); }
    }

    // This is deliberately an allowlist, not a blacklist of secret-looking property names.
    private static readonly HashSet<string> SafeProperties = new(StringComparer.Ordinal)
    { "Name", "CategoryId", "MajorCategoryId", "IsActive", "IsFeatured", "IsCustomizable", "DisplayOrder",
      "Status", "OrderId", "CustomerId", "ProductId", "Rating", "IsApproved", "QuantityOnHand", "QuantityReserved", "LowStockThreshold" };

    private static (string? OldValues, string? NewValues) Snapshot(EntityEntry entry)
    {
        try
        {
            if (entry.Entity is not (Product or Category or MajorCategory or Inventory or Role or Review or Order or Payment)) return (null, null);
            var props = entry.Properties.Where(p => SafeProperties.Contains(p.Metadata.Name)).ToList();
            switch (entry.State)
            {
                case EntityState.Added:
                    return (null, Serialize(props, p => p.CurrentValue));
                case EntityState.Deleted:
                    return (Serialize(props, p => p.OriginalValue), null);
                case EntityState.Modified:
                    var changed = props.Where(p => p.IsModified)
                                       .Select(p => p.Metadata.Name)
                                       .ToHashSet(StringComparer.Ordinal);
                    var old = Serialize(props.Where(p => changed.Contains(p.Metadata.Name)), p => p.OriginalValue);
                    var @new = Serialize(props.Where(p => changed.Contains(p.Metadata.Name)), p => p.CurrentValue);
                    return (old, @new);
                default:
                    return (null, null);
            }
        }
        catch
        {
            return (null, null);
        }
    }

    private static readonly HashSet<string> SkipProps = new(StringComparer.Ordinal)
    {
        // Base audit + soft-delete bookkeeping on AggregateRoot; not meaningful in a diff.
        "CreatedDateUtc", "CreatedBy", "ModifiedDateUtc", "ModifiedBy",
        "IsDeleted", "DeletedDateUtc", "DeletedBy",
        // AuditLog's own timestamp
        "TimestampUtc"
    };

    private static string? Serialize(IEnumerable<PropertyEntry> props, Func<PropertyEntry, object?> value)
    {
        var dict = new Dictionary<string, string>();
        foreach (var p in props)
        {
            if (p.Metadata.IsShadowProperty() || p.Metadata.IsPrimaryKey() || p.Metadata.IsConcurrencyToken)
                continue;
            if (SkipProps.Contains(p.Metadata.Name))
                continue;
            var v = value(p);
            dict[p.Metadata.Name] = v?.ToString() ?? string.Empty;
        }
        return dict.Count == 0 ? null : JsonSerializer.Serialize(dict, Json);
    }

    /// <summary>Transient holder so the audit row is built after the create/update/delete intent is resolved.</summary>
    private sealed record AuditLogSnapshot(
        string Action, string EntityName, string EntityId, string PerformedBy,
        string? IpAddress, (string? Old, string? New) Values)
    {
        public string? OldValues => Values.Old;
        public string? NewValues => Values.New;
    }
}
