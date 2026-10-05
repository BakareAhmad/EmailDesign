using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Cart;
using DeChimex.Contracts.Cart;
using DeChimex.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Store;

/// <summary>
/// Public cart. Server-side cart requires an authenticated customer (same as the existing Cart
/// API). Anonymous visitors are prompted to sign in. Checkout remains the Paystack JS flow.
/// </summary>
[Route("cart")]
public sealed class StoreCartController(ICustomerRepository customers) : StoreControllerBase
{
    private async Task<Domain.Entities.Customer?> TryCustomerAsync()
    {
        if (CurrentUser.UserId is not Guid uid) return null;
        return await customers.GetByUserIdAsync(uid, HttpContext.RequestAborted);
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Cart";
        ViewData["ActiveNav"] = "cart";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();

        var customer = await TryCustomerAsync();
        var cart = customer is null ? null : await Sender.Send(new GetCartQuery(customer.Id));
        ViewBag.Cart = cart;
        ViewBag.IsAuthenticated = User.Identity?.IsAuthenticated == true;

        var business = ViewBag.Business as Dictionary<string, string> ?? new();
        var deliveryFee = decimal.TryParse(business.GetValueOrDefault("delivery.default_fee"), out var fee) && fee >= 0 ? fee : 1500m;
        ViewBag.DeliveryFee = deliveryFee;
        ViewBag.GrandTotal = (cart?.Subtotal ?? 0m) + deliveryFee;
        return View();
    }

    [HttpPost("add")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string lineType, Guid referenceId, int quantity)
    {
        var customer = await TryCustomerAsync()
            ?? throw new NotFoundException("No customer profile is linked to this account.");
        if (quantity < 1) quantity = 1;

        await Sender.Send(new AddCartItemCommand(customer.Id, new AddCartItemRequest(
            lineType, referenceId, quantity, null)));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("update/{itemId:guid}")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid itemId, int quantity)
    {
        var customer = await TryCustomerAsync()
            ?? throw new NotFoundException("No customer profile is linked to this account.");
        await Sender.Send(new UpdateCartItemCommand(customer.Id, itemId, new UpdateCartItemRequest(quantity)));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("remove/{itemId:guid}")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid itemId)
    {
        var customer = await TryCustomerAsync()
            ?? throw new NotFoundException("No customer profile is linked to this account.");
        await Sender.Send(new RemoveCartItemCommand(customer.Id, itemId));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("clear")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear()
    {
        var customer = await TryCustomerAsync()
            ?? throw new NotFoundException("No customer profile is linked to this account.");
        await Sender.Send(new ClearCartCommand(customer.Id));
        return RedirectToAction(nameof(Index));
    }
}
