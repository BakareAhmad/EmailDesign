using DeChimex.Application.Features.Wishlist;
using DeChimex.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>Customer portal: wishlist.</summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal/wishlist")]
public sealed class PortalWishlistController(ICustomerRepository customers) : PortalControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Wishlist";
        ViewData["Heading"] = "Wishlist";
        ViewData["ActiveNav"] = "wishlist";

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var items = await Sender.Send(new GetWishlistQuery(customer.Id));
        return View(items);
    }

    [HttpPost("add/{productId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(Guid productId, string? returnUrl)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        await Sender.Send(new AddWishlistItemCommand(customer.Id,
            new DeChimex.Contracts.Wishlist.AddWishlistItemRequest(productId)), HttpContext.RequestAborted);
        TempData["Success"] = "Added to your wishlist.";
        return ReturnToShop(returnUrl);
    }

    private IActionResult ReturnToShop(string? returnUrl) => Url.IsLocalUrl(returnUrl)
        ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Index));

    [HttpPost("remove/{productId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid productId, string? returnUrl)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        await Sender.Send(new RemoveWishlistItemCommand(customer.Id, productId));
        TempData["Success"] = "Removed from your wishlist.";
        return ReturnToShop(returnUrl);
    }
}
