using DeChimex.Application.Common;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Admin company settings. The existing Settings API controller manages the ApplicationSettings
/// table directly (no CQRS command), so this MVC controller mirrors that approach.
/// </summary>
[Authorize(Policy = Permissions.Settings.View)]
[Route("admin/settings")]
public sealed class AdminSettingsController(DeChimexDbContext db, ICacheService cache, IEmailHealthCheck emailHealth) : MvcControllerBase
{
    private static readonly TimeSpan SettingsTtl = TimeSpan.FromMinutes(15);

    private async Task<Dictionary<string, string>> LoadAsync()
    {
        return await cache.GetOrCreateAsync(CacheKeys.SettingsAll,
            token => db.ApplicationSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, token),
            SettingsTtl, HttpContext.RequestAborted);
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Company settings";
        ViewData["Heading"] = "Settings";
        ViewData["ActiveNav"] = "settings";

        ViewBag.Settings = await LoadAsync();
        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Settings.Manage);
        // Live provider status so email problems are visible on the page, not buried in logs.
        ViewBag.EmailHealth = await emailHealth.CheckAsync(null, HttpContext.RequestAborted);
        return View();
    }

    /// <summary>Sends a test email to the signed-in admin and reports the provider's verdict.</summary>
    [HttpPost("email-test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailTest()
    {
        // The cookie carries the address in the Email claim (Name holds the display name).
        var me = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        if (string.IsNullOrWhiteSpace(me))
        {
            TempData["Error"] = "Could not determine your email address for the test.";
            return RedirectToAction(nameof(Index));
        }

        var result = await emailHealth.CheckAsync(me, HttpContext.RequestAborted);
        TempData[result.Healthy ? "Success" : "Error"] =
            result.Detail is null ? result.Status : $"{result.Status} - {result.Detail}";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.Settings.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index([FromForm] Dictionary<string, string> values)
    {
        if (values is not null && values.Count > 0)
        {
            var existing = await db.ApplicationSettings.ToListAsync();
            foreach (var (key, value) in values)
            {
                // The service fee is financially significant: it changes ONLY through the
                // confirmed ServiceFeePreview/ServiceFeeConfirm flow below, never silently
                // through this generic save (brief §3).
                if (key == ServiceFeeSettings.Key) continue;

                var stored = (value ?? string.Empty).Trim();

                // The WhatsApp setting holds a number, never a URL. Normalize whatever
                // the admin typed (a number or a legacy wa.me link) to digits.
                if (key == "business.whatsapp")
                    stored = DeChimex.Shared.WhatsApp.NormalizeNumber(stored);

                var setting = existing.FirstOrDefault(s => s.Key == key);
                if (setting is null)
                    await db.ApplicationSettings.AddAsync(ApplicationSetting.Create(key, stored));
                else
                    setting.SetValue(stored);
            }
            await db.SaveChangesAsync();
            await cache.RemoveAsync(CacheKeys.SettingsAll);
        }

        return RedirectToAction(nameof(Index));
    }

    // ---------------------------------------------------------------------
    // Service fee (financially significant) — two-step change with explicit
    // confirmation and optimistic concurrency on the reviewed value (brief
    // §3/§15). Deliberately NOT part of the generic form above: an edit must
    // be reviewed and confirmed, and the audit trail must record old → new.
    // ---------------------------------------------------------------------

    /// <summary>Step 1: validate the requested percentage and show the confirmation
    /// (current value, new value, future-only effect). Nothing is saved here.</summary>
    [HttpPost("service-fee")]
    [Authorize(Policy = Permissions.Settings.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceFeePreview(string? newValue)
    {
        if (!ServiceFeeSettings.TryParse(newValue, out var requested))
        {
            TempData["Error"] = "Enter a percentage between 0 and 100 (at most 2 decimal places).";
            return RedirectToAction(nameof(Index));
        }

        var current = await CurrentServiceFeeAsync();
        if (current == requested)
        {
            TempData["Success"] = "Service fee unchanged.";
            return RedirectToAction(nameof(Index));
        }

        return View("ServiceFeeConfirm", new ServiceFeeChangeModel
        {
            CurrentPercent = ServiceFeeSettings.Format(current),
            RequestedPercent = ServiceFeeSettings.Format(requested)
        });
    }

    /// <summary>Step 2: save only after explicit confirmation, and only if the value the
    /// administrator reviewed is still the current one (a stale form never overwrites a
    /// newer configuration — brief §15). The BusinessActivityCapture interceptor records
    /// "Service fee configuration changed" (old → new) atomically with this save.</summary>
    [HttpPost("service-fee/confirm")]
    [Authorize(Policy = Permissions.Settings.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceFeeConfirm(ServiceFeeChangeModel model)
    {
        if (!ServiceFeeSettings.TryParse(model.RequestedPercent, out var requested)
            || !ServiceFeeSettings.TryParse(model.CurrentPercent, out var reviewed))
        {
            TempData["Error"] = "Enter a percentage between 0 and 100 (at most 2 decimal places).";
            return RedirectToAction(nameof(Index));
        }

        var setting = await db.ApplicationSettings
            .FirstOrDefaultAsync(s => s.Key == ServiceFeeSettings.Key, HttpContext.RequestAborted);
        var current = setting is null ? ServiceFeeSettings.DefaultPercent : ServiceFeeSettings.Parse(setting.Value);

        if (current != reviewed)
        {
            TempData["Error"] = $"The service fee was changed to {ServiceFeeSettings.Format(current)}% while you were reviewing. Nothing was saved — please review again.";
            return RedirectToAction(nameof(Index));
        }

        if (current == requested)
        {
            TempData["Success"] = "Service fee unchanged.";
            return RedirectToAction(nameof(Index));
        }

        if (setting is null)
            db.ApplicationSettings.Add(ApplicationSetting.Create(ServiceFeeSettings.Key, ServiceFeeSettings.Format(requested)));
        else
            setting.SetValue(ServiceFeeSettings.Format(requested));

        await db.SaveChangesAsync(HttpContext.RequestAborted);
        await cache.RemoveAsync(CacheKeys.SettingsAll);
        TempData["Success"] = $"Service fee changed from {ServiceFeeSettings.Format(current)}% to {ServiceFeeSettings.Format(requested)}%. Applies to future transactions.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<decimal> CurrentServiceFeeAsync()
        => ServiceFeeSettings.Parse((await db.ApplicationSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == ServiceFeeSettings.Key, HttpContext.RequestAborted))?.Value);
}

/// <summary>Confirmation dialog values for a service-fee change. Percentages travel as
/// canonical strings (invariant format) so model binding is culture-safe.</summary>
public sealed class ServiceFeeChangeModel
{
    public string CurrentPercent { get; init; } = ServiceFeeSettings.Format(ServiceFeeSettings.DefaultPercent);
    public string RequestedPercent { get; init; } = ServiceFeeSettings.Format(ServiceFeeSettings.DefaultPercent);
}
