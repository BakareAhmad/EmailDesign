using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Application.Features.Procurement.Commands;
using DeChimex.Application.Features.Procurement.Queries;
using DeChimex.Application.Features.Products.Queries;
using DeChimex.Contracts.Products;
using DeChimex.Contracts.Procurement;
using DeChimex.Host.Models.Admin;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin procurement management. Reuses procurement queries/commands directly.</summary>
[Authorize(Policy = Permissions.Procurement.View)]
[Route("admin/procurement")]
public sealed class AdminProcurementController(DeChimexDbContext db) : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1)
    {
        ViewData["Title"] = "Procurement";
        ViewData["Heading"] = "Procurement";
        ViewData["ActiveNav"] = "procurement";

        var result = await Sender.Send(new SearchProcurementsQuery(new PagedRequest
        {
            Page = page, PageSize = 20
        }));

        ViewBag.Page = result.Page;
        ViewBag.PageSize = result.PageSize;
        ViewBag.TotalRecords = result.TotalRecords;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        ViewBag.CanApprove = User.HasClaim(Permissions.ClaimType, Permissions.Procurement.Approve);
        ViewBag.CanReceive = User.HasClaim(Permissions.ClaimType, Permissions.Procurement.Receive);
        return View(result.Items);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var procurement = await Sender.Send(new GetProcurementByIdQuery(id));
        if (procurement is null) return NotFound();

        ViewData["Title"] = procurement.Reference;
        ViewData["Heading"] = "Procurement";
        ViewData["ActiveNav"] = "procurement";
        ViewBag.CanApprove = User.HasClaim(Permissions.ClaimType, Permissions.Procurement.Approve);
        ViewBag.CanReceive = User.HasClaim(Permissions.ClaimType, Permissions.Procurement.Receive);
        ViewBag.Suppliers = await SupplierOptionsAsync();
        return View(procurement);
    }

    /// <summary>Manual procurement request (e.g. a supplier deal found on the counter).</summary>
    [HttpGet("create")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New procurement";
        ViewData["Heading"] = "Procurement";
        ViewData["ActiveNav"] = "procurement";

        var model = new ProcurementCreateViewModel();
        model.Items.Add(new ProcurementCreateItemViewModel());
        await PopulateProductsAsync();
        return View(model);
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ProcurementCreateViewModel model)
    {
        ViewData["Title"] = "New procurement";
        ViewData["Heading"] = "Procurement";
        ViewData["ActiveNav"] = "procurement";

        var lines = model.Items
            .Where(i => i.ProductId != Guid.Empty && i.Quantity >= 1)
            .ToList();
        if (lines.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Add at least one item with a product and quantity.");
            await PopulateProductsAsync();
            return View(model);
        }

        // Accept the business order reference (DCX-…, what every other surface shows) and,
        // for backward compatibility with saved links, a raw order GUID.
        Guid? sourceOrderId = null;
        var orderRef = model.SourceOrderNumber?.Trim();
        if (!string.IsNullOrEmpty(orderRef))
        {
            Domain.Entities.Order? linked;
            if (Guid.TryParse(orderRef, out var guidRef))
                linked = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == guidRef);
            else
                linked = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderNumber == orderRef);
            if (linked is null)
            {
                ModelState.AddModelError("SourceOrderNumber", $"No order with the number {orderRef} was found.");
                await PopulateProductsAsync();
                return View(model);
            }
            sourceOrderId = linked.Id;
        }

        var created = await Sender.Send(new CreateProcurementCommand(new CreateProcurementRequest(
            sourceOrderId,
            lines.Select(i => new CreateProcurementItemRequest(i.ProductId, i.Quantity, i.UnitCost)).ToList(),
            string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes)));

        TempData["Success"] = $"Procurement {created.Reference} requested.";
        return RedirectToAction(nameof(Details), new { id = created.Id });
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id)
    {
        await Sender.Send(new ApproveProcurementCommand(id));
        TempData["Success"] = "Procurement approved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/purchase")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Purchase(Guid id, string? supplierName)
    {
        if (string.IsNullOrWhiteSpace(supplierName))
        {
            TempData["Error"] = "Choose the supplier the stock was purchased from.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await Sender.Send(new MarkProcurementPurchasedCommand(id, new MarkPurchasedRequest(supplierName)));
        TempData["Success"] = $"Marked as purchased from {supplierName}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/receive")]
    [Authorize(Policy = Permissions.Procurement.Receive)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(Guid id)
    {
        await Sender.Send(new ReceiveProcurementCommand(id));
        TempData["Success"] = "Stock received into inventory.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            await Sender.Send(new DeleteProcurementCommand(id));
            TempData["Success"] = "Procurement deleted.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (DeChimex.Application.Common.Exceptions.NotFoundException)
        {
            TempData["Error"] = "Procurement not found.";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateProductsAsync()
    {
        var products = await Sender.Send(new SearchProductsQuery(new ProductFilter
        {
            Page = 1, PageSize = 200, ActiveOnly = false
        }));
        ViewBag.Products = products.Items
            .Select(p => new SelectListItem($"{p.Name} ({p.Sku})", p.Id.ToString()))
            .ToList();
    }

    private async Task<List<SelectListItem>> SupplierOptionsAsync()
    {
        var suppliers = await db.Suppliers
            .AsNoTracking()
            .Where(s => s.Status == "Active")
            .OrderBy(s => s.Name)
            .Select(s => s.Name)
            .ToListAsync();
        return suppliers.Select(name => new SelectListItem(name, name)).ToList();
    }
}
