using System.Globalization;
using System.Text;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Host.DevPortal;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>Service-fee accumulation, ledger, per-transaction detail and reconciliation export (dev.servicefees.view).</summary>
[DevPermission(Permissions.Developer.ServiceFeesView)]
public sealed class DevServiceFeesController(ServiceFeeReportService fees, ITechnicalEventRecorder recorder) : DevControllerBase
{
    [HttpGet("/dev/service-fees")]
    public Task<IActionResult> Index(string? from, string? to, CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Service fees";
        ViewData["DevNav"] = "fees";
        var (f, t, error) = ParseRange(from, to);
        if (error is not null) ViewData["Error"] = error;
        ViewBag.From = from; ViewBag.To = to;
        return DevView("ServiceFees", await fees.OverviewAsync(f, t, ct));
    });

    [HttpGet("/dev/service-fees/ledger")]
    public Task<IActionResult> Ledger(string? from, string? to, string? status, string? q, int page = 1, CancellationToken ct = default) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Service-fee ledger";
        ViewData["DevNav"] = "fees";
        var (f, t, error) = ParseRange(from, to);
        if (error is not null) { ViewData["Error"] = error; f = null; t = null; }
        var filter = new FeeLedgerFilter(f, t, NormaliseStatus(status), string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 80)], Math.Max(1, page));
        return DevView("ServiceFeeLedger", await fees.LedgerAsync(filter, ct));
    });

    [HttpGet("/dev/service-fees/{id:guid}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken ct) => UnlessClientAborted(async () =>
    {
        var d = await fees.DetailAsync(id, ct);
        if (d is null)
        {
            TempData["Error"] = "No Paystack payment with that id.";
            return Redirect("/dev/service-fees/ledger");
        }
        ViewData["Title"] = "Service-fee detail";
        ViewData["DevNav"] = "fees";
        return DevView("ServiceFeeDetail", d);
    });

    [HttpGet("/dev/service-fees/export")]
    public Task<IActionResult> Export(string? from, string? to, string? status, string? q, CancellationToken ct) => UnlessClientAborted(async () =>
    {
        var (f, t, error) = ParseRange(from, to);
        if (error is not null)
        {
            TempData["Error"] = error;
            return Redirect("/dev/service-fees/ledger");
        }
        var filter = new FeeLedgerFilter(f, t, NormaliseStatus(status), string.IsNullOrWhiteSpace(q) ? null : q.Trim());
        var (csv, rows, collected, reversed, net) = await fees.ExportCsvAsync(filter, ct);
        recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Information, TechnicalCategory.Maintenance, "ServiceFeeExport",
            $"Service-fee ledger exported: {rows} row(s), collected {collected:0.00}, reversed {reversed:0.00}, net {net:0.00}.",
            UserId: CurrentUser.UserId, Source: nameof(DevServiceFeesController)));
        var name = $"service-fees_{(f?.ToString("yyyyMMdd") ?? "all")}_{(t?.ToString("yyyyMMdd") ?? "now")}.csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", name);
    });

    private static string? NormaliseStatus(string? s) => s is "collected" or "refunded" or "notcollected" ? s : null;

    /// <summary>Validates yyyy-MM-dd local dates. Both or neither; from ≤ to; not in the future beyond today.</summary>
    public static (DateTime? From, DateTime? To, string? Error) ParseRange(string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to)) return (null, null, null);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return (null, null, "Choose both a start and an end date for a custom range.");
        if (!DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            || !DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return (null, null, "Dates must be in yyyy-MM-dd format.");
        if (f > t) return (null, null, "The start date must be on or before the end date.");
        return (f, t, null);
    }
}
