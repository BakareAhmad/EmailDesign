using DeChimex.Host.Models.Admin;
using DeChimex.Application.Common.Models;
using DeChimex.Application.Features.Inventory.Commands;
using DeChimex.Application.Features.Inventory.Queries;
using DeChimex.Contracts.Inventory;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin inventory management. Reuses inventory queries/commands directly.</summary>
[Authorize(Policy = Permissions.Inventory.View)]
[Route("admin/inventory")]
public sealed class AdminInventoryController : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, string? search = null)
    {
        ViewData["Title"] = "Inventory";
        ViewData["Heading"] = "Inventory";
        ViewData["ActiveNav"] = "inventory";

        var result = await Sender.Send(new SearchInventoryQuery(new PagedRequest
        {
            Page = page, PageSize = 20, Search = search
        }));

        ViewBag.Search = search;
        ViewBag.Page = result.Page;
        ViewBag.PageSize = result.PageSize;
        ViewBag.TotalRecords = result.TotalRecords;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        ViewBag.CanAdjust = User.HasClaim(Permissions.ClaimType, Permissions.Inventory.Adjust);
        ViewBag.CanStockIn = User.HasClaim(Permissions.ClaimType, Permissions.Inventory.StockIn);
        return View(result.Items);
    }

    [HttpGet("low-stock")]
    public async Task<IActionResult> LowStock()
    {
        ViewData["Title"] = "Low stock";
        ViewData["Heading"] = "Inventory";
        ViewData["ActiveNav"] = "inventory";
        var items = await Sender.Send(new GetLowStockQuery());
        return View(items);
    }

    [HttpGet("{productId:guid}")]
    public async Task<IActionResult> Details(Guid productId)
    {
        var inventory = await Sender.Send(new GetInventoryHistoryQuery(productId));
        ViewData["Title"] = "Stock history";
        ViewData["Heading"] = "Inventory";
        ViewData["ActiveNav"] = "inventory";
        ViewData["ProductId"] = productId;
        ViewBag.CanAdjust = User.HasClaim(Permissions.ClaimType, Permissions.Inventory.Adjust);
        ViewBag.CanStockIn = User.HasClaim(Permissions.ClaimType, Permissions.Inventory.StockIn);
        return View(inventory);
    }

    [HttpPost("{productId:guid}/stock-in")]
    [Authorize(Policy = Permissions.Inventory.StockIn)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StockIn(Guid productId, StockInFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            await Sender.Send(new StockInCommand(productId, new StockInRequest(
                model.Quantity, model.Reference, model.Notes)));
        }
        return RedirectToAction(nameof(Details), new { productId });
    }

    [HttpPost("{productId:guid}/adjust")]
    [Authorize(Policy = Permissions.Inventory.Adjust)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(Guid productId, StockAdjustFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            await Sender.Send(new StockAdjustmentCommand(productId, new StockAdjustmentRequest(
                model.Delta, model.Reason)));
        }
        return RedirectToAction(nameof(Details), new { productId });
    }
}
