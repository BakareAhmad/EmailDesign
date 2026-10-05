using DeChimex.Host.Models.Portal;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Auth.Commands;
using DeChimex.Application.Features.Customers.Commands;
using DeChimex.Application.Features.Customers.Queries;
using DeChimex.Contracts.Auth;
using DeChimex.Contracts.Customers;
using DeChimex.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>Customer portal: view/edit profile and change password.</summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal/profile")]
public sealed class PortalProfileController(ICustomerRepository customers) : PortalControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Profile";
        ViewData["Heading"] = "Profile";
        ViewData["ActiveNav"] = "profile";

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var profile = await Sender.Send(new GetCustomerByIdQuery(customer.Id));
        return View(new ProfileFormViewModel
        {
            FirstName = customer.FirstName,
            LastName = customer.LastName,
            Phone = customer.Phone?.Value,
            Notes = customer.Notes
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(ProfileFormViewModel model)
    {
        ViewData["Title"] = "Profile";
        ViewData["Heading"] = "Profile";
        ViewData["ActiveNav"] = "profile";

        if (!ModelState.IsValid)
            return View(model);

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        await Sender.Send(new UpdateCustomerProfileCommand(customer.Id, new UpdateCustomerProfileRequest(
            model.FirstName.Trim(), model.LastName.Trim(), model.Phone, model.Notes)));
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("password")]
    public IActionResult ChangePassword()
    {
        ViewData["Title"] = "Change password";
        ViewData["Heading"] = "Profile";
        ViewData["ActiveNav"] = "profile";
        return View(new ChangePasswordFormViewModel());
    }

    [HttpPost("password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordFormViewModel model)
    {
        ViewData["Title"] = "Change password";
        ViewData["Heading"] = "Profile";
        ViewData["ActiveNav"] = "profile";

        if (!ModelState.IsValid)
            return View(model);

        try
        {
            await Sender.Send(new ChangePasswordCommand(new ChangePasswordRequest(
                model.CurrentPassword, model.NewPassword)));
        }
        catch (ValidationAppException ex)
        {
            foreach (var (key, errors) in ex.Errors)
                foreach (var e in errors)
                    ModelState.AddModelError(key == "CurrentPassword" ? "CurrentPassword" : string.Empty, e);
            return View(model);
        }

        ViewBag.PasswordChanged = true;
        return View(model);
    }
}
