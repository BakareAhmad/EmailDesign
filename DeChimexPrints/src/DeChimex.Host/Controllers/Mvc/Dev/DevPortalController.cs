using DeChimex.Application.Common.Interfaces;
using DeChimex.Host.DevPortal;
using DeChimex.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>Developer Portal overview screens: dashboard, health, performance, jobs, database, configuration.</summary>
public sealed class DevPortalController(
    DevPortalQueries queries,
    DevHealthService health,
    ServiceFeeReportService fees,
    IJobMonitor jobs,
    DiagnosticsQueue diagnosticsQueue) : DevControllerBase
{
    [HttpGet("/dev")]
    public Task<IActionResult> Index(CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Dashboard";
        ViewData["DevNav"] = "dashboard";
        return DevView("Dashboard", await BuildDashboardAsync(ct));
    });

    /// <summary>Lightweight JSON for the dashboard's auto-refresh (uses the 30s health cache).</summary>
    [HttpGet("/dev/api/status")]
    public Task<IActionResult> Status(CancellationToken ct) => UnlessClientAborted(async () =>
    {
        var m = await BuildDashboardAsync(ct);
        return Json(new
        {
            overall = m.Overall,
            checkedAtUtc = m.Health.CheckedAtUtc,
            tiles = m.Tiles.Select(t => new { t.Key, t.Title, t.Status, t.Summary, t.Reason, t.Href }),
            last24h = new { m.Last24h.Critical, m.Last24h.Errors, m.Last24h.Warnings, m.Last24h.Security }
        });
    });

    private async Task<DevDashboardModel> BuildDashboardAsync(CancellationToken ct)
    {
        var snapshot = await health.GetAsync(false, ct);
        var hour = await queries.PerformanceAsync(DevRange.Parse("1h", null, null), ct);
        var since1h = DateTime.UtcNow.AddHours(-1);
        var counts1h = await queries.EventCountsAsync(since1h, ct);
        var jobList = jobs.Snapshot();
        var tiles = DevDashboardBuilder.Tiles(snapshot, hour,
            counts1h.Errors + counts1h.Critical,
            hour.FailuresBySubsystem.Where(f => f.Name == TechnicalCategory.Database).Sum(f => f.Count),
            await queries.IntegrationFailuresAsync(IntegrationNames.Paystack, since1h, ct),
            await queries.IntegrationFailuresAsync(IntegrationNames.Brevo, since1h, ct),
            queries.PaystackConfig(), queries.BrevoConfig(), jobList);
        var overall = DevStatus.Worst(tiles.Select(t => t.Status));
        var feeOverview = await fees.OverviewAsync(null, null, ct);
        return new DevDashboardModel(overall, tiles, snapshot, queries.System(),
            await queries.EventCountsAsync(DateTime.UtcNow.AddHours(-24), ct), hour,
            await queries.RecentProblemsAsync(8, ct), jobList,
            feeOverview.Periods.First(p => p.Period.Key == "today").Current,
            feeOverview.Periods.First(p => p.Period.Key == "month").Current,
            diagnosticsQueue.Dropped, diagnosticsQueue.Pending);
    }

    [HttpGet("/dev/health")]
    public Task<IActionResult> Health(CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "System health";
        ViewData["DevNav"] = "health";
        ViewBag.System = queries.System();
        ViewBag.Db = await queries.DatabaseAsync(false, ct);
        ViewBag.Perf = await queries.PerformanceAsync(DevRange.Parse("24h", null, null), ct);
        ViewBag.Queue = (diagnosticsQueue.Enqueued, diagnosticsQueue.Written, diagnosticsQueue.Dropped, diagnosticsQueue.Pending, diagnosticsQueue.WriteFailures, diagnosticsQueue.CaptureFailures);
        ViewBag.Realtime = HttpContext.RequestServices.GetRequiredService<RealtimeMonitor>();
        return DevView("Health", await health.GetAsync(false, ct));
    });

    [HttpGet("/dev/performance")]
    public Task<IActionResult> Performance(string? range, string? from, string? to, CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Statistics & performance";
        ViewData["DevNav"] = "performance";
        return DevView("Performance", await queries.PerformanceAsync(DevRange.Parse(range, from, to), ct));
    });

    [HttpGet("/dev/jobs")]
    public IActionResult Jobs()
    {
        ViewData["Title"] = "Background jobs";
        ViewData["DevNav"] = "jobs";
        return DevView("Jobs", jobs.Snapshot());
    }

    [HttpGet("/dev/database")]
    public Task<IActionResult> Database(CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Database & migrations";
        ViewData["DevNav"] = "database";
        return DevView("Database", await queries.DatabaseAsync(false, ct));
    });

    [HttpGet("/dev/configuration")]
    public IActionResult Configuration()
    {
        ViewData["Title"] = "Configuration";
        ViewData["DevNav"] = "configuration";
        ViewBag.System = queries.System();
        return DevView("Configuration", queries.Configuration());
    }
}
