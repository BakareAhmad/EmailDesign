using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Infrastructure.Persistence;
using DeChimex.Shared.Constants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>Developer access model: seeding, authentication, permission isolation, live revocation.</summary>
public class DevPortalAccessTests : IClassFixture<DevPortalFactory>
{
    private readonly DevPortalFactory _f;
    public DevPortalAccessTests(DevPortalFactory f) => _f = f;

    public static readonly string[] DevPages =
    {
        "/dev", "/dev/health", "/dev/performance", "/dev/jobs", "/dev/database", "/dev/configuration",
        "/dev/logs", "/dev/errors", "/dev/integrations", "/dev/payments", "/dev/email",
        "/dev/service-fees", "/dev/service-fees/ledger", "/dev/maintenance", "/dev/account/password"
    };

    [Fact]
    public async Task Anonymous_users_are_sent_to_login_for_every_dev_page_and_the_hub()
    {
        var c = _f.Browser();
        foreach (var p in DevPages.Append("/dev/api/status"))
        {
            var r = await c.GetAsync(p);
            r.StatusCode.Should().Be(HttpStatusCode.Redirect, p);
            r.Headers.Location!.ToString().Should().Contain("/Account/Login", p);
        }
        (await c.PostAsync("/hubs/dev/negotiate?negotiateVersion=1", null)).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Seeded_developer_signs_in_lands_on_the_portal_and_every_page_renders()
    {
        var c = _f.Browser();
        var login = await c.GetAsync("/Account/Login");
        var post = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = DevPortalFactory.DevEmail, ["Password"] = DevPortalFactory.DevPassword,
            ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await login.Content.ReadAsStringAsync())
        }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Be("/dev", "a developer-only account lands on the Developer Portal");

