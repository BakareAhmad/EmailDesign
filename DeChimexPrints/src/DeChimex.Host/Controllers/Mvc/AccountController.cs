using System.Security.Claims;
using DeChimex.Host.Models;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Auth.Commands;
using DeChimex.Contracts.Auth;
using DeChimex.Domain.Exceptions;
using DeChimex.Shared.Constants;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>
/// MVC authentication. Reuses the existing LoginCommand / RegisterCommand and the
/// transport-agnostic permission/role claim model, signing users in with cookie authentication.
/// </summary>
public sealed class AccountController(ISender sender) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");

        ViewData["ReturnUrl"] = returnUrl;
        ViewBag.Stats = await LoadMarketingStatsAsync();
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            var auth = await sender.Send(new LoginCommand(
                new LoginRequest(model.Email.Trim(), model.Password),
                HttpContext.Connection.RemoteIpAddress?.ToString()));

            await SignInAsync(auth);

            // The bootstrap owner must choose a new password before doing anything else.
            // Developer-only accounts change it inside the Developer Portal.
            if (auth.MustChangePassword)
                return Redirect(IsDeveloperOnly(auth.Roles)
                    ? "/dev/account/password?forced=1"
                    : "/admin/security/password?forced=1");

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            // Staff land on the first admin page their role can actually access;
            // customers land in the portal.
            var landing = FirstLandingPage(auth.Roles);
            return RedirectToAction(landing.Action, landing.Controller);
        }
        catch (UnauthorizedAppException)
        {
            RecordAuthFailure(model.Email, "Invalid credentials");
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
        }
        catch (EmailNotVerifiedException)
        {
            // Gated signup: the LOGIN form has no token field, so re-showing it with the
            // "enter the verification token" message strands the user (they cannot proceed
            // from that page). Send them to the verify page instead - it has the token
            // input, accepts the pasted link, and offers "Resend verification email".
            TempData["Info"] = "Your email is not verified yet - enter the verification token we emailed you to finish signing up.";
            return RedirectToAction(nameof(VerifyEmail), new { email = model.Email.Trim() });
        }
        catch (ForbiddenException ex)
        {
            RecordAuthFailure(model.Email, "Sign-in refused: " + ex.Message);
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (ValidationAppException ex)
        {
            AddValidationErrors(ex);
        }

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Register(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");

        ViewData["ReturnUrl"] = returnUrl;
        ViewBag.Stats = await LoadMarketingStatsAsync();
        return View(new RegisterViewModel { ReturnUrl = returnUrl });
    }

    /// <summary>Loads the editable marketing stats shown on the sign-in screens.</summary>
    private async Task<Dictionary<string, string>> LoadMarketingStatsAsync()
    {
        var settings = HttpContext.RequestServices
            .GetRequiredService<DeChimex.Domain.Repositories.IApplicationSettingRepository>();
        var keys = new[]
        {
            "business.stat_projects", "business.stat_brands", "business.stat_turnaround"
        };
        var result = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            var s = await settings.GetByKeyAsync(key);
            if (s is not null && !string.IsNullOrWhiteSpace(s.Value))
                result[key] = s.Value;
        }
        return result;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("sensitive")] // AUD-024: OTP emails cost Brevo credits; cap per IP
    public async Task<IActionResult> Register(RegisterViewModel model, string? returnUrl = null)
    {
        // AUD-024: honeypot — the field is hidden by CSS so humans never fill it; bots do.
        // Pretend success (same redirect as a real registration) and do nothing: no account,
        // no OTP email, no enumeration signal.
        if (!string.IsNullOrWhiteSpace(model.CompanyWebsite))
        {
            TempData["Info"] = "Almost there - we emailed you a verification link. Enter the token (or paste the link) to complete your signup.";
            return RedirectToAction(nameof(VerifyEmail), new { email = model.Email.Trim() });
        }

        if (!ModelState.IsValid)
            return View(model);

        try
        {
            var auth = await sender.Send(new RegisterCommand(
                new RegisterRequest(
                    model.FirstName.Trim(),
                    model.LastName.Trim(),
                    model.Email.Trim(),
                    model.Phone.Trim(),
                    model.Password),
                HttpContext.Connection.RemoteIpAddress?.ToString()));

            // Gated signup: NO session here. Brevo emails the verification token/link;
            // the account only becomes usable after the user submits that token.
            TempData["Info"] = "Almost there - we emailed you a verification link. Enter the token (or paste the link) to complete your signup.";
            return RedirectToAction(nameof(VerifyEmail), new { email = model.Email.Trim() });
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (ValidationAppException ex)
        {
            AddValidationErrors(ex);
        }

        return View(model);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();

    // ---- Forgot password ----
    [HttpGet]
    public IActionResult ForgotPassword()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        return View(new ForgotPasswordViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("sensitive")] // AUD-024: reset emails cost money; cap per IP
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        // ForgotPassword never surfaces whether the account exists (anti-enumeration);
        // always show the same confirmation. The email is only sent when the user exists.
        try
        {
            await sender.Send(new ForgotPasswordCommand(new ForgotPasswordRequest(model.Email.Trim())));
            ViewBag.Sent = true;
        }
        catch (ValidationAppException ex)
        {
            AddValidationErrors(ex);
        }
        return View(model);
    }

    // ---- Reset password (from the emailed link) ----
    [HttpGet]
    public IActionResult ResetPassword(string? email = null, string? token = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");

        var model = new ResetPasswordViewModel
        {
            Email = email ?? string.Empty,
            Token = token ?? string.Empty
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            await sender.Send(new ResetPasswordCommand(new ResetPasswordRequest(
                model.Email.Trim(), model.Token.Trim(), model.NewPassword)));
            TempData["Success"] = "Your password has been reset. Please sign in with your new password.";
            return RedirectToAction(nameof(Login));
        }
        catch (NotFoundException)
        {
            ModelState.AddModelError(string.Empty, "We could not find an account for that email.");
        }
        catch (BusinessRuleValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (ValidationAppException ex)
        {
            AddValidationErrors(ex);
        }

        return View(model);
    }

    // ---- Verify email (from the emailed link) ----
    [HttpGet]
    public async Task<IActionResult> VerifyEmail(string? email = null, string? token = null)
    {
        var model = new VerifyEmailViewModel
        {
            Email = email ?? string.Empty,
            Token = token ?? string.Empty
        };
        // If the link already carries a token, verify it immediately (common SPA-style flow).
        if (!string.IsNullOrWhiteSpace(model.Email) && !string.IsNullOrWhiteSpace(model.Token))
            return await VerifyEmailPostAsync(model);

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyEmail(VerifyEmailViewModel model)
        => await VerifyEmailPostAsync(model);

    private async Task<IActionResult> VerifyEmailPostAsync(VerifyEmailViewModel model)
    {
        if (!ModelState.IsValid)
            return View("VerifyEmail", model);

        // Accept either the raw token or the full verification link from the Brevo
        // email (the link carries email + token as query params).
        var email = model.Email.Trim();
        var token = model.Token.Trim();
        if (token.Contains("token=", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(token, UriKind.Absolute, out var link))
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(link.Query);
            if (query.TryGetValue("token", out var tv))
                token = tv.ToString();
            if (string.IsNullOrWhiteSpace(email) && query.TryGetValue("email", out var ev))
                email = ev.ToString();
            model.Email = email;
            model.Token = token;
        }

        try
        {
            var auth = await sender.Send(new VerifyEmailCommand(new VerifyEmailRequest(email, token)));
            // Signup completes here: the token matched, so sign the user in.
            await SignInAsync(auth);
            TempData["Success"] = "Email verified - welcome to DE-CHIMEX PRINT HUB!";
            return RedirectToAction("Index", "PortalDashboard");
        }
        catch (NotFoundException)
        {
            ModelState.AddModelError(string.Empty, "We could not find an account with that email.");
        }
        catch (BusinessRuleValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (ValidationAppException ex)
        {
            AddValidationErrors(ex);
        }

        return View("VerifyEmail", model);
    }

    /// <summary>
    /// Surfaces pipeline validation failures as ModelState errors. The MediatR
    /// ValidationBehavior throws ValidationAppException, which does NOT derive from
    /// FluentValidation.ValidationException, so catching the latter let signup-time
    /// validation failures escape as unhandled 500s. Keys like "Request.Phone" are
    /// stripped to the view-model property name so the field-level asp-validation-for
    /// spans show them; anything unmapped lands in the validation summary.
    /// </summary>
    /// <summary>Re-sends the verification email (anti-enumeration: always reports success).</summary>
    [HttpPost]
    [EnableRateLimiting("sensitive")] // AUD-024: OTP resend is the classic bombing vector
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendVerification(string? email)
    {
        if (!string.IsNullOrWhiteSpace(email))
            await sender.Send(new ResendVerificationEmailCommand(new ResendVerificationEmailRequest(email.Trim())));
        TempData["Info"] = "If that address matches an unverified account, a fresh verification email is on its way.";
        return RedirectToAction(nameof(VerifyEmail), new { email = email?.Trim() });
    }

    /// <summary>Developer Portal security telemetry: failed sign-in (email masked, never the password).</summary>
    private void RecordAuthFailure(string? email, string reason)
        => HttpContext.RequestServices.GetService<DeChimex.Application.Common.Interfaces.ITechnicalEventRecorder>()?.Record(
            new DeChimex.Application.Common.Interfaces.TechnicalEventEntry(
                DeChimex.Application.Common.Interfaces.TechnicalSeverity.Warning,
                DeChimex.Application.Common.Interfaces.TechnicalCategory.Security,
                DeChimex.Application.Common.Interfaces.TechnicalEventType.AuthenticationFailure,
                $"Failed sign-in for {DeChimex.Shared.Diagnostics.SensitiveDataRedactor.MaskEmail(email?.Trim()) ?? "(blank)"}: {reason}.",
                Source: nameof(AccountController),
                MessageTemplate: "Failed sign-in: " + reason,
                Properties: new Dictionary<string, string?> { ["ip"] = HttpContext.Connection.RemoteIpAddress?.ToString() }));

    private static bool IsDeveloperOnly(IReadOnlyList<string> roles)
        => roles.Contains(RoleNames.Developer) && !roles.Any(r => RoleNames.StaffRoles().Contains(r));

    /// <summary>Issues the authentication cookie for a verified AuthResponse (login + post-verification signup).</summary>
    private async Task SignInAsync(AuthResponse auth)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, auth.UserId.ToString()),
            new(ClaimTypes.Email, auth.Email),
            new("name", auth.FullName)
        };
        // AUD-006: per-request authorization freshness — the cookie carries the user's
        // SecurityStamp at sign-in; SessionFreshnessMiddleware compares it to the live
        // value on every request and re-issues the cookie when roles/permissions changed.
        if (!string.IsNullOrEmpty(auth.SecurityStamp))
            claims.Add(new Claim("secstamp", auth.SecurityStamp));
        claims.AddRange(auth.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(auth.Permissions.Select(p => new Claim(Permissions.ClaimType, p)));

        if (auth.MustChangePassword)
            claims.Add(new Claim("must_change_password", "true"));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }

    private void AddValidationErrors(ValidationAppException ex)
    {
        foreach (var (key, errors) in ex.Errors)
        {
            var field = key.StartsWith("Request.", StringComparison.OrdinalIgnoreCase)
                ? key["Request.".Length..]
                : key;
            foreach (var error in errors)
                ModelState.AddModelError(field, error);
        }
    }

    /// <summary>
    /// Maps a user's roles to the first admin page they are allowed to open
    /// (staff roles without reports.view would otherwise land on AccessDenied).
    /// </summary>
    private static (string Action, string Controller) FirstLandingPage(IReadOnlyList<string> roles)
    {
        if (roles.Contains(RoleNames.Customer) && roles.Count == 1)
            return ("Index", "PortalDashboard");

        if (roles.Contains(RoleNames.Owner) || roles.Contains(RoleNames.Manager))
            return ("Index", "AdminDashboard");
        if (roles.Contains(RoleNames.SalesStaff))
            return ("Index", "AdminOrders");
        if (roles.Contains(RoleNames.InventoryStaff))
            return ("Index", "AdminInventory");
        if (roles.Contains(RoleNames.Designer))
            return ("Index", "AdminGallery");
        // Developer-only accounts land in the Developer Portal (never the customer portal).
        if (IsDeveloperOnly(roles))
            return ("Index", "DevPortal");

        return roles.Any(r => RoleNames.StaffRoles().Contains(r))
            ? ("Index", "AdminDashboard")
            : ("Index", "PortalDashboard");
    }
}
