using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Application.Features.Customers.Commands;
using DeChimex.Application.Features.Customers.Queries;
using DeChimex.Application.Features.Orders.Queries;
using DeChimex.Contracts.Customers;
using DeChimex.Domain.Exceptions;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin customer directory (list + details with order history + full management).</summary>
[Authorize(Policy = Permissions.Customers.View)]
[Route("admin/customers")]
public sealed class AdminCustomersController : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, string? search = null)
    {
        ViewData["Title"] = "Customers";
        ViewData["Heading"] = "Customers";
        ViewData["ActiveNav"] = "customers";

        var result = await Sender.Send(new SearchCustomersQuery(new PagedRequest
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
        return View(result.Items);
    }

    /// <summary>
    /// JSON search feed for the new-order customer picker: up to 25 matches by
    /// first name, last name, email or phone, so the picker is not limited to a
    /// fixed page of customers.
    /// </summary>
    [HttpGet("picker")]
    [Produces("application/json")]
    public async Task<IActionResult> Picker(string? q = null)
    {
        var result = await Sender.Send(new SearchCustomersQuery(new PagedRequest
        {
            Page = 1,
            PageSize = 25,
            Search = q
        }));
        return Json(result.Items.Select(c => new
        {
            id = c.Id,
            name = c.FullName,
            email = c.Email,
            phone = c.Phone,
            guest = c.IsGuest
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var customer = await Sender.Send(new GetCustomerByIdQuery(id));
        if (customer is null) return NotFound();

        var orders = await Sender.Send(new GetMyOrdersQuery(id));

        ViewData["Title"] = customer.FullName;
        ViewData["Heading"] = "Customers";
        ViewData["ActiveNav"] = "customers";
        ViewBag.Orders = orders;
        return View(customer);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Customers.Manage)]
    public IActionResult Create() => View(new CustomerFormViewModel());

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Permissions.Customers.Manage)]
    public async Task<IActionResult> Create(CustomerFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            var email = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email;
            var customer = await Sender.Send(new CreateCustomerCommand(new CreateCustomerRequest(
                model.FirstName,
                model.LastName,
                email,
                string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone,
                string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes)));

            TempData["Success"] = customer.IsGuest
                ? $"Customer {customer.FullName} created as a guest (no email, no portal account)."
                : $"Customer {customer.FullName} created.";
            return RedirectToAction("Details", new { id = customer.Id });
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        return View(model);
    }

    [HttpGet("{id:guid}/edit")]
    [Authorize(Policy = Permissions.Customers.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var customer = await Sender.Send(new GetCustomerByIdQuery(id));
        if (customer is null) return NotFound();

        var model = new CustomerFormViewModel
        {
            CustomerId = customer.Id,
            FirstName = customer.FirstName,
            LastName = customer.LastName,
            Email = customer.Email,
            Phone = customer.Phone,
            Notes = customer.Notes
        };
        return View(model);
    }

    [HttpPost("{id:guid}/edit")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Permissions.Customers.Manage)]
    public async Task<IActionResult> Edit(Guid id, CustomerFormViewModel model)
    {
        model.CustomerId = id;
        if (!ModelState.IsValid)
            return View(model);

        await Sender.Send(new UpdateCustomerProfileCommand(id, new UpdateCustomerProfileRequest(
            model.FirstName,
            model.LastName,
            string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone,
            string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes)));

        TempData["Success"] = "Customer details updated.";
        return RedirectToAction("Details", new { id });
    }

    [HttpPost("{id:guid}/addresses")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Permissions.Customers.Manage)]
    public async Task<IActionResult> AddAddress(Guid id, AdminCustomerAddressFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Please complete all required address fields.";
            return RedirectToAction("Details", new { id });
        }

        try
        {
            await Sender.Send(new AddCustomerAddressCommand(id, new AddAddressRequest(
                model.Label,
                model.Street,
                model.City,
                model.State,
                model.Country,
                string.IsNullOrWhiteSpace(model.PostalCode) ? null : model.PostalCode,
                model.IsDefault)));

            TempData["Success"] = "Address added.";
        }
        catch (DomainException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction("Details", new { id });
    }

    [HttpPost("{id:guid}/addresses/{addressId:guid}/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Permissions.Customers.Manage)]
    public async Task<IActionResult> RemoveAddress(Guid id, Guid addressId)
    {
        try
        {
            await Sender.Send(new RemoveCustomerAddressCommand(id, addressId));
            TempData["Success"] = "Address removed.";
        }
        catch (DomainException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction("Details", new { id });
    }
}
