using DeChimex.Host.DevPortal;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>Technical audit log + error center (dev.logs.view).</summary>
[DevPermission(Permissions.Developer.LogsView)]
public sealed class DevLogsController(DevPortalQueries queries) : DevControllerBase
{
    [HttpGet("/dev/logs")]
    public Task<IActionResult> Index(string? range, string? from, string? to, string? severity, string? category,
        string? eventType, string? q, string? correlationId, string? integration, Guid? orderId, Guid? userId, int page = 1,
        CancellationToken ct = default) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Technical log";
        ViewData["DevNav"] = "logs";
        var filter = new LogFilter(Clean(severity), Clean(category), Clean(eventType), Clean(q, 200), Clean(correlationId, 64),
            Clean(integration), orderId, userId, Math.Max(1, page));
        return DevView("Logs", await queries.LogsAsync(filter, DevRange.Parse(range, from, to), ct));
    });

    [HttpGet("/dev/logs/{id:guid}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken ct) => UnlessClientAborted(async () =>
    {
        var detail = await queries.LogDetailAsync(id, ct);
        if (detail is null)
        {
            TempData["Error"] = "That technical event no longer exists (it may have been removed by retention).";
            return Redirect("/dev/logs");
        }
        ViewData["Title"] = "Technical event";
        ViewData["DevNav"] = "logs";
        return DevView("LogDetail", detail);
    });

    [HttpGet("/dev/errors")]
    public Task<IActionResult> Errors(string? range, string? from, string? to, string? category, bool warnings = false,
        CancellationToken ct = default) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Error center";
        ViewData["DevNav"] = "errors";
        var r = DevRange.Parse(range, from, to, "7d");
        ViewBag.Range = r;
        ViewBag.Category = Clean(category);
        ViewBag.Warnings = warnings;
        return DevView("Errors", await queries.ErrorGroupsAsync(r, Clean(category), warnings, ct));
    });

    [HttpGet("/dev/errors/{fingerprint:regex(^[[0-9a-f]]{{40}}$)}")]
    public Task<IActionResult> ErrorGroup(string fingerprint, int page = 1, CancellationToken ct = default) => UnlessClientAborted(async () =>
    {
        var detail = await queries.ErrorGroupAsync(fingerprint, page, ct);
        if (detail is null)
        {
            TempData["Error"] = "That error group has no remaining occurrences.";
            return Redirect("/dev/errors");
        }
        ViewData["Title"] = "Error group";
        ViewData["DevNav"] = "errors";
        return DevView("ErrorGroup", detail);
    });

    private static string? Clean(string? s, int max = 64)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
