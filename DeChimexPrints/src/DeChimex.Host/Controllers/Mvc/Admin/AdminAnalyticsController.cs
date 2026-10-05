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
/// Admin analytics: summary, revenue trend, top products and order-status mix, projected
/// into typed row models for Razor rendering. REVENUE (AUD-001/016) comes from the shared
/// IRevenueQueries — the explicit paid-like whitelist (never a numeric range: the additive
/// custom-order states 10-15 are UNPAID) on the cash basis (PaidAtUtc) — so this page
/// reconciles with the Dashboard, Reports and the fee ledger by construction.
/// </summary>
[Authorize(Policy = Permissions.Reports.View)]
[Route("admin/analytics")]
public sealed class AdminAnalyticsController(DeChimexDbContext db, IRevenueQueries revenue) : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(string period = "30d")
    {
        ViewData["Title"] = "Analytics";
        ViewData["Heading"] = "Analytics";
        ViewData["ActiveNav"] = "analytics";

        ViewBag.Period = period;
        ViewBag.Summary = await BuildSummaryAsync(period);
        ViewBag.RevenueTrend = await BuildRevenueTrendAsync(period);
        ViewBag.TopProducts = await BuildTopProductsAsync(period);
        ViewBag.StatusMix = await BuildStatusMixAsync(period);
        return View();
    }

    private async Task<AnalyticsSummaryRow> BuildSummaryAsync(string period)
    {
        var (from, to, prevFrom, prevTo) = GetPeriodRange(period);

        // AUD-001: one shared paid-like + cash-basis source for every revenue figure.
        var paidRows = await revenue.GetPaidOrdersAsync(from, to);
        var prevPaidRows = await revenue.GetPaidOrdersAsync(prevFrom, prevTo);
        var paid = paidRows.Select(o => Math.Max(0m, o.Amount)).ToList();
        var prevPaid = prevPaidRows.Select(o => Math.Max(0m, o.Amount)).ToList();

        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CreatedDateUtc >= from && o.CreatedDateUtc <= to)
            .CountAsync();
        var prevOrders = await db.Orders.AsNoTracking()
            .Where(o => o.CreatedDateUtc >= prevFrom && o.CreatedDateUtc <= prevTo)
            .CountAsync();

        var revenueTotal = paid.Sum();
        var prevRevenue = prevPaid.Sum();
        var count = orders;
        var prevCount = prevOrders;
        var paidCount = paid.Count;
        var prevPaidCount = prevPaid.Count;
        var aov = paidCount > 0 ? revenueTotal / paidCount : 0m;
        var prevAov = prevPaidCount > 0 ? prevRevenue / prevPaidCount : 0m;
        var conv = count > 0 ? Math.Round((decimal)paidCount / count * 100, 1) : 0m;
        var prevConv = prevCount > 0 ? Math.Round((decimal)prevPaidCount / prevCount * 100, 1) : 0m;

        return new AnalyticsSummaryRow(
            revenueTotal, count, Math.Round(aov, 0), conv,
            TrendPercent(revenueTotal, prevRevenue),
            TrendPercent((decimal)count, (decimal)prevCount),
            TrendPercent(aov, prevAov),
            TrendPercent(conv, prevConv));
    }

    private async Task<List<DeChimex.Contracts.Dashboard.RevenuePointDto>> BuildRevenueTrendAsync(string period)
    {
        var (from, to, _, _) = GetPeriodRange(period);
        var paid = await revenue.GetPaidOrdersAsync(from, to);

        return BuildTimeBuckets(from, to, period)
            .Select(b => new DeChimex.Contracts.Dashboard.RevenuePointDto(b.Label,
                paid.Where(o => o.PaidAtUtc >= b.From && o.PaidAtUtc < b.To)
                    .Sum(o => Math.Max(0m, o.Amount))))
            .ToList();
    }

    private async Task<List<TopProductRow>> BuildTopProductsAsync(string period)
    {
        var (from, to, _, _) = GetPeriodRange(period);
        var validOrderIds = await revenue.GetPaidOrderIdsAsync(from, to);

        var rows = await db.OrderItems.AsNoTracking()
            .Where(oi => validOrderIds.Contains(oi.OrderId))
            .GroupBy(oi => oi.ProductName)
            .Select(g => new { name = g.Key, units = g.Sum(x => x.Quantity), revenue = g.Sum(x => x.UnitPrice.Amount * x.Quantity) })
            .OrderByDescending(r => r.revenue)
            .Take(10)
            .ToListAsync();

        return rows.Select(r => new TopProductRow(r.name, r.units, r.revenue)).ToList();
    }

    private async Task<List<OrderStatusMixRow>> BuildStatusMixAsync(string period)
    {
        var (from, to, _, _) = GetPeriodRange(period);
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CreatedDateUtc >= from && o.CreatedDateUtc <= to)
            .GroupBy(o => o.Status)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync();

        var total = orders.Sum(o => o.count);
        return orders
            .OrderByDescending(o => o.count)
            .Select(o => new OrderStatusMixRow(
                o.status.ToString(), o.count,
                total > 0 ? Math.Round((decimal)o.count / total * 100, 0) : 0,
                StatusTone(o.status)))
            .ToList();
    }

    private static (DateTime from, DateTime to, DateTime prevFrom, DateTime prevTo) GetPeriodRange(string period)
    {
        var now = DateTime.UtcNow;
        return period switch
        {
            "q" => (now.AddMonths(-3), now, now.AddMonths(-6), now.AddMonths(-3)),
            "y" => (now.AddYears(-1), now, now.AddYears(-2), now.AddYears(-1)),
            _ => (now.AddDays(-30), now, now.AddDays(-60), now.AddDays(-30))
        };
    }

    private static List<(string Label, DateTime From, DateTime To)> BuildTimeBuckets(DateTime from, DateTime to, string period)
    {
        var buckets = new List<(string, DateTime, DateTime)>();
        if (period == "30d")
        {
            for (var d = from; d < to; d = d.AddDays(7))
            {
                var end = d.AddDays(7) < to ? d.AddDays(7) : to;
                var weekNum = (int)((d - from).TotalDays / 7) + 1;
                buckets.Add(("W" + weekNum, d, end));
            }
        }
        else if (period == "q")
        {
            for (var d = from; d < to; d = d.AddMonths(1))
            {
                var end = d.AddMonths(1) < to ? d.AddMonths(1) : to;
                buckets.Add((d.ToString("MMM"), d, end));
            }
        }
        else
        {
            for (var d = from; d < to; d = d.AddMonths(3))
            {
                var end = d.AddMonths(3) < to ? d.AddMonths(3) : to;
                var qNum = (int)((d - from).TotalDays / 91) + 1;
                buckets.Add(("Q" + qNum, d, end));
            }
        }
        return buckets;
    }

    private static string TrendPercent(decimal current, decimal previous)
    {
        if (previous == 0) return current > 0 ? "+100%" : "0%";
        var pct = Math.Round((current - previous) / previous * 100, 1);
        return (pct >= 0 ? "+" : "") + pct + "%";
    }

    private static string StatusTone(OrderStatus status) => status switch
    {
        OrderStatus.Completed or OrderStatus.Delivered => "success",
        OrderStatus.InProduction or OrderStatus.Ready => "info",
        OrderStatus.PendingPayment or OrderStatus.AwaitingProcurement => "warning",
        OrderStatus.Cancelled => "neutral",
        OrderStatus.Refunded => "error",
        _ => "neutral"
    };
}
