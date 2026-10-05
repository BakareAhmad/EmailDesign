using DeChimex.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using DeChimex.Domain.Enums;
using DeChimex.Application.Features.Dashboard;
using DeChimex.Application.Features.Inventory.Queries;
using DeChimex.Application.Features.Orders.Queries;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin dashboard: KPIs, revenue trend, sales by category, low stock, recent orders.</summary>
[Authorize(Policy = Permissions.Reports.View)]
[Route("admin/dashboard")]
public sealed class AdminDashboardController(Persistence.Context.DeChimexDbContext db) : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Dashboard";
        ViewData["Heading"] = "Dashboard";
        ViewData["ActiveNav"] = "dashboard";

        var kpis = await Sender.Send(new GetDashboardKpisQuery());
        var byCategory = await Sender.Send(new GetSalesByCategoryQuery());
        var revenueTrend = await Sender.Send(new GetRevenueTrendQuery(8));
        var lowStock = await Sender.Send(new GetLowStockQuery());
        var recentOrders = await Sender.Send(new SearchOrdersQuery(new OrderFilter { Page = 1, PageSize = 8 }));

        ViewBag.Kpis = kpis;
        ViewBag.SalesByCategory = byCategory;
        ViewBag.RevenueTrend = revenueTrend;
        ViewBag.LowStock = lowStock;
        ViewBag.RecentOrders = recentOrders.Items;

        // AUD-018: cancelled orders whose payment is still held (Succeeded, never refunded)
        // — the outstanding-refund queue. The tile keeps the owed money visible instead of
        // letting it silently ride on the cancelled order.
        var heldOnCancelled = db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Succeeded
                        && db.Orders.Any(o => o.Id == p.OrderId && o.Status == OrderStatus.Cancelled));
        ViewBag.OutstandingRefunds = await heldOnCancelled.Select(p => p.OrderId).Distinct().CountAsync();
        ViewBag.OutstandingRefundTotal = await heldOnCancelled.SumAsync(p => (decimal?)p.Amount.Amount) ?? 0m;
        return View();
    }
}
