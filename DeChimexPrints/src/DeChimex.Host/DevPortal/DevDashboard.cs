using DeChimex.Application.Common.Interfaces;

namespace DeChimex.Host.DevPortal;

public sealed record DevTile(string Key, string Title, string Status, string Summary, string? Reason, string Href);

public sealed record DevDashboardModel(string Overall, IReadOnlyList<DevTile> Tiles, DevHealthSnapshot Health, SystemInfo System,
    (int Critical, int Errors, int Warnings, int Security) Last24h, PerformanceStats LastHour,
    IReadOnlyList<Domain.Entities.Diagnostics.TechnicalEvent> RecentProblems, IReadOnlyList<JobStatusSnapshot> Jobs,
    FeeTotals FeesToday, FeeTotals FeesMonth, long QueueDropped, int QueuePending);

/// <summary>
/// Builds the dashboard: one tile per subsystem with a developer-facing state
/// (Healthy / Warning / Degraded / Unavailable), a one-line reason, and a link to the screen
/// that explains it. Health-check results map directly (Healthy→Healthy, Degraded→Degraded,
/// Unhealthy→Unavailable); "Warning" means the check passes but recent real failures exist.
/// </summary>
public static class DevDashboardBuilder
{
    public static IReadOnlyList<DevTile> Tiles(DevHealthSnapshot h, PerformanceStats hour, int appErrors1h, int dbErrors1h,
        int paystackFailures1h, int emailFailures1h, (string Status, string Detail, string Mode) paystackCfg,
        (string Status, string Detail, string Mode) brevoCfg, IReadOnlyList<JobStatusSnapshot> jobs)
    {
        var tiles = new List<DevTile>();

        // Application: 5xx rate over the last hour + unhandled errors.
        string appStatus; string? appReason = null;
        if (hour.Requests >= 20 && hour.ErrorRatePercent >= 5) { appStatus = DevStatus.Degraded; appReason = $"{hour.ErrorRatePercent}% of requests failed with 5xx in the last hour."; }
        else if (hour.ServerErrors > 0 || appErrors1h > 0) { appStatus = DevStatus.Warning; appReason = $"{hour.ServerErrors} server error response(s), {appErrors1h} application error event(s) in the last hour."; }
        else appStatus = DevStatus.Healthy;
        tiles.Add(new DevTile("app", "Application", appStatus,
            $"{hour.Requests} req/h · avg {hour.AvgMs} ms · {hour.ErrorRatePercent}% 5xx", appReason,
            appStatus == DevStatus.Healthy ? "/dev/performance?range=1h" : "/dev/errors?range=24h"));

        tiles.Add(FromCheck("db", "Database", h.Get("database"), dbErrors1h, "database error event(s) in the last hour", "/dev/database"));

        var ps = FromCheck("paystack", "Paystack", h.Get("paystack"), paystackFailures1h, "failed Paystack call(s)/webhook(s) in the last hour", "/dev/integrations#paystack");
        if (paystackCfg.Status is "Missing" or "Invalid")
            ps = ps with { Status = DevStatus.Unavailable, Reason = paystackCfg.Detail, Href = "/dev/configuration" };
        else ps = ps with { Summary = $"{ps.Summary} · {paystackCfg.Mode} mode" };
        tiles.Add(ps);

        var em = FromCheck("email", "Email (Brevo)", h.Get("brevo"), emailFailures1h, "failed email send(s) in the last hour", "/dev/email");
        if (brevoCfg.Status is "Missing" or "Invalid")
            em = em with { Status = DevStatus.Unavailable, Reason = brevoCfg.Detail, Href = "/dev/configuration" };
        else if (brevoCfg.Status == "Disabled" && DevStatus.Rank(em.Status) < DevStatus.Rank(DevStatus.Warning))
            em = em with { Status = DevStatus.Warning, Reason = "Email sending is disabled; messages are logged, not delivered." };
        tiles.Add(em);

        tiles.Add(FromCheck("storage", "Storage", h.Get("storage"), 0, "", "/dev/health#storage"));
        tiles.Add(FromCheck("realtime", "SignalR", h.Get("realtime"), 0, "", "/dev/health#realtime"));

        var failing = jobs.Count(j => j.State == "Failing");
        var stale = jobs.Count(j => j.State == "Stale");
        var jobStatus = failing > 0 ? DevStatus.Degraded : stale > 0 ? DevStatus.Warning
            : jobs.Any(j => j.FailureCount > 0 && j.LastFailureUtc > DateTime.UtcNow.AddHours(-1)) ? DevStatus.Warning : DevStatus.Healthy;
        tiles.Add(new DevTile("jobs", "Background jobs", jobStatus, $"{jobs.Count} monitored · {failing} failing · {stale} stale",
            jobStatus == DevStatus.Healthy ? null : string.Join("; ", jobs.Where(j => j.State is "Failing" or "Stale" || (j.LastFailureUtc > DateTime.UtcNow.AddHours(-1)))
                .Select(j => $"{j.Name}: {j.State}{(j.LastFailureMessage is null ? "" : " — " + j.LastFailureMessage)}")), "/dev/jobs"));
        return tiles;
    }

    private static DevTile FromCheck(string key, string title, DevHealthEntry? e, int recentFailures, string failureText, string href)
    {
        if (e is null) return new DevTile(key, title, DevStatus.Unavailable, "No health check registered", null, href);
        var status = e.Status;
        string? reason = status == DevStatus.Healthy ? null : (e.Error is null ? e.Description : $"{e.Description} ({e.Error})");
        if (status == DevStatus.Healthy && recentFailures > 0)
        {
            status = DevStatus.Warning;
            reason = $"{recentFailures} {failureText}.";
        }
        var summary = string.IsNullOrWhiteSpace(e.Description)
            ? $"{(e.Status == DevStatus.Healthy ? "Responding" : e.Status)} · {e.DurationMs:0.#} ms"
            : $"{e.Description} · {e.DurationMs:0.#} ms";
        return new DevTile(key, title, status, summary, reason, href);
    }
}
