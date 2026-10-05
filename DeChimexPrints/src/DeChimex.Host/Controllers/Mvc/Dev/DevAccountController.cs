using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Auth.Commands;
using DeChimex.Contracts.Auth;
using DeChimex.Host.Models.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>Developer self-service: change own password (reuses ChangePasswordCommand).</summary>
public sealed class DevAccountController : DevControllerBase
{
    private bool Forced => User.HasClaim("must_change_password", "true");

    [HttpGet("/dev/account/password")]
    public IActionResult Password(string? forced = null)
    {
        ViewData["Title"] = "Change password";
        ViewData["DevNav"] = "account";
        ViewBag.Forced = Forced || forced == "1";
        return DevView("Password", new ChangePasswordFormViewModel());
    }

    [HttpPost("/dev/account/password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(ChangePasswordFormViewModel model)
    {
        ViewData["Title"] = "Change password";
        ViewData["DevNav"] = "account";
        ViewBag.Forced = Forced;
        if (!ModelState.IsValid) return DevView("Password", model);
        try
        {
            await Sender.Send(new ChangePasswordCommand(new ChangePasswordRequest(model.CurrentPassword, model.NewPassword)));
            if (Forced)
            {
                var claims = User.Claims.Where(c => c.Type != "must_change_password").ToList();
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            }
            TempData["Success"] = "Your password was updated. Use it the next time you sign in.";
            return Redirect("/dev/account/password");
        }
        catch (ValidationAppException ex)
        {
            foreach (var (field, messages) in ex.Errors)
                foreach (var message in messages)
                    ModelState.AddModelError(field.StartsWith("Request.") ? field["Request.".Length..] : field, message);
            return DevView("Password", model);
        }
        catch (NotFoundException)
        {
            TempData["Error"] = "Your account was not found.";
            return Redirect("/dev/account/password");
        }
    }
}
