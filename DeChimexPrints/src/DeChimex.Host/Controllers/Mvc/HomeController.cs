using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>
/// MVC landing page. Phase 1 skeleton: proves cookie auth, permission policy and view
/// rendering. Feature pages (admin/portal/storefront) are added in later phases.
/// </summary>
[Authorize]
public sealed class HomeController : Controller
{
    public IActionResult Index()
    {
        ViewData["FullName"] = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "there";
        ViewData["IsOwner"] = User.IsInRole(RoleNames.Owner);
        ViewData["Permissions"] = User.Claims
            .Where(c => c.Type == Permissions.ClaimType)
            .Select(c => c.Value)
            .OrderBy(v => v)
            .ToList();
        return View();
    }
}
