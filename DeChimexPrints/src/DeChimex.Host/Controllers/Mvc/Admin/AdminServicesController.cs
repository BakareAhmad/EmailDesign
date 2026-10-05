using DeChimex.Host.Models.Admin;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Services.Commands;
using DeChimex.Application.Features.Services.Queries;
using DeChimex.Contracts.Services;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin services management. Reuses service queries/commands directly.</summary>
[Authorize(Policy = Permissions.Services.View)]
[Route("admin/services")]
public sealed class AdminServicesController : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Services";
        ViewData["Heading"] = "Services";
        ViewData["ActiveNav"] = "services";

        var services = await Sender.Send(new GetServicesQuery(ActiveOnly: false));
        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Services.Manage);
        return View(services);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Services.Manage)]
    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New service";
        ViewData["Heading"] = "Services";
        ViewData["ActiveNav"] = "services";

        // Pre-assign a curated accent so the form preview reflects the colour that
        // will be saved, while staying varied across existing services.
        var existing = await Sender.Send(new GetServicesQuery(ActiveOnly: false));
        var model = new ServiceFormViewModel
        {
            IconColor = ServiceIconPalette.Pick(existing.Select(s => s.IconColor).ToList())
        };
        return View(model);
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Services.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ServiceFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        await Sender.Send(new CreateServiceCommand(new CreateServiceRequest(
            model.Name.Trim(), model.Description, model.BasePrice, model.IconColor, model.IsFeatured)));
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Services.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var service = await Sender.Send(new GetServiceByIdQuery(id));
        if (service is null) return NotFound();

        ViewData["Title"] = "Edit service";
        ViewData["Heading"] = "Services";
        ViewData["ActiveNav"] = "services";
        return View(new ServiceFormViewModel
        {
            Id = service.Id,
            Name = service.Name,
            Description = service.Description,
            BasePrice = service.BasePrice,
            IconColor = service.IconColor,
            IsFeatured = service.IsFeatured,
            IsActive = service.IsActive
        });
    }

    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Services.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, ServiceFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            await Sender.Send(new UpdateServiceCommand(id, new UpdateServiceRequest(
                model.Name.Trim(), model.Description, model.BasePrice, model.IconColor, model.IsFeatured, model.IsActive)));
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Services.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            await Sender.Send(new DeleteServiceCommand(id));
            TempData["Success"] = "Service deleted.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (NotFoundException)
        {
            TempData["Error"] = "Service not found.";
        }
        return RedirectToAction(nameof(Index));
    }
}
