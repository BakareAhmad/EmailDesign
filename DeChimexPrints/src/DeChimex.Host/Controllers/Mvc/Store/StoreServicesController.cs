using DeChimex.Application.Features.Services.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Store;

/// <summary>Public services listing.</summary>
[AllowAnonymous]
[Route("services")]
public sealed class StoreServicesController : StoreControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Services";
        ViewData["ActiveNav"] = "services";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        var services = await Sender.Send(new GetServicesQuery(ActiveOnly: true));
        return View(services);
    }
}
