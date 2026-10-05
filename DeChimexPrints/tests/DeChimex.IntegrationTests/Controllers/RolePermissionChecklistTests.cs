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
/// Verifies the admin role-permission checklist end to end (role-permission checklist
/// requirement, 2026-08-28): an admin can edit the checklist of any role, including the
/// seeded system roles, the change applies immediately, and it survives another seeder
/// run (simulated application restart). A deleted role is re-created with its canonical map.
/// </summary>
public class RolePermissionChecklistTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RolePermissionChecklistTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed record Perm(string Name, string Id, bool Checked);

    [Fact]
    public async Task System_role_checklist_edits_apply_immediately_and_survive_reseed()
    {
        var client = await LoginAsOwnerAsync();

        // 1. Manager starts with the canonical map: 32 of 38 (all except users.* and the owner-default activity.view permission;
        //    marketing.view/manage were added to the registry and Manager\u2019s map on 2026-09-08).
        var permLink = await PermissionsLinkForRoleAsync(client, "Manager");
        var page = await (await client.GetAsync(permLink)).Content.ReadAsStringAsync();
        var perms = ParsePermissions(page);
        perms.Count.Should().Be(38, "the full permission registry is offered on every checklist");
        perms.Count(p => p.Checked).Should().Be(32);
        perms.Where(p => !p.Checked).Select(p => p.Name)
            .Should().BeEquivalentTo("users.view", "users.create", "users.update", "users.disable", "users.assignroles", "activity.view");

        // 2. The admin unchecks settings.manage and saves through the real UI.
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var keep = perms.Where(p => p.Checked && p.Name != "settings.manage").Select(p => p.Id).ToList();
        var form = new FormUrlEncodedContent(keep
            .Select(id => new KeyValuePair<string, string>("PermissionIds", id))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token)));
        var save = await client.PostAsync(permLink, form);
        save.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // 3. The edit applied immediately: 31 granted, settings.manage gone.
        page = await (await client.GetAsync(permLink)).Content.ReadAsStringAsync();
        perms = ParsePermissions(page);
        perms.Count(p => p.Checked).Should().Be(31);
        perms.Should().NotContain(p => p.Checked && p.Name == "settings.manage");

        // 4. Simulate an application restart: run the seeder again against the same database.
        using (var scope = _factory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
        }

        // 5. The admin edit survived the restart.
        page = await (await client.GetAsync(permLink)).Content.ReadAsStringAsync();
        perms = ParsePermissions(page);
        perms.Count(p => p.Checked).Should().Be(31, "the seeder must not re-sync permissions of existing roles");
        perms.Should().NotContain(p => p.Checked && p.Name == "settings.manage");

        // 6. Untouched system roles still hold their canonical maps.
        (await CheckedCountForRoleAsync(client, "Owner")).Should().Be(38);
        (await CheckedCountForRoleAsync(client, "SalesStaff")).Should().Be(9);
        (await CheckedCountForRoleAsync(client, "InventoryStaff")).Should().Be(8, "canonical map now includes messages.view (owner directive 2026-09-08)");
        (await CheckedCountForRoleAsync(client, "Designer")).Should().Be(5, "canonical map now includes messages.view (owner directive 2026-09-08)");
        (await CheckedCountForRoleAsync(client, "Customer")).Should().Be(1);
    }

    [Fact]
    public async Task A_role_deleted_out_of_band_is_recreated_with_its_canonical_map()
    {
        var client = await LoginAsOwnerAsync();
        var permLink = await PermissionsLinkForRoleAsync(client, "Designer");
        var checkedBefore = ParsePermissions(await (await client.GetAsync(permLink)).Content.ReadAsStringAsync());
        checkedBefore.Count(p => p.Checked).Should().Be(5);

        // Delete Designer directly through the database (bypassing the role-delete guard,
        // which correctly blocks deleting system roles from the UI).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Designer");
            role.Should().NotBeNull();
            db.Roles.Remove(role!);
            await db.SaveChangesAsync();
        }

        // The next seeder run (application restart) must re-create the role.
        using (var scope = _factory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
        }

        var rolesPage = await (await client.GetAsync("/admin/users/roles")).Content.ReadAsStringAsync();
        var permLinkAfter = await PermissionsLinkForRoleAsync(client, "Designer");
        var checkedAfter = ParsePermissions(await (await client.GetAsync(permLinkAfter)).Content.ReadAsStringAsync());
        checkedAfter.Select(p => p.Name).Should().BeEquivalentTo(checkedBefore.Select(p => p.Name));
        checkedAfter.Count(p => p.Checked).Should().Be(5, "re-created roles get the canonical permission map (Designer now includes messages.view)");
    }

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // The seeded owner is forced to change the bootstrap password on first login.
        // An earlier test in this class may already have done that, so fall back to the
        // known changed password when the bootstrap one no longer works.
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
            await CompleteForcedPasswordChangeAsync(client, password);
        return true;
    }

    private static async Task CompleteForcedPasswordChangeAsync(HttpClient client, string currentPassword)
    {
        var html = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPassword"] = currentPassword,
            ["NewPassword"] = "Owner!Pass9",
            ["ConfirmPassword"] = "Owner!Pass9",
            ["__RequestVerificationToken"] = token
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
    private static async Task<string> PermissionsLinkForRoleAsync(HttpClient client, string roleName)
    {
        var html = await (await client.GetAsync("/admin/users/roles")).Content.ReadAsStringAsync();
        var row = Regex.Split(html, "<tr").FirstOrDefault(part => part.Contains($">{roleName}</td>"));
        row.Should().NotBeNull($"the roles list should contain a row for {roleName}");
        return Regex.Match(row!, "href=\"(/admin/users/roles/[0-9a-fA-F-]{36}/permissions)\"").Groups[1].Value;
    }

    private static async Task<int> CheckedCountForRoleAsync(HttpClient client, string roleName) =>
        ParsePermissions(await (await client.GetAsync(await PermissionsLinkForRoleAsync(client, roleName))).Content.ReadAsStringAsync())
            .Count(p => p.Checked);

    private static List<Perm> ParsePermissions(string html)
    {
        var rx = new Regex(@"name=""PermissionIds"" value=""([0-9a-fA-F-]{36})""( checked)?\s*/>\s*([a-z]+\.[a-z]+)");
        return rx.Matches(html)
            .Select(m => new Perm(m.Groups[3].Value, m.Groups[1].Value, m.Groups[2].Success))
            .ToList();
    }
}
