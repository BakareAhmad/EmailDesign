using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Infrastructure.Persistence;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Regression coverage for the missing Marketing admin menu (owner report 2026-09-08:
/// "can't see marketing menu despite allowing its permission"). The /admin/marketing
/// controller and views existed, but _AdminLayout.cshtml never rendered a sidebar link,
/// so the feature was unreachable from the UI no matter which permissions a role held.
/// Also covers the owner directive that marketing is allowed for Owner and Manager by
/// DEFAULT: the seeder backfills the two marketing permissions on pre-existing Owner /
/// Manager roles (additive only — other permission removals made through the checklist
/// must still survive restarts).
/// </summary>
public class MarketingMenuTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MarketingMenuTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_sidebar_shows_the_marketing_link_for_owner()
    {
        var client = await LoginAsOwnerAsync();

        var dashboard = await (await client.GetAsync("/admin/dashboard")).Content.ReadAsStringAsync();

        dashboard.Should().Contain("href=\"/admin/marketing\"",
            "the admin sidebar must expose the Marketing section to roles holding marketing.view");
        dashboard.Should().Contain("<span>Marketing</span>");

        // The link must actually lead to the marketing broadcasts page.
        var marketing = await client.GetAsync("/admin/marketing");
        marketing.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Seeder_backfills_marketing_for_owner_and_manager_without_restoring_other_removals()
    {
        // Simulate a pre-marketing database: strip marketing.* from both roles, and also
        // remove one unrelated permission from each (as an admin checklist edit would) to
        // prove the backfill is additive-only.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var perms = await db.Permissions.ToDictionaryAsync(p => p.Name, p => p.Id);

            await StripAsync(db, "Owner", new[] { "marketing.view", "marketing.manage", "reports.export" }, perms);
            await StripAsync(db, "Manager", new[] { "marketing.view", "marketing.manage", "settings.manage" }, perms);
            await db.SaveChangesAsync();
        }

        // Restart simulation: run the seeder against the same database.
        using (var scope = _factory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var perms = await db.Permissions.ToDictionaryAsync(p => p.Name, p => p.Id);

            var owner = await GrantedAsync(db, "Owner", perms);
            owner.Should().Contain(new[] { "marketing.view", "marketing.manage" },
                "owner directive 2026-09-08: marketing is allowed for Owner by default");
            owner.Should().NotContain("reports.export",
                "the backfill must ONLY add marketing permissions, never restore other removals");

            var manager = await GrantedAsync(db, "Manager", perms);
            manager.Should().Contain(new[] { "marketing.view", "marketing.manage" },
                "owner directive 2026-09-08: marketing is allowed for Manager by default");
            manager.Should().NotContain("settings.manage",
                "the backfill must ONLY add marketing permissions, never restore other removals");
        }
    }

    private static async Task StripAsync(DeChimexDbContext db, string roleName, string[] permissionNames,
        IReadOnlyDictionary<string, Guid> perms)
    {
        var role = await db.Roles.Include(r => r.Permissions).FirstAsync(r => r.Name == roleName);
        var ids = permissionNames.Select(n => perms[n]).ToHashSet();
        foreach (var rp in role.Permissions.Where(rp => ids.Contains(rp.PermissionId)).ToList())
            db.Remove(rp);
    }

    private static async Task<HashSet<string>> GrantedAsync(DeChimexDbContext db, string roleName,
        IReadOnlyDictionary<string, Guid> perms)
    {
        var byId = perms.ToDictionary(kv => kv.Value, kv => kv.Key);
        var role = await db.Roles.Include(r => r.Permissions).FirstAsync(r => r.Name == roleName);
        return role.Permissions.Select(rp => byId[rp.PermissionId]).ToHashSet();
    }

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // The seeded owner is forced to change the bootstrap password on first login.
        var ok = await TryOwnerLoginAsync(client, "ChangeMe123!");
        if (!ok)
            ok = await TryOwnerLoginAsync(client, "Owner!Pass9");
        ok.Should().BeTrue("owner login should succeed with the bootstrap or the changed password");
        return client;
    }

    private async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "owner@dechimexprinthub.com",
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        if (response.StatusCode != HttpStatusCode.Redirect)
            return false;

        var location = response.Headers.Location?.ToString() ?? "";
        if (location.Contains("/admin/security/password"))
        {
            var pwHtml = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
            var pwToken = Regex.Match(pwHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var pwResponse = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["CurrentPassword"] = password,
                ["NewPassword"] = "Owner!Pass9",
                ["ConfirmPassword"] = "Owner!Pass9",
                ["__RequestVerificationToken"] = pwToken
            }));
            pwResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }
        return true;
    }
}
