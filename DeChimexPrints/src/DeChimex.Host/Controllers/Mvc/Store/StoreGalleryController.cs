using DeChimex.Application.Features.Gallery.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Store;

/// <summary>Public gallery.</summary>
[AllowAnonymous]
[Route("gallery")]
public sealed class StoreGalleryController : StoreControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(string? category = null)
    {
        ViewData["Title"] = "Gallery";
        ViewData["ActiveNav"] = "gallery";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        ViewBag.ActiveCategory = category ?? "All";
        var items = await Sender.Send(new GetGalleryQuery(IncludeArchived: false, Category: null));
        return View(items);
    }
}
