using DeChimex.Host.Models.Admin;
using DeChimex.Application.Features.Categories;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Contracts.Categories;
using DeChimex.Contracts.Orders;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin category management. Reuses category queries/commands directly.</summary>
[Authorize(Policy = Permissions.Categories.View)]
[Route("admin/categories")]
public sealed class AdminCategoriesController(ICategoryOptionsQueries categoryOptions) : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(string? search = null, Guid? majorCategoryId = null, bool unassigned = false)
    {
        ViewData["Title"] = "Categories";
        ViewData["Heading"] = "Categories";
        ViewData["ActiveNav"] = "categories";

        var categories = await Sender.Send(new GetCategoriesQuery(ActiveOnly: false));

        // The category list is a single small cached entry (MED-004); filtering in memory
        // here keeps that cache intact instead of fragmenting it per search term.
        var trimmed = search?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            categories = categories
                .Where(c => c.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                    || (c.Description?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        if (unassigned) categories = categories.Where(c => c.MajorCategoryId == null).ToList();
        else if (majorCategoryId.HasValue) categories = categories.Where(c => c.MajorCategoryId == majorCategoryId).ToList();
        await PopulateMajors();
        ViewBag.MajorCategoryId = majorCategoryId; ViewBag.Unassigned = unassigned;
        ViewBag.Search = search;
        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Categories.Manage);
        return View(categories);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    public async Task<IActionResult> Create(Guid? majorCategoryId = null)
    {
        ViewData["Title"] = "New category";
        ViewData["Heading"] = "Categories";
        ViewData["ActiveNav"] = "categories";
        await PopulateMajors();
        return View(new CategoryFormViewModel { MajorCategoryId = majorCategoryId });
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CategoryFormViewModel model)
    {
        await PopulateMajors();
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            // DisplayOrder is automatic: null tells the handler to append at the end.
            await Sender.Send(new CreateCategoryCommand(new CreateCategoryRequest(
                model.Name.Trim(), model.Description, MajorCategoryId: model.MajorCategoryId)));
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var category = await Sender.Send(new GetCategoryByIdQuery(id));
        if (category is null) return NotFound();

        ViewData["Title"] = "Edit category";
        ViewData["Heading"] = "Categories";
        ViewData["ActiveNav"] = "categories";
        await PopulateMajors();
        return View(new CategoryFormViewModel
        {
            Id = category.Id,
            Name = category.Name,
            Description = category.Description,
            MajorCategoryId = category.MajorCategoryId,
            IsActive = category.IsActive
        });
    }

    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, CategoryFormViewModel model)
    {
        await PopulateMajors();
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            // DisplayOrder null = keep the category's current position (ordering is automatic).
            await Sender.Send(new UpdateCategoryCommand(id, new UpdateCategoryRequest(
                model.Name.Trim(), model.Description, null, model.IsActive, model.MajorCategoryId, UpdateMajorCategory: true)));
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            await Sender.Send(new DeleteCategoryCommand(id));
            TempData["Success"] = "Category deleted.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (NotFoundException)
        {
            TempData["Error"] = "Category not found.";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateMajors() => ViewBag.MajorCategories = await Sender.Send(new GetMajorCategoriesQuery());

    // ----------------------------------------------------------------------------------
    // Ordering options editor (CUSTOM_ORDER_FLOW_PLAN.md §4). The dynamic rows are
    // serialized to JSON client-side and rebuilt by SaveCategoryOptionsCommand.
    // ----------------------------------------------------------------------------------

    [HttpGet("{id:guid}/options")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    public async Task<IActionResult> Options(Guid id)
    {
        var category = await Sender.Send(new GetCategoryByIdQuery(id));
        if (category is null) return NotFound();

        ViewData["Title"] = $"Ordering options — {category.Name}";
        ViewData["Heading"] = "Categories";
        ViewData["ActiveNav"] = "categories";
        ViewBag.Category = category;
        ViewBag.Options = await categoryOptions.GetForCategoryAsync(id, HttpContext.RequestAborted);
        return View();
    }

    [HttpPost("{id:guid}/options")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOptions(Guid id, string optionsJson)
    {
        var category = await Sender.Send(new GetCategoryByIdQuery(id));
        if (category is null) return NotFound();

        List<SaveCategoryOptionDto> options;
        try
        {
            options = string.IsNullOrWhiteSpace(optionsJson)
                ? new List<SaveCategoryOptionDto>()
                : System.Text.Json.JsonSerializer.Deserialize<List<SaveCategoryOptionDto>>(
                    optionsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<SaveCategoryOptionDto>();
        }
        catch (System.Text.Json.JsonException)
        {
            TempData["Error"] = "The options data could not be read. Please try again.";
            return RedirectToAction(nameof(Options), new { id });
        }

        try
        {
            await Sender.Send(new SaveCategoryOptionsCommand(id, new SaveCategoryOptionsRequest(options)));
            TempData["Success"] = "Ordering options saved.";
        }
        catch (ValidationAppException ex)
        {
            TempData["Error"] = ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "Please fix the highlighted options.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Options), new { id });
    }
}
