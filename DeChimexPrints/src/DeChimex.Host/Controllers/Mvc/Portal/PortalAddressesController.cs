using DeChimex.Host.Models.Portal;
using DeChimex.Application.Features.Customers.Commands;
using DeChimex.Application.Features.Customers.Queries;
using DeChimex.Contracts.Customers;
using DeChimex.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>Customer portal: saved addresses.</summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal/addresses")]
public sealed class PortalAddressesController(ICustomerRepository customers) : PortalControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Addresses";
        ViewData["Heading"] = "Addresses";
        ViewData["ActiveNav"] = "addresses";

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var profile = await Sender.Send(new GetCustomerByIdQuery(customer.Id));
        return View(profile?.Addresses ?? new List<AddressDto>());
    }

    [HttpGet("add")]
    public IActionResult Add()
    {
        ViewData["Title"] = "Add address";
        ViewData["Heading"] = "Addresses";
        ViewData["ActiveNav"] = "addresses";
        return View(new AddressFormViewModel());
    }

    [HttpPost("add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(AddressFormViewModel model)
    {
        ViewData["Title"] = "Add address";
        ViewData["Heading"] = "Addresses";
        ViewData["ActiveNav"] = "addresses";

        if (!ModelState.IsValid)
            return View(model);

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        await Sender.Send(new AddCustomerAddressCommand(customer.Id, new AddAddressRequest(
            model.Label.Trim(), model.Street.Trim(), model.City.Trim(), model.State.Trim(),
            model.Country.Trim(), model.PostalCode, model.IsDefault)));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("remove/{addressId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid addressId)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        await Sender.Send(new RemoveCustomerAddressCommand(customer.Id, addressId));
        return RedirectToAction(nameof(Index));
    }
}
