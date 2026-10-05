using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities.Diagnostics;
using DeChimex.Domain.Enums;
using DeChimex.Infrastructure.Diagnostics;
using DeChimex.Infrastructure.Options;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.DevPortal;

/// <summary>Time-range vocabulary shared by the portal screens.</summary>
public sealed record DevRange(string Key, string Label, DateTime FromUtc, DateTime ToUtc)
{
    public static readonly string[] Keys = { "1h", "24h", "7d", "30d", "custom" };

    /// <summary>Parses ?range=1h|24h|7d|30d|custom&amp;from=yyyy-MM-dd&amp;to=yyyy-MM-dd (custom dates are UTC days).
    /// Invalid input falls back to the default instead of failing.</summary>
    public static DevRange Parse(string? key, string? from, string? to, string fallback = "24h")
    {
        var now = DateTime.UtcNow;
        switch (key)
        {
            case "1h": return new DevRange("1h", "Last hour", now.AddHours(-1), now);
            case "7d": return new DevRange("7d", "Last 7 days", now.AddDays(-7), now);
            case "30d": return new DevRange("30d", "Last 30 days", now.AddDays(-30), now);
            case "custom":
                if (DateTime.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var f)
                    && DateTime.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                    && f.Date <= t.Date && (t.Date - f.Date).TotalDays <= 366)
                {
                    var end = t.Date.AddDays(1);
                    return new DevRange("custom", $"{f:yyyy-MM-dd} to {t:yyyy-MM-dd} (UTC)", DateTime.SpecifyKind(f.Date, DateTimeKind.Utc),
                        DateTime.SpecifyKind(end > now ? now : end, DateTimeKind.Utc));
                }
                break;
            case "24h": return new DevRange("24h", "Last 24 hours", now.AddHours(-24), now);
        }
        return fallback == key ? new DevRange("24h", "Last 24 hours", now.AddHours(-24), now) : Parse(fallback, null, null, fallback);
    }
}

public sealed record LogFilter(string? Severity, string? Category, string? EventType, string? Q, string? CorrelationId,
    string? Integration, Guid? OrderId, Guid? UserId, int Page = 1);

