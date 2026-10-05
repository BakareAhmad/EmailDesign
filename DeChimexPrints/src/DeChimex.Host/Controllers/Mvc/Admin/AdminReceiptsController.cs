using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Controllers.Mvc;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin receipt listing and viewing (receipts are generated after payment).</summary>
[Authorize(Policy = Permissions.Orders.View)]
[Route("admin/receipts")]
public sealed class AdminReceiptsController(IInvoiceQueries invoices, IReceiptRepository receipts,
    Persistence.Context.DeChimexDbContext db) : MvcControllerBase
{
    private readonly IInvoiceQueries _invoices = invoices;
    private readonly IReceiptRepository _receipts = receipts;
    private readonly Persistence.Context.DeChimexDbContext _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, int pageSize = 10, string? search = null)
    {
        ViewData["Title"] = "Receipts";
        ViewData["Heading"] = "Receipts";
        ViewData["ActiveNav"] = "receipts";

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 100 ? 10 : pageSize;

        // AUD-009: filter/count/page in SQL exactly like the invoices screen — the previous
        // implementation loaded EVERY receipt ever created and searched/paged in memory,
        // degrading linearly with the table.
        var q = _db.Receipts.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(r => EF.Functions.Like(r.ReceiptNumber, $"%{s}%")
                || EF.Functions.Like(r.OrderNumber, $"%{s}%")
                || EF.Functions.Like(r.CustomerName, $"%{s}%"));
        }
        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(r => r.PaidAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();

        ViewBag.Search = search;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.TotalRecords = total;
        ViewBag.TotalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        return View(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> View(Guid id)
    {
        var receipt = await _invoices.GetReceiptByIdAsync(id, CancellationToken.None);
        if (receipt is null) return NotFound();

        ViewData["Title"] = receipt.ReceiptNumber;
        ViewData["Heading"] = "Receipts";
        ViewData["ActiveNav"] = "receipts";
        await LoadBusinessHeaderAsync();
        return View(receipt);
    }

    private async Task LoadBusinessHeaderAsync()
    {
        // The header comes from the shared application settings (business profile).
        var settings = HttpContext.RequestServices.GetRequiredService<IApplicationSettingRepository>();
        ViewBag.BusinessName = (await settings.GetByKeyAsync("business.name"))?.Value ?? "DeChimex";
        ViewBag.BusinessMotto = (await settings.GetByKeyAsync("business.motto"))?.Value;
        ViewBag.BusinessPhone = (await settings.GetByKeyAsync("business.phone"))?.Value;
        ViewBag.BusinessEmail = (await settings.GetByKeyAsync("business.email"))?.Value;
        ViewBag.BusinessAddress = (await settings.GetByKeyAsync("business.address"))?.Value;
    }

    /// <summary>Generate a public share link for the receipt.</summary>
    [HttpPost("{id:guid}/share")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Share(Guid id)
    {
        var receipt = await _receipts.GetByIdAsync(id, CancellationToken.None);
        if (receipt is null) return NotFound();
        if (string.IsNullOrEmpty(receipt.ShareToken))
        {
            receipt.SetShareToken(Guid.NewGuid().ToString("N"));
            _receipts.Update(receipt);
        }
        TempData["Success"] = "Share link generated. Copy it and send it to your customer.";
        return RedirectToAction(nameof(View), new { id });
    }
}
