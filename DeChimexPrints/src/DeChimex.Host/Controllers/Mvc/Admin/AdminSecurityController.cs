using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Auth.Commands;
using DeChimex.Contracts.Auth;
using DeChimex.Host.Models.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Staff self-service security page. Change your own password. Open to any staff
/// role, never to customers (the customer role cannot reach /admin/* anyway).
/// </summary>
[Authorize]
[Route("admin/security")]
public sealed class AdminSecurityController : MvcControllerBase
{
    private bool IsStaff() => Shared.Constants.RoleNames.StaffRoles().Any(r => User.IsInRole(r));

    private bool Forced => User.HasClaim("must_change_password", "true");

    [HttpGet("password")]
    public IActionResult Password(string? forced = null)
    {
        if (!IsStaff()) return RedirectToRoute("default");
        ViewData["Title"] = "Change password";
        ViewData["Heading"] = "Security";
        ViewData["ActiveNav"] = "settings";
        ViewBag.Forced = Forced || forced == "1";
        return View(new ChangePasswordFormViewModel());
    }

    /// <summary>
    /// Re-issues the authentication cookie without the must_change_password claim so the
    /// enforcement middleware stops bouncing after a successful change, without a re-login.
    /// </summary>
    private async Task ClearMustChangeClaimAsync()
    {
        if (!User.HasClaim("must_change_password", "true")) return;

        var claims = User.Claims.Where(c => c.Type != "must_change_password").ToList();
        var identity = new System.Security.Claims.ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new System.Security.Claims.ClaimsPrincipal(identity));
    }

    [HttpPost("password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(ChangePasswordFormViewModel model)
    {
        if (!IsStaff()) return RedirectToRoute("default");
        ViewData["Title"] = "Change password";
        ViewData["Heading"] = "Security";
        ViewData["ActiveNav"] = "settings";
        ViewBag.Forced = Forced;

        if (!ModelState.IsValid)
            return View(model);

        try
        {
            await Sender.Send(new ChangePasswordCommand(new ChangePasswordRequest(
                model.CurrentPassword, model.NewPassword)));
            await ClearMustChangeClaimAsync();
            TempData["Success"] = "Your password was updated. Use it the next time you sign in.";
            return RedirectToAction(nameof(Password));
        }
        catch (ValidationAppException ex)
        {
            foreach (var (field, messages) in ex.Errors)
                foreach (var message in messages)
                    ModelState.AddModelError(field, message);
            return View(model);
        }
        catch (NotFoundException)
        {
            TempData["Error"] = "Your account was not found.";
            return RedirectToAction(nameof(Password));
        }
    }
}
