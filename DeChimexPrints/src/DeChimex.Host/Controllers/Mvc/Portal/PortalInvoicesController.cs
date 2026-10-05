using DeChimex.Host.Controllers;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>
/// Customer portal: own invoices. The shared Invoices API endpoint is staff-only
/// (Permissions.Orders.View), so this controller scopes the same invoice data to the current
/// customer's own orders - fixing the portal invoices screen for customers.
/// </summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal/invoices")]
public sealed class PortalInvoicesController(DeChimexDbContext db, ICustomerRepository customers) : PortalControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Invoices";
        ViewData["Heading"] = "Invoices";
        ViewData["ActiveNav"] = "invoices";

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);

        var rows = await db.Invoices.AsNoTracking()
            .Where(i => i.CustomerId == customer.Id)
            .SelectMany(i => db.Orders.Where(o => o.Id == i.OrderId).DefaultIfEmpty(), (i, o) => new { i, o })
            .OrderByDescending(x => x.i.IssuedAtUtc)
            .ToListAsync();

        var items = rows.Select(x => new InvoiceListDto(
            x.i.Id, x.i.InvoiceNumber, x.i.OrderId,
            x.o != null ? x.o.OrderNumber : null,
            customer.FullName,
            x.i.Subtotal.Amount, x.i.Discount.Amount, x.i.DeliveryFee.Amount, x.i.Tax.Amount,
            x.i.GrandTotal.Amount, x.i.GrandTotal.Currency, x.i.Status.ToString(),
            x.i.IssuedAtUtc, x.i.PaidAtUtc, x.i.DocumentUrl, x.i.ShareToken, x.i.OrderId == null)).ToList();

        return View(items);
    }
}
