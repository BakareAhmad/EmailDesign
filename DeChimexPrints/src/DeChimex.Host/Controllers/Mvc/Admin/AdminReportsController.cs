using DeChimex.Application.Common.Interfaces;
using DeChimex.Host.Models.Admin;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Admin reports (read-only). Sales/product/service REVENUE (AUD-001/016) comes from the
/// shared IRevenueQueries — the explicit paid-like whitelist (never a numeric range: the
/// additive custom-order states 10-15 are UNPAID) on the cash basis (PaidAtUtc) — so the
/// figures reconcile with the Dashboard and Analytics by construction.
/// </summary>
[Authorize(Policy = Permissions.Reports.View)]
[Route("admin/reports")]
public sealed class AdminReportsController(DeChimexDbContext db, IRevenueQueries revenue) : MvcControllerBase
{
    [HttpGet]
    public IActionResult Index()
    {
        ViewData["Title"] = "Reports";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View();
    }

    [HttpGet("sales")]
    public async Task<IActionResult> Sales(DateTime? from, DateTime? to, string period = "Monthly", string? format = null)
    {
        var (dateFrom, dateTo) = ResolveDateRange(from, to);
        var paid = await revenue.GetPaidOrdersAsync(dateFrom, dateTo);

        var buckets = BuildReportBuckets(dateFrom, dateTo, period);
        var result = buckets.Select(b =>
        {
            var bucketOrders = paid.Where(o => o.PaidAtUtc >= b.From && o.PaidAtUtc < b.To).ToList();
            return new SalesReportRow(b.Label, bucketOrders.Count, bucketOrders.Sum(o => Math.Max(0m, o.Amount)));
        }).Where(r => r.Orders > 0 || r.Revenue > 0).ToList();

        // AUD-028: month-end accounting needs spreadsheets, not screenshots.
        if (IsCsv(format))
            return Csv("sales-report", "Period,Orders,Revenue",
                result.Select(r => new[] { r.Period, r.Orders.ToString(), r.Revenue.ToString("0.00") }));

        ViewData["Title"] = "Sales report";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View(result);
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory(DateTime? from, DateTime? to)
    {
        var (dateFrom, dateTo) = ResolveDateRange(from, to);
        var transactions = await (
            from t in db.InventoryTransactions.AsNoTracking()
            join i in db.Inventories.AsNoTracking() on t.InventoryId equals i.Id
            where t.OccurredOnUtc >= dateFrom && t.OccurredOnUtc <= dateTo
            select new { i.ProductName, t.Type, t.QuantityChange })
            .ToListAsync();

        var result = transactions
            .GroupBy(t => t.ProductName)
            .Select(g => new InventoryReportRow(
                g.Key,
                g.Where(t => t.Type == InventoryTransactionType.StockIn).Sum(t => t.QuantityChange),
                g.Where(t => t.Type == InventoryTransactionType.StockOut).Sum(t => Math.Abs(t.QuantityChange)),
                g.Where(t => t.Type == InventoryTransactionType.Adjustment).Sum(t => t.QuantityChange)))
            .OrderByDescending(r => r.StockIn + r.StockOut)
            .ToList();

        ViewData["Title"] = "Inventory report";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View(result);
    }

    [HttpGet("lowstock")]
    public async Task<IActionResult> LowStock()
    {
        var rows = await db.Inventories.AsNoTracking()
            .Where(i => (i.QuantityOnHand - i.QuantityReserved) <= i.LowStockThreshold)
            .Select(i => new
            {
                product = i.ProductName,
                available = i.QuantityOnHand - i.QuantityReserved,
                threshold = i.LowStockThreshold,
                status = (i.QuantityOnHand - i.QuantityReserved) == 0 ? "Out of stock" : "Low"
            })
            .OrderBy(i => i.available)
            .ToListAsync();

        var items = rows.Select(r => new LowStockRow(r.product, r.available, r.threshold, r.status)).ToList();

        ViewData["Title"] = "Low stock report";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View(items);
    }

    [HttpGet("customers")]
    public async Task<IActionResult> Customers(DateTime? from, DateTime? to)
    {
        var (dateFrom, dateTo) = ResolveDateRange(from, to);

        var customers = await db.Customers.AsNoTracking()
            .Select(c => new { c.Id, c.FirstName, c.LastName, Spend = c.LifetimeSpending.Amount })
            .ToListAsync();

        var orderCounts = await db.Orders.AsNoTracking()
            .Where(o => o.CreatedDateUtc >= dateFrom && o.CreatedDateUtc <= dateTo)
            .GroupBy(o => o.CustomerId)
            .Select(g => new { customerId = g.Key, count = g.Count() })
            .ToListAsync();

        var result = customers
            .Select(c => new CustomerReportRow(
                (c.FirstName + " " + c.LastName).Trim(),
                orderCounts.FirstOrDefault(o => o.customerId == c.Id)?.count ?? 0,
                c.Spend))
            .Where(r => r.Orders > 0)
            .OrderByDescending(r => r.LifetimeSpend)
            .ToList();

        ViewData["Title"] = "Customer report";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View(result);
    }

    [HttpGet("products")]
    public async Task<IActionResult> Products(DateTime? from, DateTime? to, string? format = null)
    {
        var (dateFrom, dateTo) = ResolveDateRange(from, to);
        var validOrderIds = await revenue.GetPaidOrderIdsAsync(dateFrom, dateTo);

        var rows = await db.OrderItems.AsNoTracking()
            .Where(oi => validOrderIds.Contains(oi.OrderId))
            .GroupBy(oi => oi.ProductName)
            .Select(g => new { product = g.Key, units = g.Sum(x => x.Quantity), revenue = g.Sum(x => x.UnitPrice.Amount * x.Quantity) })
            .OrderByDescending(r => r.revenue)
            .ToListAsync();

        var items = rows.Select(r => new ProductReportRow(r.product, r.units, r.revenue)).ToList();

        if (IsCsv(format))
            return Csv("product-report", "Product,Units,Revenue",
                items.Select(r => new[] { r.Product, r.Units.ToString(), r.Revenue.ToString("0.00") }));

        ViewData["Title"] = "Product report";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View(items);
    }

    [HttpGet("services")]
    public async Task<IActionResult> Services(DateTime? from, DateTime? to, string? format = null)
    {
        var (dateFrom, dateTo) = ResolveDateRange(from, to);
        var validOrderIds = await revenue.GetPaidOrderIdsAsync(dateFrom, dateTo);

        var rows = await db.OrderServiceLines.AsNoTracking()
            .Where(sl => validOrderIds.Contains(sl.OrderId))
            .GroupBy(sl => sl.ServiceName)
            .Select(g => new { service = g.Key, bookings = g.Sum(x => x.Quantity), revenue = g.Sum(x => x.UnitPrice.Amount * x.Quantity) })
            .OrderByDescending(r => r.revenue)
            .ToListAsync();

        var items = rows.Select(r => new ServiceReportRow(r.service, r.bookings, r.revenue)).ToList();

        if (IsCsv(format))
            return Csv("service-report", "Service,Bookings,Revenue",
                items.Select(r => new[] { r.Service, r.Bookings.ToString(), r.Revenue.ToString("0.00") }));

        ViewData["Title"] = "Service report";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View(items);
    }

    [HttpGet("staff")]
    public async Task<IActionResult> Staff(DateTime? from, DateTime? to)
    {
        var (dateFrom, dateTo) = ResolveDateRange(from, to);
        var items = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TimestampUtc >= dateFrom && a.TimestampUtc <= dateTo)
            .GroupBy(a => a.PerformedBy)
            .Select(g => new { staff = g.Key, actions = g.Count() })
            .OrderByDescending(r => r.actions)
            .ToListAsync();

        var staffEmails = items.Select(i => i.staff).ToList();
        var usersWithRoles = await (
            from u in db.Users.AsNoTracking()
            join ur in db.UserRoles.AsNoTracking() on u.Id equals ur.UserId
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where staffEmails.Contains(u.Email.Value)
            select new { u.Email, RoleName = r.Name })
            .ToListAsync();

        var result = items.Select(i => new StaffReportRow(
            i.staff ?? "Unknown",
            usersWithRoles.FirstOrDefault(u => u.Email.Value == i.staff)?.RoleName ?? "Unknown",
            i.actions)).ToList();

        ViewData["Title"] = "Staff activity";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View(result);
    }

