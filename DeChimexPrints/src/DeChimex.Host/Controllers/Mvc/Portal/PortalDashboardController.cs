using DeChimex.Application.Features.Customers.Queries;
using DeChimex.Application.Features.Orders.Queries;
using DeChimex.Application.Features.Wishlist;
using DeChimex.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>Customer portal landing page: profile summary, recent orders, wishlist count.</summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal")]
public sealed class PortalDashboardController(ICustomerRepository customers) : PortalControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Dashboard";
        ViewData["Heading"] = "Dashboard";
        ViewData["ActiveNav"] = "dashboard";

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var profile = await Sender.Send(new GetCustomerByIdQuery(customer.Id));
        var orders = await Sender.Send(new GetMyOrdersQuery(customer.Id));
        var wishlist = await Sender.Send(new GetWishlistQuery(customer.Id));

        ViewBag.Profile = profile;
        ViewBag.Orders = orders;
        ViewBag.WishlistCount = wishlist.Count;
        return View();
    }
}
