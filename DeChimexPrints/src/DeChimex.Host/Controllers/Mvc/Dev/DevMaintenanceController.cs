using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Domain.Enums;
using DeChimex.Host.DevPortal;
using DeChimex.Infrastructure.Diagnostics;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>
/// SAFE maintenance actions only. Every action: POST + anti-forgery token + an explicit
/// confirmation field + dev.maintenance.run (live-checked) + a technical audit event
/// "Developer X ran Y" + idempotent (repeating it is harmless). There is deliberately NO SQL
/// console, shell, file browser, arbitrary email sender, webhook replay or data deletion.
/// </summary>
[DevPermission(Permissions.Developer.MaintenanceRun)]
public sealed class DevMaintenanceController(
    DevHealthService health,
    ServiceFeeReportService fees,
    IMemoryCache cache,
    DiagnosticsRetention retention,
    DiagnosticsWriterService writer,
    MetricsFlushService metricsFlush,
    DeChimexDbContext db,
    ITechnicalEventRecorder recorder) : DevControllerBase
{
    public static readonly IReadOnlyList<(string Key, string Title, string Description, bool NeedsReference)> Actions = new[]
    {
        ("rerun-health", "Re-run health checks", "Runs every health check now, bypassing the 30-second cache.", false),
        ("clear-cache", "Clear application cache", "Evicts all in-memory cache entries (settings, catalogue shell, portal caches). Data is re-read from the database on next use.", false),
        ("flush-diagnostics", "Flush diagnostics buffers", "Writes queued technical events, integration calls and request metrics to the database now.", false),
        ("apply-retention", "Apply diagnostics retention", "Deletes diagnostics rows older than the retention window. Only the three diagnostics tables are touched; business data never.", false),
        ("reverify-payment", "Re-verify a pending Paystack payment", "Asks Paystack for the current status of ONE Initialized/Pending payment and applies the normal verification rules. Already-settled payments are left unchanged.", true),
        ("recompute-spending", "Recompute lifetime spending", "Recomputes every customer's LifetimeSpending from order history (orders with a successful payment, excluding refunded — the exact pair the write paths use) and corrects any drift, reporting each corrected customer. Safe: the recomputed value can only equal what the normal write paths would have produced.", false),
    };

    [HttpGet("/dev/maintenance")]
    public Task<IActionResult> Index(CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Maintenance";
        ViewData["DevNav"] = "maintenance";
        var recent = await db.TechnicalEvents.AsNoTracking()
            .Where(e => e.EventType == TechnicalEventType.MaintenanceAction)
            .OrderByDescending(e => e.TimestampUtc).Take(15).ToListAsync(ct);
        return DevView("Maintenance", recent);
    });

    [HttpPost("/dev/maintenance/{operation}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(string operation, string? confirm, string? reference, CancellationToken ct)
    {
        var action = operation;
        var def = Actions.FirstOrDefault(a => a.Key == action);
        if (def.Key is null)
        {
            TempData["Error"] = "Unknown maintenance action.";
            return Redirect("/dev/maintenance");
        }
        if (!string.Equals(confirm, "yes", StringComparison.Ordinal))
        {
            TempData["Error"] = $"'{def.Title}' was not run: tick the confirmation box first.";
            return Redirect("/dev/maintenance");
        }

        string result;
        var ok = true;
        try
        {
            switch (action)
            {
                case "rerun-health":
                    var snap = await health.GetAsync(force: true, ct);
                    result = $"Overall {snap.Overall}: " + string.Join(", ", snap.Entries.Select(e => $"{e.Name} {e.Status}"));
                    break;
                case "clear-cache":
                    var before = (cache as MemoryCache)?.Count ?? -1;
                    (cache as MemoryCache)?.Compact(1.0);
                    fees.Invalidate();
                    result = before >= 0 ? $"{before} cache entr{(before == 1 ? "y" : "ies")} evicted." : "Cache cleared.";
                    break;
                case "flush-diagnostics":
                    var buckets = await metricsFlush.FlushAsync(ct);
                    await writer.DrainAsync(ct);
                    result = $"Flushed {buckets} metric bucket(s) and the technical event queue.";
                    break;
                case "apply-retention":
                    var r = await retention.ApplyAsync(ct);
                    result = $"Deleted {r.TechnicalEvents} technical event(s), {r.IntegrationCalls} integration call(s), {r.MetricBuckets} metric bucket(s) past retention.";
                    break;
                case "reverify-payment":
                    (ok, result) = await ReverifyAsync(reference, ct);
                    break;
                case "recompute-spending":
                    (ok, result) = await RecomputeSpendingAsync(ct);
                    break;
                default:
                    result = "Not implemented.";
                    ok = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            ok = false;
            // Include the message: these are operator-facing diagnostics for SAFE actions,
            // and the type name alone made failures undebuggable.
            result = $"Failed: {ex.GetType().Name}: {ex.Message}";
        }

        var who = User.FindFirst("name")?.Value ?? "developer";
        recorder.Record(new TechnicalEventEntry(
            ok ? TechnicalSeverity.Information : TechnicalSeverity.Warning,
            TechnicalCategory.Maintenance, TechnicalEventType.MaintenanceAction,
            $"Developer {who} ran '{def.Title}': {result}",
            UserId: CurrentUser.UserId, Source: nameof(DevMaintenanceController),
            MessageTemplate: $"Developer ran '{def.Title}'",
            Properties: new Dictionary<string, string?> { ["action"] = action, ["reference"] = reference, ["ok"] = ok.ToString() }));

        TempData[ok ? "Success" : "Error"] = $"{def.Title}: {result}";
        return Redirect("/dev/maintenance");
    }

    /// <summary>
    /// AUD-034: reconcile the denormalised Customer.LifetimeSpending against history.
    /// The recomputed rule mirrors the write paths exactly: +GrandTotal when a payment is
    /// confirmed (Succeeded), −GrandTotal when the order is refunded — i.e. the SUM of
    /// GrandTotal over orders that (a) have a Succeeded payment and (b) are not Refunded.
    /// </summary>
    private async Task<(bool Ok, string Result)> RecomputeSpendingAsync(CancellationToken ct)
    {
        var paidOrderIds = db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Succeeded)
            .Select(p => p.OrderId).Distinct();
        // Project the owned Money scalar first, then group in memory: GroupBy+Sum over an
        // owned-type property has no InMemory translation (and this is a maintenance tool —
        // the customer count is small by nature).
        var rows = await db.Orders.AsNoTracking()
            .Where(o => paidOrderIds.Contains(o.Id) && o.Status != OrderStatus.Refunded)
            .Select(o => new { o.CustomerId, Total = o.GrandTotal.Amount })
            .ToListAsync(ct);
        var actualByCustomer = rows.GroupBy(r => r.CustomerId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Total));

        var customers = await db.Customers.ToListAsync(ct);
        var corrected = 0;
        var details = new List<string>();
        foreach (var c in customers)
        {
            var expected = actualByCustomer.GetValueOrDefault(c.Id, 0m);
            if (c.LifetimeSpending.Amount == expected) continue;
            // Guests may have no email — label them by id instead of throwing.
            details.Add($"{c.Email?.Value ?? $"customer {c.Id:n}"[..Math.Min(40, (c.Email?.Value ?? $"customer {c.Id:n}").Length)]}: {c.LifetimeSpending.Amount:N2} → {expected:N2}");
            c.RecomputeSpending(DeChimex.Domain.ValueObjects.Money.Of(expected, c.LifetimeSpending.Currency));
            corrected++;
        }
        if (corrected > 0)
            await db.SaveChangesAsync(ct);

        return (true, corrected == 0
            ? $"All {customers.Count} customer(s) match order history — no drift."
            : $"Corrected {corrected} of {customers.Count}: " + string.Join("; ", details));
    }

    private async Task<(bool Ok, string Result)> ReverifyAsync(string? reference, CancellationToken ct)
    {
        reference = reference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > 80)
            return (false, "Enter the payment reference to re-verify.");
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Reference == reference, ct);
        if (payment is null) return (false, $"No payment with reference {reference}.");
        if (payment.Method != PaymentMethod.Paystack) return (false, "Only Paystack payments can be re-verified.");
        if (payment.Status is not (PaymentStatus.Initialized or PaymentStatus.Pending))
            return (true, $"Payment {reference} is already {payment.Status}; nothing to do (no change made).");
        try
        {
            var dto = await Sender.Send(new VerifyPaymentCommand(reference, null), ct);
            fees.Invalidate();
            return (true, $"Payment {reference} verified with Paystack; status now {dto.Status}.");
        }
        catch (NotFoundException)
        {
            return (false, $"No payment with reference {reference}.");
        }
    }
}