public sealed record LogPage(LogFilter Filter, DevRange Range, IReadOnlyList<TechnicalEvent> Items, int Total, int Page, int PageSize,
    IReadOnlyList<string> EventTypes)
{
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record CorrelatedAudit(DateTime TimestampUtc, string Action, string EntityName, string? Title, string? ActorRole);

public sealed record LogDetail(TechnicalEvent Event, IReadOnlyList<TechnicalEvent> SameCorrelation, IReadOnlyList<CorrelatedAudit> BusinessAudit,
    IReadOnlyList<IntegrationCall> Calls, int GroupOccurrences);

public sealed record ErrorGroup(string Fingerprint, string Severity, string Category, string EventType, string? ExceptionType,
    string Message, string? Source, int Count, DateTime FirstSeenUtc, DateTime LastSeenUtc, Guid LatestId);

public sealed record ErrorGroupDetail(ErrorGroup Group, IReadOnlyList<TechnicalEvent> Occurrences, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record RouteStat(string Method, string Route, int Requests, int ServerErrors, int ClientErrors, int Slow, double AvgMs, long MaxMs);
public sealed record TimePoint(DateTime StartUtc, int Requests, int ServerErrors, double AvgMs);
public sealed record CountItem(string Name, int Count);

public sealed record PerformanceStats(DevRange Range, int Requests, int ServerErrors, int ClientErrors, double ErrorRatePercent,
    double AvgMs, long MaxMs, int Slow, int SlowThresholdMs, IReadOnlyList<TimePoint> Timeline, IReadOnlyList<RouteStat> TopRoutes,
    IReadOnlyList<RouteStat> SlowestRoutes, IReadOnlyList<RouteStat> ErrorRoutes, IReadOnlyList<CountItem> SecurityEvents,
    IReadOnlyList<CountItem> FailuresBySubsystem, int AuthenticationFailures, int AuthorizationFailures, int RateLimited,
    DateTime MetricsSinceUtc);

public sealed record IntegrationSummary(string Name, string ConfigStatus, string ConfigDetail, string Mode, DevHealthEntry? Health,
    int Calls, int Failures, double FailureRatePercent, double AvgMs, DateTime? LastSuccessUtc, DateTime? LastFailureUtc,
    string? LastFailureDetail, IReadOnlyList<CountItem> ByOperation, IReadOnlyList<IntegrationCall> RecentFailures);

public sealed record StalePayment(string Reference, string Status, DateTime CreatedUtc, decimal Amount, Guid Id);

public sealed record PaymentDiagnostics(DevRange Range, IReadOnlyList<CountItem> Initialize, IReadOnlyList<CountItem> Verify,
    IReadOnlyList<CountItem> Webhooks, IReadOnlyList<CountItem> Refunds, int Duplicates, int Mismatches, int InvalidSignatures,
    IReadOnlyList<CountItem> PaymentStatuses, IReadOnlyList<StalePayment> StaleInitialized, IReadOnlyList<IntegrationCall> RecentProblems,
    int RefundedPayments, int FailedPayments);

public sealed record TemplateStat(string Template, int Attempted, int Succeeded, int Failed, int Skipped);

public sealed record EmailDiagnostics(DevRange Range, int Attempted, int Succeeded, int Failed, int Skipped,
    IReadOnlyList<TemplateStat> ByTemplate, IReadOnlyList<CountItem> ByRecipientCategory, IReadOnlyList<IntegrationCall> RecentFailures,
    IReadOnlyList<IntegrationCall> Recent, bool SendingEnabled, bool KeyConfigured, JobStatusSnapshot? Queue);

public sealed record MigrationInfo(string Id, bool Applied);

public sealed record DatabaseInfo(string Provider, bool CanConnect, double LatencyMs, string? Error, bool Relational,
    IReadOnlyList<MigrationInfo> Migrations, int PendingCount, string? LastApplied, IReadOnlyList<CountItem> TableRows, string SchemaNote);

public sealed record ConfigItem(string Area, string Name, string Status, string Detail);

public sealed record SystemInfo(string Environment, string Version, string InformationalVersion, DateTime BuildUtc, string Runtime,
    string Framework, string Os, string ProcessArch, int ProcessorCount, DateTime StartedUtc, TimeSpan Uptime, long WorkingSetMb,
    long GcHeapMb, int ThreadCount, string ContentRoot, string ServerTimeZone, string DatabaseProvider);

/// <summary>
/// Read-side of the Developer Portal. Every query is time-window bounded, paginated or
/// aggregated server-side, and uses AsNoTracking. Nothing here returns customer contact data,
/// raw gateway payloads, email bodies or secrets.
/// </summary>
public sealed class DevPortalQueries(
    DeChimexDbContext db,
    IMemoryCache cache,
    RequestMetricsCollector metrics,
    IJobMonitor jobs,
    IOptions<PaystackOptions> paystack,
    IOptions<BrevoOptions> brevo,
    IOptions<StorageOptions> storage,
    IOptions<SeedOptions> seed,
    IOptions<DevPortalOptions> devOptions,
    IOptions<MarketingOptions> marketing,
    IConfiguration configuration,
    IWebHostEnvironment env)
{
    public const int LogPageSize = 50;

    // ---------------- Technical log ----------------

    public async Task<LogPage> LogsAsync(LogFilter f, DevRange range, CancellationToken ct = default)
    {
        var q = db.TechnicalEvents.AsNoTracking().Where(e => e.TimestampUtc >= range.FromUtc && e.TimestampUtc <= range.ToUtc);
        if (!string.IsNullOrWhiteSpace(f.Severity))
        {
            // "Warning+" style filter: the chosen level and everything more severe.
            var min = TechnicalSeverity.Rank(f.Severity.TrimEnd('+'));
            var allowed = TechnicalSeverity.All.Where(s => TechnicalSeverity.Rank(s) >= min).ToList();
            q = f.Severity.EndsWith('+') ? q.Where(e => allowed.Contains(e.Severity)) : q.Where(e => e.Severity == f.Severity);
        }
        if (!string.IsNullOrWhiteSpace(f.Category)) q = q.Where(e => e.Category == f.Category);
        if (!string.IsNullOrWhiteSpace(f.EventType)) q = q.Where(e => e.EventType == f.EventType);
        if (!string.IsNullOrWhiteSpace(f.Integration)) q = q.Where(e => e.Integration == f.Integration);
        if (!string.IsNullOrWhiteSpace(f.CorrelationId)) q = q.Where(e => e.CorrelationId == f.CorrelationId.Trim());
        if (f.OrderId is { } oid) q = q.Where(e => e.OrderId == oid);
        if (f.UserId is { } uid) q = q.Where(e => e.UserId == uid);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var s = f.Q.Trim();
            q = q.Where(e => e.Message.Contains(s) || (e.ExceptionType != null && e.ExceptionType.Contains(s))
                             || (e.Route != null && e.Route.Contains(s)) || (e.Source != null && e.Source.Contains(s))
                             || (e.CorrelationId != null && e.CorrelationId == s));
        }

        var total = await q.CountAsync(ct);
        var page = Math.Clamp(f.Page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)LogPageSize)));
        var items = await q.OrderByDescending(e => e.TimestampUtc).Skip((page - 1) * LogPageSize).Take(LogPageSize).ToListAsync(ct);

        var eventTypes = await cache.GetOrCreateAsync("devportal:eventtypes", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return await db.TechnicalEvents.AsNoTracking().Select(x => x.EventType).Distinct().OrderBy(x => x).Take(100).ToListAsync(ct);
        }) ?? new List<string>();

        return new LogPage(f with { Page = page }, range, items, total, page, LogPageSize, eventTypes);
    }

    public async Task<LogDetail?> LogDetailAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.TechnicalEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return null;

        var same = e.CorrelationId is null ? new List<TechnicalEvent>() : await db.TechnicalEvents.AsNoTracking()
            .Where(x => x.CorrelationId == e.CorrelationId && x.Id != e.Id)
            .OrderBy(x => x.TimestampUtc).Take(50).ToListAsync(ct);

        // Business audit entries from the same request (correlated by correlation id). Only
        // non-PII columns are projected: no old/new values, no IP address.
        var audit = e.CorrelationId is null ? new List<CorrelatedAudit>() : await db.AuditLogs.AsNoTracking()
            .Where(a => a.CorrelationId == e.CorrelationId)
            .OrderBy(a => a.TimestampUtc).Take(50)
            .Select(a => new CorrelatedAudit(a.TimestampUtc, a.Action, a.EntityName, a.Title, a.ActorRole))
            .ToListAsync(ct);

        var calls = e.CorrelationId is null ? new List<IntegrationCall>() : await db.IntegrationCalls.AsNoTracking()
            .Where(c => c.CorrelationId == e.CorrelationId).OrderBy(c => c.TimestampUtc).Take(50).ToListAsync(ct);

        var occurrences = await db.TechnicalEvents.AsNoTracking().CountAsync(x => x.Fingerprint == e.Fingerprint, ct);
        return new LogDetail(e, same, audit, calls, occurrences);
    }

    // ---------------- Error center ----------------

    public async Task<IReadOnlyList<ErrorGroup>> ErrorGroupsAsync(DevRange range, string? category, bool includeWarnings, CancellationToken ct = default)
    {
        var severities = includeWarnings
            ? new[] { TechnicalSeverity.Warning, TechnicalSeverity.Error, TechnicalSeverity.Critical }
            : new[] { TechnicalSeverity.Error, TechnicalSeverity.Critical };
        var q = db.TechnicalEvents.AsNoTracking()
            .Where(e => e.TimestampUtc >= range.FromUtc && e.TimestampUtc <= range.ToUtc && severities.Contains(e.Severity));
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(e => e.Category == category);

        var groups = await q.GroupBy(e => e.Fingerprint)
            .Select(g => new { g.Key, Count = g.Count(), First = g.Min(x => x.TimestampUtc), Last = g.Max(x => x.TimestampUtc) })
            .OrderByDescending(g => g.Last).Take(200).ToListAsync(ct);
        if (groups.Count == 0) return Array.Empty<ErrorGroup>();

        var keys = groups.Select(g => g.Key).ToList();
        var samples = await q.Where(e => keys.Contains(e.Fingerprint))
            .GroupBy(e => e.Fingerprint)
            .Select(g => g.OrderByDescending(x => x.TimestampUtc)
                .Select(x => new { x.Fingerprint, x.Id, x.Severity, x.Category, x.EventType, x.ExceptionType, x.Message, x.Source })
                .First())
            .ToListAsync(ct);
        var byKey = samples.ToDictionary(s => s.Fingerprint);

        return groups.Where(g => byKey.ContainsKey(g.Key)).Select(g =>
        {
            var s = byKey[g.Key];
            return new ErrorGroup(g.Key, s.Severity, s.Category, s.EventType, s.ExceptionType, s.Message, s.Source, g.Count, g.First, g.Last, s.Id);
        }).ToList();
    }

    public async Task<ErrorGroupDetail?> ErrorGroupAsync(string fingerprint, int page, CancellationToken ct = default)
    {
        var q = db.TechnicalEvents.AsNoTracking().Where(e => e.Fingerprint == fingerprint);
        var total = await q.CountAsync(ct);
        if (total == 0) return null;
        var first = await q.MinAsync(e => e.TimestampUtc, ct);
        var latest = await q.OrderByDescending(e => e.TimestampUtc).FirstAsync(ct);
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)LogPageSize)));
        var items = await q.OrderByDescending(e => e.TimestampUtc).Skip((page - 1) * LogPageSize).Take(LogPageSize).ToListAsync(ct);
        var group = new ErrorGroup(fingerprint, latest.Severity, latest.Category, latest.EventType, latest.ExceptionType,
            latest.Message, latest.Source, total, first, latest.TimestampUtc, latest.Id);
        return new ErrorGroupDetail(group, items, total, page, LogPageSize);
    }

    // ---------------- Performance / statistics ----------------

    public async Task<PerformanceStats> PerformanceAsync(DevRange range, CancellationToken ct = default)
    {
        var fromBucket = RequestMetricsCollector.BucketOf(range.FromUtc);
        var persisted = await db.RequestMetricBuckets.AsNoTracking()
            .Where(b => b.BucketStartUtc >= fromBucket && b.BucketStartUtc <= range.ToUtc)
            .Select(b => new PendingBucket(b.BucketStartUtc, b.Method, b.Route, b.RequestCount, b.ServerErrorCount,
                b.ClientErrorCount, b.SlowCount, b.TotalDurationMs, b.MaxDurationMs))
            .ToListAsync(ct);
        // Include not-yet-flushed counters so the numbers are exact up to now.
        var all = persisted.Concat(metrics.Peek().Where(b => b.BucketStartUtc >= fromBucket && b.BucketStartUtc <= range.ToUtc)).ToList();

        var requests = all.Sum(b => b.Requests);
        var server = all.Sum(b => b.ServerErrors);
        var client = all.Sum(b => b.ClientErrors);
        var totalMs = all.Sum(b => b.TotalMs);

        var byRoute = all.GroupBy(b => (b.Method, b.Route)).Select(g =>
        {
            var n = g.Sum(x => x.Requests);
            return new RouteStat(g.Key.Method, g.Key.Route, n, g.Sum(x => x.ServerErrors), g.Sum(x => x.ClientErrors),
                g.Sum(x => x.Slow), n == 0 ? 0 : Math.Round(g.Sum(x => x.TotalMs) / (double)n, 1), g.Max(x => x.MaxMs));
        }).ToList();

        // Timeline bin size adapts to the range.
        var span = range.ToUtc - range.FromUtc;
        var bin = span <= TimeSpan.FromHours(1.1) ? TimeSpan.FromMinutes(5)
            : span <= TimeSpan.FromDays(1.1) ? TimeSpan.FromHours(1)
            : span <= TimeSpan.FromDays(8) ? TimeSpan.FromHours(6)
            : TimeSpan.FromDays(1);
        var timeline = new List<TimePoint>();
        var start = new DateTime(range.FromUtc.Ticks - range.FromUtc.Ticks % bin.Ticks, DateTimeKind.Utc);
        for (var t = start; t <= range.ToUtc; t += bin)
        {
            var end = t + bin;
            var inBin = all.Where(b => b.BucketStartUtc >= t && b.BucketStartUtc < end).ToList();
            var n = inBin.Sum(b => b.Requests);
            timeline.Add(new TimePoint(t, n, inBin.Sum(b => b.ServerErrors), n == 0 ? 0 : Math.Round(inBin.Sum(b => b.TotalMs) / (double)n, 1)));
            if (timeline.Count > 400) break;
        }

        var security = await db.TechnicalEvents.AsNoTracking()
            .Where(e => e.TimestampUtc >= range.FromUtc && e.TimestampUtc <= range.ToUtc && e.Category == TechnicalCategory.Security)
            .GroupBy(e => e.EventType).Select(g => new CountItem(g.Key, g.Count())).ToListAsync(ct);
        var failures = await db.TechnicalEvents.AsNoTracking()
            .Where(e => e.TimestampUtc >= range.FromUtc && e.TimestampUtc <= range.ToUtc
                        && (e.Severity == TechnicalSeverity.Error || e.Severity == TechnicalSeverity.Critical))
            .GroupBy(e => e.Category).Select(g => new CountItem(g.Key, g.Count())).ToListAsync(ct);
        var integrationFailures = await db.IntegrationCalls.AsNoTracking()
            .Where(c => c.TimestampUtc >= range.FromUtc && c.TimestampUtc <= range.ToUtc && !c.Succeeded && c.Outcome != "Skipped")
            .GroupBy(c => c.Integration).Select(g => new CountItem(g.Key + " calls", g.Count())).ToListAsync(ct);

        var since = await cache.GetOrCreateAsync("devportal:metrics-since", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await db.RequestMetricBuckets.AsNoTracking().OrderBy(b => b.BucketStartUtc).Select(b => (DateTime?)b.BucketStartUtc).FirstOrDefaultAsync(ct);
        });

        return new PerformanceStats(range, requests, server, client,
            requests == 0 ? 0 : Math.Round(server * 100.0 / requests, 2),
            requests == 0 ? 0 : Math.Round(totalMs / (double)requests, 1),
            all.Count == 0 ? 0 : all.Max(b => b.MaxMs), all.Sum(b => b.Slow), metrics.SlowThresholdMs, timeline,
            byRoute.OrderByDescending(r => r.Requests).Take(15).ToList(),
            byRoute.Where(r => r.Requests > 0).OrderByDescending(r => r.AvgMs).Take(10).ToList(),
            byRoute.Where(r => r.ServerErrors > 0 || r.ClientErrors > 0).OrderByDescending(r => r.ServerErrors).ThenByDescending(r => r.ClientErrors).Take(10).ToList(),
            security.OrderByDescending(s => s.Count).ToList(),
            failures.Concat(integrationFailures).OrderByDescending(s => s.Count).ToList(),
            security.Where(s => s.Name == TechnicalEventType.AuthenticationFailure).Sum(s => s.Count),
            security.Where(s => s.Name == TechnicalEventType.AuthorizationFailure).Sum(s => s.Count),
            security.Where(s => s.Name == TechnicalEventType.RateLimited).Sum(s => s.Count),
            since ?? metrics.StartedUtc);
    }

    // ---------------- Integrations ----------------

    public (string Status, string Detail, string Mode) PaystackConfig()
    {
        var o = paystack.Value;
        bool hasSecret = !string.IsNullOrWhiteSpace(o.SecretKey), hasPublic = !string.IsNullOrWhiteSpace(o.PublicKey);
        if (!hasSecret || !hasPublic)
            return ("Missing", $"{(hasSecret ? "" : "Secret key missing. ")}{(hasPublic ? "" : "Public key missing.")}".Trim(), "-");
        string ModeOf(string k, string p) => k.StartsWith(p + "_test_", StringComparison.Ordinal) ? "test"
            : k.StartsWith(p + "_live_", StringComparison.Ordinal) ? "live" : "unknown";
        var sm = ModeOf(o.SecretKey, "sk");
        var pm = ModeOf(o.PublicKey, "pk");
        if (sm == "unknown" || pm == "unknown")
            return ("Invalid", "A key does not have the expected sk_/pk_ test|live format.", sm == pm ? sm : "mixed");
        if (sm != pm)
            return ("Invalid", $"Secret key is {sm} mode but public key is {pm} mode.", "mixed");
        return ("Configured", $"Secret and public keys present ({sm} mode). API host: {HostOf(o.ApiBaseUrl)}.", sm);
    }

    public (string Status, string Detail, string Mode) BrevoConfig()
    {
        var o = brevo.Value;
        if (!o.Enabled) return ("Disabled", "Email sending is disabled (Brevo:Enabled=false); emails are logged, not sent.", "off");
        if (string.IsNullOrWhiteSpace(o.ApiKey)) return ("Missing", "Sending is enabled but no API key is configured.", "-");
        if (!o.ApiKey.StartsWith("xkeysib-", StringComparison.Ordinal))
            return ("Invalid", "API key does not have the expected xkeysib- format.", "live");
        if (string.IsNullOrWhiteSpace(o.SenderEmail)) return ("Invalid", "Sender email is not configured.", "live");
        return ("Configured", $"API key present; sender {Shared.Diagnostics.SensitiveDataRedactor.MaskEmail(o.SenderEmail)}. API host: {HostOf(o.ApiBaseUrl)}.", "live");
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : "(invalid URL)";

    public async Task<IntegrationSummary> IntegrationAsync(string name, DevRange range, DevHealthSnapshot health, CancellationToken ct = default)
    {
        var (status, detail, mode) = name == IntegrationNames.Paystack ? PaystackConfig() : BrevoConfig();
        var q = db.IntegrationCalls.AsNoTracking().Where(c => c.Integration == name && c.TimestampUtc >= range.FromUtc && c.TimestampUtc <= range.ToUtc);
        var stats = await q.GroupBy(c => new { c.Operation, c.Succeeded, Skipped = c.Outcome == "Skipped" })
            .Select(g => new { g.Key.Operation, g.Key.Succeeded, g.Key.Skipped, Count = g.Count(), Ms = g.Sum(x => x.DurationMs) })
            .ToListAsync(ct);
        var calls = stats.Where(s => !s.Skipped).Sum(s => s.Count);
        var failures = stats.Where(s => !s.Succeeded && !s.Skipped).Sum(s => s.Count);
        var ms = stats.Where(s => !s.Skipped).Sum(s => s.Ms);

        var lastSuccess = await db.IntegrationCalls.AsNoTracking().Where(c => c.Integration == name && c.Succeeded)
            .OrderByDescending(c => c.TimestampUtc).Select(c => (DateTime?)c.TimestampUtc).FirstOrDefaultAsync(ct);
        var lastFailure = await db.IntegrationCalls.AsNoTracking().Where(c => c.Integration == name && !c.Succeeded && c.Outcome != "Skipped")
            .OrderByDescending(c => c.TimestampUtc).FirstOrDefaultAsync(ct);
        var recent = await q.Where(c => !c.Succeeded && c.Outcome != "Skipped").OrderByDescending(c => c.TimestampUtc).Take(15).ToListAsync(ct);

        return new IntegrationSummary(name, status, detail, mode, health.Get(name.ToLowerInvariant()), calls, failures,
            calls == 0 ? 0 : Math.Round(failures * 100.0 / calls, 1), calls == 0 ? 0 : Math.Round(ms / (double)calls, 0),
            lastSuccess, lastFailure?.TimestampUtc, lastFailure is null ? null : $"{lastFailure.Operation} {lastFailure.Outcome}: {lastFailure.Detail}",
            stats.GroupBy(s => s.Operation).Select(g => new CountItem(g.Key, g.Sum(x => x.Count))).OrderByDescending(c => c.Count).ToList(),
            recent);
    }

    // ---------------- Payments diagnostics ----------------

    public async Task<PaymentDiagnostics> PaymentsAsync(DevRange range, CancellationToken ct = default)
    {
        var q = db.IntegrationCalls.AsNoTracking()
            .Where(c => c.Integration == IntegrationNames.Paystack && c.TimestampUtc >= range.FromUtc && c.TimestampUtc <= range.ToUtc);
        var byOp = await q.GroupBy(c => new { c.Operation, c.Outcome }).Select(g => new { g.Key.Operation, g.Key.Outcome, Count = g.Count() }).ToListAsync(ct);
        List<CountItem> Op(string op) => byOp.Where(x => x.Operation == op).Select(x => new CountItem(x.Outcome, x.Count)).OrderByDescending(x => x.Count).ToList();

        var problems = await q.Where(c => !c.Succeeded || c.Outcome == "Duplicate" || c.Outcome == "Failed")
            .OrderByDescending(c => c.TimestampUtc).Take(25).ToListAsync(ct);

        var staleCutoff = DateTime.UtcNow.AddMinutes(-30);
        var stale = await db.Payments.AsNoTracking()
            .Where(p => p.Method == PaymentMethod.Paystack && (p.Status == PaymentStatus.Initialized || p.Status == PaymentStatus.Pending) && p.CreatedDateUtc < staleCutoff)
            .OrderByDescending(p => p.CreatedDateUtc).Take(25)
            .Select(p => new StalePayment(p.Reference, p.Status.ToString(), p.CreatedDateUtc, p.Amount.Amount, p.Id))
            .ToListAsync(ct);

        var statuses = await db.Payments.AsNoTracking()
            .Where(p => p.Method == PaymentMethod.Paystack && p.CreatedDateUtc >= range.FromUtc && p.CreatedDateUtc <= range.ToUtc)
            .GroupBy(p => p.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        var webhooks = Op("webhook");
        return new PaymentDiagnostics(range, Op("initialize"), Op("verify"), webhooks, Op("refund"),
            webhooks.Where(w => w.Name == "Duplicate").Sum(w => w.Count),
            webhooks.Where(w => w.Name is "AmountMismatch" or "CurrencyMismatch").Sum(w => w.Count),
            webhooks.Where(w => w.Name == "Rejected").Sum(w => w.Count),
            statuses.Select(s => new CountItem(s.Key.ToString(), s.Count)).OrderByDescending(s => s.Count).ToList(),
            stale, problems,
            statuses.Where(s => s.Key == PaymentStatus.Refunded).Sum(s => s.Count),
            statuses.Where(s => s.Key is PaymentStatus.Failed or PaymentStatus.Abandoned).Sum(s => s.Count));
    }

    // ---------------- Email diagnostics ----------------

    public async Task<EmailDiagnostics> EmailAsync(DevRange range, CancellationToken ct = default)
    {
        var q = db.IntegrationCalls.AsNoTracking()
            .Where(c => c.Integration == IntegrationNames.Brevo && c.TimestampUtc >= range.FromUtc && c.TimestampUtc <= range.ToUtc);
        var grouped = await q.GroupBy(c => new { c.Template, c.Outcome, c.Succeeded })
            .Select(g => new { g.Key.Template, g.Key.Outcome, g.Key.Succeeded, Count = g.Count() }).ToListAsync(ct);
        var cats = await q.GroupBy(c => c.RecipientCategory).Select(g => new CountItem(g.Key ?? "unspecified", g.Count())).ToListAsync(ct);
        var failures = await q.Where(c => !c.Succeeded && c.Outcome != "Skipped").OrderByDescending(c => c.TimestampUtc).Take(25).ToListAsync(ct);
        var recent = await q.OrderByDescending(c => c.TimestampUtc).Take(25).ToListAsync(ct);

        var byTemplate = grouped.GroupBy(g => g.Template ?? "Unspecified").Select(g => new TemplateStat(g.Key,
            g.Sum(x => x.Count), g.Where(x => x.Succeeded).Sum(x => x.Count),
            g.Where(x => !x.Succeeded && x.Outcome != "Skipped").Sum(x => x.Count),
            g.Where(x => x.Outcome == "Skipped").Sum(x => x.Count))).OrderByDescending(t => t.Attempted).ToList();

        return new EmailDiagnostics(range, grouped.Sum(g => g.Count), grouped.Where(g => g.Succeeded).Sum(g => g.Count),
            grouped.Where(g => !g.Succeeded && g.Outcome != "Skipped").Sum(g => g.Count), grouped.Where(g => g.Outcome == "Skipped").Sum(g => g.Count),
            byTemplate, cats.OrderByDescending(c => c.Count).ToList(), failures, recent,
            brevo.Value.Enabled, !string.IsNullOrWhiteSpace(brevo.Value.ApiKey),
            jobs.Snapshot().FirstOrDefault(j => j.Key == Infrastructure.Email.BackgroundEmailQueue.JobKey));
    }

    // ---------------- Database ----------------

    public async Task<DatabaseInfo> DatabaseAsync(bool force, CancellationToken ct = default)
    {
        if (!force && cache.TryGetValue("devportal:db", out DatabaseInfo? cached) && cached is not null) return cached;

        var provider = db.Database.ProviderName ?? "unknown";
        var relational = db.Database.IsRelational();
        var sw = Stopwatch.StartNew();
        bool can; string? error = null;
        try { can = await db.Database.CanConnectAsync(ct); }
        catch (Exception ex) { can = false; error = ex.GetType().Name; }
        var latency = Math.Round(sw.Elapsed.TotalMilliseconds, 1);

        // GetMigrations() is relational-only; for the in-memory preview read the migration ids
        // from the [Migration] attributes of the migrations assembly instead.
        var all = relational
            ? db.Database.GetMigrations().ToList()
            : db.GetType().Assembly.GetTypes()
                .Select(t => t.GetCustomAttributes(typeof(Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute), false)
                    .OfType<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>().FirstOrDefault()?.Id)
                .Where(id => id is not null).Select(id => id!).OrderBy(id => id, StringComparer.Ordinal).ToList();
        HashSet<string> applied;
        string note;
        if (relational && can)
        {
            applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet();
            note = "Schema managed by EF Core migrations (applied automatically at startup by DatabaseSeeder).";
        }
        else if (relational)
        {
            applied = new HashSet<string>();
            note = "Database unreachable: migration state unknown.";
        }
        else
        {
            applied = all.ToHashSet();
            note = "In-memory provider (preview/testing): schema is created from the model with EnsureCreated; migrations are listed for reference only.";
        }
        var migrations = all.Select(m => new MigrationInfo(m, applied.Contains(m))).Reverse().ToList();

        var rows = new List<CountItem>();
        if (can)
        {
            rows.Add(new("Users", await db.Users.CountAsync(ct)));
            rows.Add(new("Orders", await db.Orders.CountAsync(ct)));
            rows.Add(new("Payments", await db.Payments.CountAsync(ct)));
            rows.Add(new("AuditLogs", await db.AuditLogs.CountAsync(ct)));
            rows.Add(new("TechnicalEvents", await db.TechnicalEvents.CountAsync(ct)));
            rows.Add(new("IntegrationCalls", await db.IntegrationCalls.CountAsync(ct)));
            rows.Add(new("RequestMetricBuckets", await db.RequestMetricBuckets.CountAsync(ct)));
        }

        var info = new DatabaseInfo(provider.Replace("Microsoft.EntityFrameworkCore.", string.Empty), can, latency, error, relational,
            migrations, relational && can ? all.Count(m => !applied.Contains(m)) : 0,
            applied.Count == 0 ? null : all.LastOrDefault(applied.Contains), rows, note);
        cache.Set("devportal:db", info, TimeSpan.FromSeconds(60));
        return info;
    }

    // ---------------- Configuration ----------------

    public IReadOnlyList<ConfigItem> Configuration()
    {
        var items = new List<ConfigItem>();
        var (ps, pd, pm) = PaystackConfig();
        items.Add(new("Payments", "Paystack keys", ps, pd));
        items.Add(new("Payments", "Paystack mode", pm == "live" ? "Live" : pm == "test" ? "Test" : ps, pm switch
        {
            "live" => "LIVE keys: real money moves.",
            "test" => "Test keys: no real money moves.",
            _ => "Mode cannot be determined."
        }));
        items.Add(new("Payments", "Customer-borne charge", paystack.Value.CustomerBorneCharge ? "Enabled" : "Disabled",
            "Whether the configured share of Paystack's fee is added to the customer payable."));
        items.Add(new("Payments", "Currency", "Configured", paystack.Value.Currency));

        var (bs, bd, _) = BrevoConfig();
        items.Add(new("Email", "Brevo", bs, bd));
        items.Add(new("Email", "Dev outbox", string.IsNullOrWhiteSpace(brevo.Value.DevOutboxPath) ? "Not set" : "Configured",
            "Local file copy of emails when sending is disabled (development only)."));

        var cs = configuration.GetConnectionString("DefaultConnection");
        var inMemory = string.Equals(configuration["Database:UseInMemory"], "true", StringComparison.OrdinalIgnoreCase);
        items.Add(new("Database", "Connection string", string.IsNullOrWhiteSpace(cs) ? "Missing" : "Configured",
            inMemory ? "Configured, but the in-memory provider is active (Database:UseInMemory=true)." : "Present (value never displayed)."));
        items.Add(new("Database", "Provider", "Configured", inMemory ? "EF Core InMemory" : "SQL Server"));

        var root = Path.GetFullPath(storage.Value.LocalRootPath);
        items.Add(new("Storage", "Provider", "Configured", storage.Value.Provider));
        items.Add(new("Storage", "Upload root", Directory.Exists(root) ? "Configured" : "Missing",
            Directory.Exists(root) ? "Directory exists." : "Directory does not exist yet (created on first upload)."));
        items.Add(new("Storage", "Max upload size", "Configured", $"{storage.Value.MaxFileSizeBytes / 1024 / 1024} MB"));

        items.Add(new("Accounts", "Owner bootstrap", string.IsNullOrWhiteSpace(seed.Value.OwnerEmail) ? "Missing" : "Configured",
            "Seeded once; password change forced on first login."));
        items.Add(new("Accounts", "Developer account seed",
            string.IsNullOrWhiteSpace(seed.Value.DeveloperEmail) || string.IsNullOrWhiteSpace(seed.Value.DeveloperPassword) ? "Not set" : "Configured",
            "Seed:DeveloperEmail / Seed:DeveloperPassword (use environment variables outside Development)."));

        items.Add(new("Marketing", "Broadcast sending", marketing.Value.AllowBroadcasts ? "Enabled" : "Disabled",
            "Marketing:AllowBroadcasts safeguard."));

        var permit = configuration["RateLimiting:PermitLimit"];
        items.Add(new("Security", "Rate limiting", "Configured", permit is null ? "Defaults" : $"{permit} requests per window per IP"));
        items.Add(new("Security", "HTTPS/HSTS", env.IsDevelopment() ? "Development" : "Enabled",
            env.IsDevelopment() ? "HSTS and HTTPS redirection are off in Development." : "HSTS + HTTPS redirection active."));

        var o = devOptions.Value;
        items.Add(new("Developer Portal", "Retention", "Configured", $"Events/integration calls {o.RetentionDays} days; request metrics {o.MetricsRetentionDays} days."));
        items.Add(new("Developer Portal", "Thresholds", "Configured", $"Slow request ≥ {o.SlowRequestMs} ms; slow DB command ≥ {o.SlowDbCommandMs} ms."));
        items.Add(new("Developer Portal", "Realtime alerts", o.RealtimeAlerts ? "Enabled" : "Disabled", "Error/Critical events pushed over /hubs/dev."));
        items.Add(new("Developer Portal", "Reporting time zone", "Configured", o.ReportingTimeZone));

        var logPath = configuration["Serilog:WriteTo:1:Args:path"];
        items.Add(new("Logging", "Serilog file sink", string.IsNullOrWhiteSpace(logPath) ? "Not set" : "Configured",
            string.IsNullOrWhiteSpace(logPath) ? "Console only." : $"Rolling file {logPath}"));
        return items;
    }

    // ---------------- System ----------------

    private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public SystemInfo System()
    {
        var asm = typeof(Program).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "-";
        var buildUtc = File.Exists(asm.Location) ? File.GetLastWriteTimeUtc(asm.Location) : DateTime.MinValue;
        using var proc = Process.GetCurrentProcess();
        return new SystemInfo(env.EnvironmentName, asm.GetName().Version?.ToString() ?? "-", info, buildUtc,
            RuntimeInformation.FrameworkDescription, Environment.Version.ToString(), RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, ProcessStartUtc,
            DateTime.UtcNow - ProcessStartUtc, proc.WorkingSet64 / 1024 / 1024, GC.GetTotalMemory(false) / 1024 / 1024,
            proc.Threads.Count, env.ContentRootPath, TimeZoneInfo.Local.Id,
            (db.Database.ProviderName ?? "unknown").Replace("Microsoft.EntityFrameworkCore.", string.Empty));
    }

    // ---------------- Dashboard counters ----------------

    public async Task<(int Critical, int Errors, int Warnings, int Security)> EventCountsAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        var rows = await db.TechnicalEvents.AsNoTracking().Where(e => e.TimestampUtc >= sinceUtc)
            .GroupBy(e => new { e.Severity, IsSecurity = e.Category == TechnicalCategory.Security })
            .Select(g => new { g.Key.Severity, g.Key.IsSecurity, Count = g.Count() }).ToListAsync(ct);
        return (rows.Where(r => r.Severity == TechnicalSeverity.Critical).Sum(r => r.Count),
            rows.Where(r => r.Severity == TechnicalSeverity.Error).Sum(r => r.Count),
            rows.Where(r => r.Severity == TechnicalSeverity.Warning).Sum(r => r.Count),
            rows.Where(r => r.IsSecurity).Sum(r => r.Count));
    }

    public async Task<IReadOnlyList<TechnicalEvent>> RecentProblemsAsync(int take, CancellationToken ct = default)
        => await db.TechnicalEvents.AsNoTracking()
            .Where(e => e.Severity == TechnicalSeverity.Error || e.Severity == TechnicalSeverity.Critical)
            .OrderByDescending(e => e.TimestampUtc).Take(take).ToListAsync(ct);

    public async Task<int> IntegrationFailuresAsync(string integration, DateTime sinceUtc, CancellationToken ct = default)
        => await db.IntegrationCalls.AsNoTracking()
            .CountAsync(c => c.Integration == integration && c.TimestampUtc >= sinceUtc && !c.Succeeded && c.Outcome != "Skipped", ct);
}