    [HttpGet("payments")]
    public async Task<IActionResult> Payments(DateTime? from, DateTime? to)
    {
        var (dateFrom, dateTo) = ResolveDateRange(from, to);
        var rows = await db.Payments.AsNoTracking()
            .Where(p => p.CreatedDateUtc >= dateFrom && p.CreatedDateUtc <= dateTo)
            .GroupBy(p => p.Status)
            .Select(g => new { status = g.Key.ToString(), count = g.Count(), amount = g.Sum(p => p.Amount.Amount) })
            .OrderByDescending(r => r.amount)
            .ToListAsync();

        var items = rows.Select(r => new PaymentReportRow(r.status, r.count, r.amount)).ToList();

        ViewData["Title"] = "Payments report";
        ViewData["Heading"] = "Reports";
        ViewData["ActiveNav"] = "reports";
        return View(items);
    }

    /// <summary>AUD-028: ?format=csv streams the same rows as a downloadable spreadsheet
    /// (mirrors the Developer Portal's service-fee ledger export).</summary>
    private static bool IsCsv(string? format) => string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);

    private IActionResult Csv(string name, string header, IEnumerable<string[]> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        foreach (var row in rows)
            sb.AppendLine(string.Join(',', row.Select(Escape)));
        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv; charset=utf-8", $"{name}-{DateTime.UtcNow:yyyyMMdd}.csv");

        static string Escape(string v)
            => v.Contains(',') || v.Contains('"') || v.Contains('\n') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    private static (DateTime from, DateTime to) ResolveDateRange(DateTime? from, DateTime? to)
    {
        var dateTo = to ?? DateTime.UtcNow;
        var dateFrom = from ?? dateTo.AddMonths(-1);
        return (dateFrom, dateTo);
    }

    private static List<(string Label, DateTime From, DateTime To)> BuildReportBuckets(DateTime from, DateTime to, string period)
    {
        var buckets = new List<(string, DateTime, DateTime)>();
        switch (period)
        {
            case "Daily":
                for (var d = from.Date; d < to; d = d.AddDays(1))
                    buckets.Add((d.ToString("MMM dd"), d, d.AddDays(1)));
                break;
            case "Weekly":
                for (var d = from.Date; d < to; d = d.AddDays(7))
                {
                    var end = d.AddDays(7) < to ? d.AddDays(7) : to;
                    var weekNum = (int)((d - from).TotalDays / 7) + 1;
                    buckets.Add(("Week " + weekNum, d, end));
                }
                break;
            case "Monthly":
                for (var d = new DateTime(from.Year, from.Month, 1); d < to; d = d.AddMonths(1))
                {
                    var end = d.AddMonths(1) < to ? d.AddMonths(1) : to;
                    buckets.Add((d.ToString("MMM yyyy"), d, end));
                }
                break;
            case "Yearly":
                for (var d = new DateTime(from.Year, 1, 1); d < to; d = d.AddYears(1))
                {
                    var end = d.AddYears(1) < to ? d.AddYears(1) : to;
                    buckets.Add((d.Year.ToString(), d, end));
                }
                break;
        }
        return buckets;
    }
}
