using DeChimex.Host.Models.Admin;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Models;
using DeChimex.Application.Features.Users;
using DeChimex.Contracts.Auth;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin staff user + role management. Reuses user management commands/queries directly.</summary>
[Authorize(Policy = Permissions.Users.View)]
[Route("admin/users")]
public sealed class AdminUsersController : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, string? search = null)
    {
        ViewData["Title"] = "Users";
        ViewData["Heading"] = "Users";
        ViewData["ActiveNav"] = "users";

        var result = await Sender.Send(new SearchUsersQuery(new PagedRequest
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
        ViewBag.CanCreate = User.HasClaim(Permissions.ClaimType, Permissions.Users.Create);
        ViewBag.CanDisable = User.HasClaim(Permissions.ClaimType, Permissions.Users.Disable);
        return View(result.Items);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Users.Create)]
    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New staff user";
        ViewData["Heading"] = "Users";
        ViewData["ActiveNav"] = "users";
        await PopulateRolesAsync();
        return View(new StaffUserFormViewModel());
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Users.Create)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(StaffUserFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await PopulateRolesAsync();
            return View(model);
        }

        try
        {
            await Sender.Send(new CreateStaffUserCommand(new CreateStaffUserRequest(
                model.FirstName.Trim(), model.LastName.Trim(), model.Email.Trim(), model.Password,
                model.Username.Trim(), model.RoleIds ?? new List<Guid>())));
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateRolesAsync();
            return View(model);
        }
        catch (ValidationAppException ex)
        {
            // Command validation is stricter than the form annotations (password
            // complexity, at-least-one-role): surface failures on the form instead
            // of escaping as a 500. Keys arrive as "Request.<Prop>" — strip the
            // prefix so messages bind to the matching fields (AccountController
            // uses the same mapping).
            foreach (var (key, errors) in ex.Errors)
            {
                var field = key.StartsWith("Request.", StringComparison.OrdinalIgnoreCase)
                    ? key["Request.".Length..]
                    : key;
                foreach (var error in errors)
                    ModelState.AddModelError(field, error);
            }
            await PopulateRolesAsync();
            return View(model);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/username")]
    [Authorize(Policy = Permissions.Users.Create)]
    public async Task<IActionResult> Username(Guid id)
    {
        var user = await Sender.Send(new GetUserByIdQuery(id));
        if (user is null) return NotFound();
        ViewData["Title"] = $"Username: {user.FullName}";
        ViewData["Heading"] = "Users";
        ViewData["ActiveNav"] = "users";
        return View(new UsernameFormViewModel
        {
            UserId = user.Id,
            DisplayName = user.FullName,
            Email = user.Email,
            CurrentUsername = user.Username,
            Username = user.Username ?? string.Empty
        });
    }

    [HttpPost("{id:guid}/username")]
    [Authorize(Policy = Permissions.Users.Create)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Username(Guid id, UsernameFormViewModel model)
    {
        model.UserId = id;
        var user = await Sender.Send(new GetUserByIdQuery(id));
        if (user is null) return NotFound();
        model.DisplayName = user.FullName;
        model.Email = user.Email;
        model.CurrentUsername = user.Username;
        ViewData["Title"] = $"Username: {user.FullName}";
        ViewData["Heading"] = "Users";
        ViewData["ActiveNav"] = "users";

        if (!ModelState.IsValid)
            return View(model);

        try
        {
            await Sender.Send(new SetUsernameCommand(id, model.Username.Trim()));
            TempData["Success"] = $"Username updated to {model.Username.Trim().ToLowerInvariant()}.";
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
        catch (ValidationAppException ex)
        {
            // Same Request.-prefix stripping as Create (AccountController pattern).
            foreach (var (key, errors) in ex.Errors)
            {
                var field = key.StartsWith("Request.", StringComparison.OrdinalIgnoreCase)
                    ? key["Request.".Length..]
                    : key;
                foreach (var error in errors)
                    ModelState.AddModelError(field, error);
            }
            return View(model);
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/active")]
    [Authorize(Policy = Permissions.Users.Disable)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(Guid id, bool isActive)
    {
        await Sender.Send(new SetUserActiveCommand(id, isActive));
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("roles")]
    [Authorize(Policy = Permissions.Users.View)]
    public async Task<IActionResult> Roles()
    {
        ViewData["Title"] = "Roles";
        ViewData["Heading"] = "Roles";
        ViewData["ActiveNav"] = "users";

        var roles = await Sender.Send(new GetRolesQuery());
        ViewBag.CanAssign = User.HasClaim(Permissions.ClaimType, Permissions.Users.AssignRoles);
        return View(roles);
    }

    [HttpGet("roles/create")]
    [Authorize(Policy = Permissions.Users.AssignRoles)]
    public IActionResult RoleCreate()
    {
        ViewData["Title"] = "New role";
        ViewData["Heading"] = "Roles";
        ViewData["ActiveNav"] = "users";
        return View("RoleForm", new RoleFormViewModel());
    }

    [HttpPost("roles/create")]
    [Authorize(Policy = Permissions.Users.AssignRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RoleCreate(RoleFormViewModel model)
    {
        ViewData["Title"] = "New role";
        ViewData["Heading"] = "Roles";
        ViewData["ActiveNav"] = "users";

        if (!ModelState.IsValid)
            return View("RoleForm", model);

        try
        {
            var role = await Sender.Send(new CreateRoleCommand(new CreateRoleRequest(
                model.Name.Trim(),
                string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim())));
            TempData["Success"] = $"Role {role.Name} created. Assign its permissions next.";
            return RedirectToAction(nameof(RolePermissions), new { id = role.Id });
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("RoleForm", model);
        }
    }

    [HttpGet("roles/{id:guid}/edit")]
    [Authorize(Policy = Permissions.Users.AssignRoles)]
    public async Task<IActionResult> RoleEdit(Guid id)
    {
        var roles = await Sender.Send(new GetRolesQuery());
        var role = roles.FirstOrDefault(r => r.Id == id);
        if (role is null) return NotFound();
        if (role.IsSystemRole)
        {
            TempData["Error"] = "System roles cannot be modified.";
            return RedirectToAction(nameof(Roles));
        }

        ViewData["Title"] = $"Edit {role.Name}";
        ViewData["Heading"] = "Roles";
        ViewData["ActiveNav"] = "users";
        return View("RoleForm", new RoleFormViewModel
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description
        });
    }

    [HttpPost("roles/{id:guid}/edit")]
    [Authorize(Policy = Permissions.Users.AssignRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RoleEdit(Guid id, RoleFormViewModel model)
    {
        model.Id = id;
        if (!ModelState.IsValid)
        {
            ViewData["Title"] = "Edit role";
            ViewData["Heading"] = "Roles";
            ViewData["ActiveNav"] = "users";
            return View("RoleForm", model);
        }

        try
        {
            await Sender.Send(new UpdateRoleCommand(id, new UpdateRoleRequest(
                model.Name.Trim(),
                string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim())));
            TempData["Success"] = "Role updated.";
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewData["Title"] = "Edit role";
            ViewData["Heading"] = "Roles";
            ViewData["ActiveNav"] = "users";
            return View("RoleForm", model);
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Roles));
        }
        return RedirectToAction(nameof(Roles));
    }

    [HttpPost("roles/{id:guid}/delete")]
    [Authorize(Policy = Permissions.Users.AssignRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RoleDelete(Guid id)
    {
        try
        {
            await Sender.Send(new DeleteRoleCommand(id));
            TempData["Success"] = "Role deleted.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (NotFoundException)
        {
            TempData["Error"] = "Role not found.";
        }
        return RedirectToAction(nameof(Roles));
    }

    [HttpGet("roles/{id:guid}/permissions")]
    [Authorize(Policy = Permissions.Users.AssignRoles)]
    public async Task<IActionResult> RolePermissions(Guid id)
    {
        var roles = await Sender.Send(new GetRolesQuery());
        var role = roles.FirstOrDefault(r => r.Id == id);
        if (role is null) return NotFound();

        // Developer Portal (dev.*) permissions are not business permissions: they are hidden
        // from the checklist and preserved untouched on save (granted only via the Developer role).
        var permissions = (await Sender.Send(new GetPermissionsQuery()))
            .Where(p => !Permissions.Developer.IsDeveloperPermission(p.Name)).ToList();
        ViewData["Title"] = $"Permissions: {role.Name}";
        ViewData["Heading"] = "Roles";
        ViewData["ActiveNav"] = "users";
        ViewBag.Role = role;
        return View(permissions);
    }

    [HttpPost("roles/{id:guid}/permissions")]
    [Authorize(Policy = Permissions.Users.AssignRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RolePermissions(Guid id, List<Guid> PermissionIds)
    {
        var roles = await Sender.Send(new GetRolesQuery());
        var role = roles.FirstOrDefault(r => r.Id == id);
        if (role is null) return NotFound();

        try
        {
            await Sender.Send(new UpdateRolePermissionsCommand(id, new UpdateRolePermissionsRequest(
                PermissionIds?.Distinct().ToList() ?? new List<Guid>())));
            TempData["Success"] = $"Permissions for {role.Name} updated.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Roles));
    }

    private async Task PopulateRolesAsync()
    {
        var roles = await Sender.Send(new GetRolesQuery());
        ViewBag.Roles = roles
            .Where(r => !r.IsSystemRole || r.Name != RoleNames.Customer)
            .Select(r => new SelectListItem(r.Name, r.Id.ToString()))
            .ToList();
    }
}
