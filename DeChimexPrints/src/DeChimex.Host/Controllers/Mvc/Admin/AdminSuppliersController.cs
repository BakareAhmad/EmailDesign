using DeChimex.Host.Models.Admin;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Admin supplier management. The existing Suppliers API controller manages suppliers directly
/// against the DbContext (there is no CQRS command for suppliers), so the MVC controller mirrors
/// that same direct approach - no HTTP self-call, same business rules.
/// </summary>
[Authorize(Policy = Permissions.Procurement.View)]
[Route("admin/suppliers")]
public sealed class AdminSuppliersController(DeChimexDbContext db, IUnitOfWork uow, ICurrentUser currentUser) : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Suppliers";
        ViewData["Heading"] = "Suppliers";
        ViewData["ActiveNav"] = "suppliers";

        var suppliers = await db.Suppliers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync();

        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Procurement.Approve);
        return View(suppliers);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    public IActionResult Create()
    {
        ViewData["Title"] = "New supplier";
        ViewData["Heading"] = "Suppliers";
        ViewData["ActiveNav"] = "suppliers";
        return View(new SupplierFormViewModel());
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SupplierFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var supplier = Supplier.Create(model.Name, model.Contact, model.Email, model.Phone, model.Category, model.Status);
        db.Suppliers.Add(supplier);
        await uow.SaveChangesAsync(HttpContext.RequestAborted);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var supplier = await db.Suppliers.FindAsync(new object[] { id });
        if (supplier is null) return NotFound();

        ViewData["Title"] = "Edit supplier";
        ViewData["Heading"] = "Suppliers";
        ViewData["ActiveNav"] = "suppliers";
        return View(new SupplierFormViewModel
        {
            Id = supplier.Id,
            Name = supplier.Name,
            Contact = supplier.Contact,
            Email = supplier.Email,
            Phone = supplier.Phone,
            Category = supplier.Category,
            Status = supplier.Status
        });
    }

    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, SupplierFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var supplier = await db.Suppliers.FindAsync(new object[] { id });
        if (supplier is null) return NotFound();

        supplier.Update(model.Name, model.Contact, model.Email, model.Phone, model.Category, model.Status);
        await uow.SaveChangesAsync(HttpContext.RequestAborted);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Procurement.Approve)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var supplier = await db.Suppliers.FindAsync(new object[] { id });
        if (supplier is not null)
        {
            supplier.MarkDeleted(currentUser.Email);
            await uow.SaveChangesAsync(HttpContext.RequestAborted);
        }
        return RedirectToAction(nameof(Index));
    }
}
