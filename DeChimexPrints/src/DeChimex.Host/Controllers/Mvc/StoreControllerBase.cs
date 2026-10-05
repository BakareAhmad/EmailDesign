using DeChimex.Application.Common;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Contracts.Categories;
using DeChimex.Persistence.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>Base for public storefront controllers: loads whitelisted business info
/// (name, motto, contact, socials, address) and active categories for the shared nav/footer.</summary>
public abstract class StoreControllerBase : MvcControllerBase
{
    private static readonly TimeSpan SettingsTtl = TimeSpan.FromMinutes(15);

    private static readonly string[] PublicKeys =
    {
        "business.name", "business.motto", "business.email", "business.phone",
        "business.instagram", "business.tiktok", "business.address", "business.pickup_address",
        "business.whatsapp", "business.currency", "delivery.default_fee",
        "business.stat_projects", "business.stat_brands", "business.stat_services", "business.stat_turnaround",
        // Shown at checkout (owner directive): the customer-borne service fee must price
        // from the same whitelisted snapshot the rest of the storefront reads.
        ServiceFeeSettings.Key
    };

    protected async Task<Dictionary<string, string>> LoadBusinessInfoAsync()
    {
        var db = HttpContext.RequestServices.GetRequiredService<DeChimexDbContext>();
        var cache = HttpContext.RequestServices.GetRequiredService<ICacheService>();

        var all = await cache.GetOrCreateAsync(CacheKeys.SettingsAll,
            token => db.ApplicationSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, token),
            SettingsTtl, HttpContext.RequestAborted);

        return PublicKeys.Where(all.ContainsKey).ToDictionary(k => k, k => all[k]);
    }

    protected async Task<IReadOnlyList<CategoryDto>> LoadNavCategoriesAsync()
    {
        ViewBag.NavMajorCategories = await Sender.Send(new DeChimex.Application.Features.Categories.GetMajorCategoriesQuery());
        var categoryQueries = HttpContext.RequestServices.GetRequiredService<ICategoryQueries>();
        return await categoryQueries.GetAllAsync(activeOnly: true, HttpContext.RequestAborted);
    }

    protected string Business(Dictionary<string, string> info, string key, string fallback = "")
        => info.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
}