        foreach (var p in DevPages)
        {
            var r = await c.GetAsync(p);
            r.StatusCode.Should().Be(HttpStatusCode.OK, p);
            r.Headers.CacheControl?.NoStore.Should().BeTrue($"{p} must not be cached");
        }
    }

    [Fact]
    public async Task Developer_is_seeded_with_a_hashed_password_the_developer_role_only_and_no_business_permissions()
    {
        var (hash, roles, perms) = await _f.WithDbAsync(async db =>
        {
            var u = await db.Users.Include(x => x.Roles).SingleAsync(x => x.Email.Value == DevPortalFactory.DevEmail);
            var roleIds = u.Roles.Select(r => r.RoleId).ToList();
            var roleEntities = await db.Roles.Include(r => r.Permissions).Where(r => roleIds.Contains(r.Id)).ToListAsync();
            var permIds = roleEntities.SelectMany(r => r.Permissions.Select(p => p.PermissionId)).ToList();
            var names = await db.Permissions.Where(p => permIds.Contains(p.Id)).Select(p => p.Name).ToListAsync();
            return (u.PasswordHash, roleEntities.Select(r => r.Name).ToList(), names);
        });
        hash.Should().NotBe(DevPortalFactory.DevPassword).And.NotContain(DevPortalFactory.DevPassword);
        roles.Should().BeEquivalentTo(new[] { RoleNames.Developer });
        perms.Should().BeEquivalentTo(Permissions.Developer.All());
        perms.Should().NotContain(p => !p.StartsWith("dev."), "the developer gets no business permission (no refunds, pricing, customers, users, settings)");
    }

    [Fact]
    public async Task Reseeding_is_idempotent_and_never_hijacks_an_existing_account()
    {
        using (var scope = _f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        (await _f.WithDbAsync(db => db.Users.CountAsync(u => u.Email.Value == DevPortalFactory.DevEmail))).Should().Be(1);
        (await _f.WithDbAsync(db => db.Roles.CountAsync(r => r.NormalizedName == "DEVELOPER"))).Should().Be(1);
        (await _f.WithDbAsync(db => db.Permissions.CountAsync(p => p.Name.StartsWith("dev.")))).Should().Be(Permissions.Developer.All().Count);
        (await _f.DeveloperAsync()).Should().NotBeNull("the password was not rewritten by the re-seed");
    }

    [Theory]
    [InlineData("/admin/dashboard")]
    [InlineData("/admin/users")]
    [InlineData("/admin/users/roles")]
    [InlineData("/admin/settings")]
    [InlineData("/admin/customers")]
    [InlineData("/admin/orders")]
    [InlineData("/admin/products")]
    [InlineData("/portal")]
    public async Task Developer_cannot_reach_business_screens(string path)
    {
        var c = await _f.DeveloperAsync();
        DevPortalFactory.IsDenied(await c.GetAsync(path)).Should().BeTrue(path);
    }

    [Fact]
    public async Task Developer_cannot_refund_change_settings_or_assign_roles_even_by_posting_directly()
    {
        var c = await _f.DeveloperAsync();
        var token = TestSeed.ExtractAntiforgeryToken(await (await c.GetAsync("/dev/maintenance")).Content.ReadAsStringAsync());
        foreach (var url in new[] { "/admin/settings/service-fee", "/admin/settings", $"/admin/orders/{Guid.NewGuid()}/refund" })
        {
            var r = await c.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
            { ["newValue"] = "0", ["__RequestVerificationToken"] = token }));
            DevPortalFactory.IsDenied(r).Should().BeTrue(url);
        }
    }

    [Fact]
    public async Task Owner_customer_and_staff_do_not_get_the_developer_portal_by_default()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_f.Services, _f.Browser);
        DevPortalFactory.IsDenied(await owner.GetAsync("/dev")).Should().BeTrue("Owner is not a developer unless explicitly granted");
        var adminHtml = await (await owner.GetAsync("/admin/dashboard")).Content.ReadAsStringAsync();
        adminHtml.Should().NotContain("href=\"/dev\"", "the Technical link only shows with dev.portal.view");

        var email = $"cust.{Guid.NewGuid():N}@yopmail.com";
        await TestSeed.RegisterCustomerAsync(_f.Services, email);
        await TestSeed.VerifyCustomerAsync(_f.Services, email);
        var cust = _f.Browser();
        (await TestSeed.LoginMvcAsync(cust, email, TestSeed.DefaultPassword)).Should().BeTrue();
        DevPortalFactory.IsDenied(await cust.GetAsync("/dev")).Should().BeTrue();
        DevPortalFactory.IsDenied(await cust.GetAsync("/dev/logs")).Should().BeTrue();
        (await (await cust.GetAsync("/portal")).Content.ReadAsStringAsync()).Should().NotContain("Developer Portal");

        var staffEmail = $"sales.{Guid.NewGuid():N}@dechimex.test";
        await TestSeed.CreateStaffUserAsync(_f.Services, staffEmail, TestSeed.DefaultPassword, RoleNames.SalesStaff);
        var staff = _f.Browser();
        (await TestSeed.LoginMvcAsync(staff, staffEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        DevPortalFactory.IsDenied(await staff.GetAsync("/dev")).Should().BeTrue();
        DevPortalFactory.IsDenied(await staff.GetAsync("/dev/service-fees")).Should().BeTrue();
    }

    [Fact]
    public async Task Owner_explicitly_granted_the_developer_role_gets_both_portals()
    {
        var email = $"mgr.{Guid.NewGuid():N}@dechimex.test";
        await TestSeed.CreateStaffUserAsync(_f.Services, email, TestSeed.DefaultPassword, RoleNames.Manager);
        await _f.WithDbAsync(async db =>
        {
            var u = await db.Users.Include(x => x.Roles).SingleAsync(x => x.Email.Value == email);
            u.AssignRole((await db.Roles.SingleAsync(r => r.NormalizedName == "DEVELOPER")).Id);
            return await db.SaveChangesAsync();
        });
        var c = _f.Browser();
        (await TestSeed.LoginMvcAsync(c, email, TestSeed.DefaultPassword)).Should().BeTrue();
        (await c.GetAsync("/dev")).StatusCode.Should().Be(HttpStatusCode.OK);
        var admin = await c.GetAsync("/admin/dashboard");
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Content.ReadAsStringAsync()).Should().Contain("href=\"/dev\"");
    }

    [Fact]
    public async Task Role_checklist_never_offers_or_grants_developer_permissions_and_preserves_them_on_save()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_f.Services, _f.Browser);
        var devPermIds = await _f.WithDbAsync(db => db.Permissions.Where(p => p.Name.StartsWith("dev.")).Select(p => p.Id.ToString()).ToListAsync());

        foreach (var roleName in new[] { "Manager", "Developer" })
        {
            var link = await PermissionsLinkAsync(owner, roleName);
            var page = await (await owner.GetAsync(link)).Content.ReadAsStringAsync();
            page.Should().NotContain("dev.portal.view", "developer permissions are not on the business checklist");
            var ids = Regex.Matches(page, @"name=""PermissionIds"" value=""([0-9a-fA-F-]{36})"" checked").Select(m => m.Groups[1].Value);
            // Tamper: try to add every dev permission to Manager, and submit an empty set for Developer.
            var submit = roleName == "Manager" ? ids.Concat(devPermIds) : Enumerable.Empty<string>();
            var form = new FormUrlEncodedContent(submit.Select(id => new KeyValuePair<string, string>("PermissionIds", id))
                .Append(new("__RequestVerificationToken", TestSeed.ExtractAntiforgeryToken(page))));
            (await owner.PostAsync(link, form)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        var (mgrDev, devDev) = await _f.WithDbAsync(async db =>
        {
            var devIds = await db.Permissions.Where(p => p.Name.StartsWith("dev.")).Select(p => p.Id).ToListAsync();
            var mgr = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.NormalizedName == "MANAGER");
            var dev = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.NormalizedName == "DEVELOPER");
            return (mgr.Permissions.Count(p => devIds.Contains(p.PermissionId)), dev.Permissions.Count(p => devIds.Contains(p.PermissionId)));
        });
        mgrDev.Should().Be(0, "submitted dev.* ids are ignored: the checklist cannot grant developer access");
        devDev.Should().Be(Permissions.Developer.All().Count, "saving the checklist preserves the role's existing dev.* permissions");
        (await _f.DeveloperAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task Removing_the_developer_role_takes_effect_on_the_very_next_request()
    {
        var email = $"dev2.{Guid.NewGuid():N}@dechimex.test";
        await TestSeed.CreateStaffUserAsync(_f.Services, email, TestSeed.DefaultPassword, RoleNames.Developer);
        var c = _f.Browser();
        (await TestSeed.LoginMvcAsync(c, email, TestSeed.DefaultPassword)).Should().BeTrue();
        (await c.GetAsync("/dev/logs")).StatusCode.Should().Be(HttpStatusCode.OK);

        await _f.WithDbAsync(async db =>
        {
            var u = await db.Users.Include(x => x.Roles).SingleAsync(x => x.Email.Value == email);
            u.SetRoles(Array.Empty<Guid>());
            return await db.SaveChangesAsync();
        });
        DevPortalFactory.IsDenied(await c.GetAsync("/dev/logs")).Should().BeTrue("the portal re-checks permissions against the database on every request");
        DevPortalFactory.IsDenied(await c.GetAsync("/dev")).Should().BeTrue();
    }

    [Fact]
    public async Task Individual_dev_permissions_gate_their_sections_and_the_navigation()
    {
        // A custom technical role with portal access only.
        var roleName = "Observer" + Guid.NewGuid().ToString("N")[..6];
        var email = $"obs.{Guid.NewGuid():N}@dechimex.test";
        await _f.WithDbAsync(async db =>
        {
            var role = DeChimex.Domain.Entities.Role.Create(roleName);
            role.SetPermissions(new[] { (await db.Permissions.SingleAsync(p => p.Name == Permissions.Developer.PortalView)).Id });
            db.Roles.Add(role);
            return await db.SaveChangesAsync();
        });
        await TestSeed.CreateStaffUserAsync(_f.Services, email, TestSeed.DefaultPassword, roleName);
        var c = _f.Browser();
        (await TestSeed.LoginMvcAsync(c, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var dash = await c.GetAsync("/dev");
        dash.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await dash.Content.ReadAsStringAsync();
        html.Should().NotContain("href=\"/dev/logs\"").And.NotContain("href=\"/dev/service-fees\"").And.NotContain("href=\"/dev/maintenance\"");
        foreach (var p in new[] { "/dev/logs", "/dev/errors", "/dev/payments", "/dev/email", "/dev/integrations", "/dev/service-fees", "/dev/service-fees/export", "/dev/maintenance" })
            DevPortalFactory.IsDenied(await c.GetAsync(p)).Should().BeTrue(p);
        var token = TestSeed.ExtractAntiforgeryToken(await (await c.GetAsync("/dev/account/password")).Content.ReadAsStringAsync());
        DevPortalFactory.IsDenied(await c.PostAsync("/dev/maintenance/clear-cache", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["confirm"] = "yes", ["__RequestVerificationToken"] = token }))).Should().BeTrue("maintenance needs dev.maintenance.run");
    }

    [Fact]
    public async Task Forced_password_change_blocks_the_portal_until_completed()
    {
        var email = $"dev3.{Guid.NewGuid():N}@dechimex.test";
        await TestSeed.CreateStaffUserAsync(_f.Services, email, TestSeed.DefaultPassword, RoleNames.Developer);
        await _f.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(x => x.Email.Value == email)).SetMustChangePassword();
            return await db.SaveChangesAsync();
        });
        var c = _f.Browser();
        var login = await c.GetAsync("/Account/Login");
        var post = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = TestSeed.DefaultPassword,
            ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await login.Content.ReadAsStringAsync())
        }));
        post.Headers.Location!.ToString().Should().StartWith("/dev/account/password");
        var blocked = await c.GetAsync("/dev/logs");
        blocked.StatusCode.Should().Be(HttpStatusCode.Redirect);
        blocked.Headers.Location!.ToString().Should().Contain("/dev/account/password");

        // Validation: mismatch and wrong current password are rejected.
        var bad = await DevPortalFactory.PostFormAsync(c, "/dev/account/password", "/dev/account/password", new()
        { ["CurrentPassword"] = TestSeed.DefaultPassword, ["NewPassword"] = "NewDev!Pass77", ["ConfirmPassword"] = "Different!77" });
        bad.StatusCode.Should().Be(HttpStatusCode.OK);
        var wrong = await DevPortalFactory.PostFormAsync(c, "/dev/account/password", "/dev/account/password", new()
        { ["CurrentPassword"] = "WrongPass!1", ["NewPassword"] = "NewDev!Pass77", ["ConfirmPassword"] = "NewDev!Pass77" });
        wrong.StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync("/dev/logs")).StatusCode.Should().Be(HttpStatusCode.Redirect, "still blocked after failed attempts");

        var ok = await DevPortalFactory.PostFormAsync(c, "/dev/account/password", "/dev/account/password", new()
        { ["CurrentPassword"] = TestSeed.DefaultPassword, ["NewPassword"] = "NewDev!Pass77", ["ConfirmPassword"] = "NewDev!Pass77" });
        ok.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await c.GetAsync("/dev/logs")).StatusCode.Should().Be(HttpStatusCode.OK);
        var fresh = _f.Browser();
        (await TestSeed.LoginMvcAsync(fresh, email, "NewDev!Pass77")).Should().BeTrue();
    }

    [Fact]
    public async Task Failed_sign_in_and_access_denied_are_recorded_as_security_events_with_masked_email()
    {
        var c = _f.Browser();
        (await TestSeed.LoginMvcAsync(c, DevPortalFactory.DevEmail, "totally-wrong")).Should().BeFalse();
        var dev = await _f.DeveloperAsync();
        await dev.GetAsync("/admin/settings");
        await _f.FlushDiagnosticsAsync();
        var events = await _f.WithDbAsync(db => db.TechnicalEvents.AsNoTracking().Where(e => e.Category == "Security").ToListAsync());
        events.Should().Contain(e => e.EventType == "AuthenticationFailure");
        events.Should().Contain(e => e.EventType == "AuthorizationFailure" && e.Route!.Contains("/admin/settings"));
        events.Should().NotContain(e => e.Message.Contains(DevPortalFactory.DevEmail), "emails are masked in technical events");
        events.Should().NotContain(e => (e.Properties ?? "").Contains("totally-wrong"), "passwords are never logged");
    }

    private static async Task<string> PermissionsLinkAsync(HttpClient client, string roleName)
    {
        var html = await (await client.GetAsync("/admin/users/roles")).Content.ReadAsStringAsync();
        var row = Regex.Split(html, "<tr").First(part => part.Contains($">{roleName}</td>"));
        return Regex.Match(row, "href=\"(/admin/users/roles/[0-9a-fA-F-]{36}/permissions)\"").Groups[1].Value;
    }
}
