using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Categories;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace DeChimex.Host.Controllers.Mvc.Admin;

[Authorize(Policy = Permissions.Categories.View)]
[Route("admin/major-categories")]
public sealed class AdminMajorCategoriesController : MvcControllerBase
{
    private void Page(string title)
    {
        ViewData["Title"] = title; ViewData["Heading"] = "Major categories"; ViewData["ActiveNav"] = "major-categories";
        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Categories.Manage);
    }
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        Page("Major categories"); return View(await Sender.Send(new GetMajorCategoriesQuery()));
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var major = (await Sender.Send(new GetMajorCategoriesQuery())).FirstOrDefault(m => m.Id == id);
        if (major is null) return NotFound();
        Page(major.Name);
        ViewBag.Categories = (await Sender.Send(new GetCategoriesQuery(false))).Where(c => c.MajorCategoryId == id).ToList();
        return View(major);
    }
    [HttpGet("create")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    public IActionResult Create() { Page("New major category"); return View("Form", new MajorCategoryFormViewModel()); }
    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var major = (await Sender.Send(new GetMajorCategoriesQuery())).FirstOrDefault(m => m.Id == id);
        if (major is null) return NotFound();
        Page("Edit major category");
        return View("Form", new MajorCategoryFormViewModel { Id = id, Name = major.Name, Description = major.Description });
    }
    [HttpPost("create")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Create(MajorCategoryFormViewModel model) { model.Id = null; return Save(model); }
    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(Guid id, MajorCategoryFormViewModel model) { model.Id = id; return Save(model); }
    private async Task<IActionResult> Save(MajorCategoryFormViewModel model)
    {
        Page(model.Id is null ? "New major category" : "Edit major category");
        if (!ModelState.IsValid) return View("Form", model);
        try
        {
            var id = await Sender.Send(new SaveMajorCategoryCommand(model.Id, model.Name, model.Description));
            TempData["Success"] = "Major category saved.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (ConflictException ex) { ModelState.AddModelError("", ex.Message); return View("Form", model); }
        catch (NotFoundException) { return NotFound(); }
    }
    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        try { await Sender.Send(new DeleteMajorCategoryCommand(id)); TempData["Success"] = "Major category deleted."; }
        catch (ConflictException ex) { TempData["Error"] = ex.Message; }
        catch (NotFoundException) { return NotFound(); }
        return RedirectToAction(nameof(Index));
    }
}
