using DeChimex.Application.Common;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories;
using DeChimex.Contracts.Categories;
using DeChimex.Persistence.Context;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Services;

/// <summary>
/// Data the customer shell (top bar, menus, footer) needs on EVERY customer-facing
/// page — public storefront and signed-in portal alike. Injected into
/// <c>_CustomerLayout.cshtml</c> so pages served by portal controllers (which never
/// loaded business info before) render the same header/footer as the storefront.
/// Reads the same whitelisted settings snapshot as <see cref="Controllers.Mvc.StoreControllerBase"/>.
/// </summary>
public sealed class CustomerShellService(DeChimexDbContext db, ICacheService cache, ISender sender, IWebHostEnvironment env)
{
    /// <summary>
    /// Brand image files under wwwroot/img. They are optional at runtime: when a file is
    /// missing the views render a text/brand fallback instead of a broken image or a 404
    /// round-trip. See AI_HANDOFF.txt §89 for how to (re)install them.
    /// </summary>
    public string? LogoUrl => AssetUrl("img/logo.png");
    // Hero art is a true-alpha cut-out (tools/hero-art/extract_hero_alpha.py): WebP first, full RGBA PNG as the
    // <img> fallback (never JPG, which cannot carry transparency). Portrait hero-mobile.* serves <=720 px.
    public string? HeroDesktopUrl => AssetUrl("img/hero.png");
    public string? HeroMobileUrl => AssetUrl("img/hero-mobile.png");
    /// <summary>Optional lighter variants: half-size desktop (tablets, 1x laptops) and WebP twins.</summary>
    public string? HeroTabletUrl => AssetUrl("img/hero-768.png");
    public string? HeroDesktopWebpUrl => AssetUrl("img/hero.webp");
    public string? HeroTabletWebpUrl => AssetUrl("img/hero-768.webp");
    public string? HeroMobileWebpUrl => AssetUrl("img/hero-mobile.webp");

    /// <summary>
    /// Rendered width of the landscape hero art (matches customer.css .hm-hero / .hm-visual): 721-1023 px the
    /// art column is 67% of the row + 3% overlap + the right gutter (≈72vw); ≥1024 px 68% + 5% + gutter
    /// (≈74vw, capped at ≈880 px once the 1240 px container stops growing at 1288 px). ≤720 px the portrait
    /// hero-mobile.* is used; if it is absent the landscape art fills the ≈55% right column (≈52vw).
    /// </summary>
    public const string HeroSizes = "(min-width: 1288px) 880px, (min-width: 1024px) 74vw, (min-width: 721px) 72vw, 52vw";

    /// <summary>"a.jpg 768w, b.jpg 1536w" from whichever desktop hero files exist.</summary>
    public static string? Srcset(string? tablet, string? desktop)
    {
        var parts = new List<string>();
        if (tablet is not null) parts.Add($"{tablet} 768w");
        if (desktop is not null) parts.Add($"{desktop} 1536w");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime CheckedUtc, bool Exists)> AssetCache = new();
    private string? AssetUrl(string relative)
    {
        var root = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        var now = DateTime.UtcNow;
        if (!AssetCache.TryGetValue(full, out var hit) || (now - hit.CheckedUtc) > TimeSpan.FromMinutes(1))
        {
            hit = (now, File.Exists(full));
            AssetCache[full] = hit;
        }
        return hit.Exists ? "/" + relative : null;
    }

    private static readonly TimeSpan SettingsTtl = TimeSpan.FromMinutes(15);

    /// <summary>Public (non-secret) settings keys the storefront may display.</summary>
    public static readonly string[] PublicKeys =
    {
        "business.name", "business.motto", "business.email", "business.phone",
        "business.instagram", "business.tiktok", "business.address", "business.pickup_address",
        "business.whatsapp", "business.currency", "delivery.default_fee",
        "business.stat_projects", "business.stat_brands", "business.stat_services", "business.stat_turnaround",
        ServiceFeeSettings.Key
    };

    private Dictionary<string, string>? _business;
    private IReadOnlyList<MajorCategoryDto>? _majors;

    public async Task<Dictionary<string, string>> GetBusinessAsync(CancellationToken ct)
    {
        if (_business is not null) return _business;
        var all = await cache.GetOrCreateAsync(CacheKeys.SettingsAll,
            token => db.ApplicationSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, token),
            SettingsTtl, ct);
        _business = PublicKeys.Where(all.ContainsKey).ToDictionary(k => k, k => all[k]);
        return _business;
    }

    public async Task<IReadOnlyList<MajorCategoryDto>> GetMajorCategoriesAsync(CancellationToken ct)
        => _majors ??= await sender.Send(new GetMajorCategoriesQuery(), ct);
}
