using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Products.Queries;
using DeChimex.Application.Features.Services.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Store;

/// <summary>Public landing page: hero, business stats, services and categories.</summary>
[AllowAnonymous]
[Route("")]
public sealed class StoreHomeController : StoreControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Home";
        ViewData["ActiveNav"] = "home";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        ViewBag.Services = await Sender.Send(new GetServicesQuery(ActiveOnly: true));
        ViewBag.Categories = await Sender.Send(new GetCategoriesQuery(ActiveOnly: true));
        // Product-first homepage: featured products feed the hero mosaic and the
        // "Popular right now" rail (falls back to the newest products when nothing is flagged).
        var featured = await Sender.Send(new GetFeaturedProductsQuery(8));
        if (featured.Count < 4)
        {
            var newest = await Sender.Send(new SearchProductsQuery(new ProductFilter { Page = 1, PageSize = 8, ActiveOnly = true }));
            featured = featured.Concat(newest.Items.Where(n => featured.All(f => f.Id != n.Id))).Take(8).ToList();
        }
        ViewBag.Featured = featured;
        return View();
    }
}
