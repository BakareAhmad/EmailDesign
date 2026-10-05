using Asp.Versioning;
using DeChimex.Persistence.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers;

/// <summary>
/// Business application settings. The authenticated settings management surface
/// (list/edit/bulk-save) now lives in the MVC AdminSettingsController; only the anonymous
/// whitelisted storefront contract remains as an API endpoint.
/// </summary>
[ApiVersion("1.0")]
[AllowAnonymous]
public sealed class SettingsController(DeChimexDbContext db, DeChimex.Application.Common.Interfaces.ICacheService cache) : ApiControllerBase
{
    private static readonly TimeSpan SettingsTtl = TimeSpan.FromMinutes(15);

    /// <summary>Business keys that are safe to expose to anonymous storefront visitors.</summary>
    private static readonly string[] PublicKeys =
    [
        "business.name",
        "business.motto",
        "business.email",
        "business.phone",
        "business.instagram",
        "business.tiktok",
        "business.address",
        "business.pickup_address",
        "business.whatsapp",
    ];

    /// <summary>Whitelisted storefront business details for public pages (footer, contact). Anonymous; served from the same cached map so it stays consistent with admin saves.</summary>
    [HttpGet("public")]
    public async Task<IActionResult> GetPublic(CancellationToken ct)
    {
        var map = await cache.GetOrCreateAsync(DeChimex.Application.Common.CacheKeys.SettingsAll,
            token => db.ApplicationSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, token),
            SettingsTtl, ct);
        var filtered = PublicKeys.Where(map.ContainsKey).ToDictionary(k => k, k => map[k]);
        return Success(filtered);
    }
}
