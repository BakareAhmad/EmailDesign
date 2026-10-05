using System.Net;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Staff creation must fail gracefully: command-level validation (password
/// complexity, at-least-one-role) is stricter than the form annotations, and
/// those failures used to escape AdminUsersController.Create as an unhandled
/// ValidationAppException (HTTP 500). They must re-render the form with
/// field-level messages instead (AI_HANDOFF §80).
/// </summary>
public class StaffCreationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public StaffCreationTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<Guid> SalesStaffRoleIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        return (await db.Roles.FirstAsync(r => r.Name == RoleNames.SalesStaff)).Id;
    }

    private static async Task<string> AntiforgeryAsync(HttpClient browser)
    {
        var form = await browser.GetAsync("/admin/users/create");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        return TestSeed.ExtractAntiforgeryToken(await form.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateStaff_WeakPassword_RerendersFormWithPasswordError()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var roleId = await SalesStaffRoleIdAsync();
        var email = $"staff-weak-{Guid.NewGuid():N}@example.com";

        var attempt = await owner.PostAsync("/admin/users/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "Jummy",
                ["LastName"] = "Weak",
                ["Email"] = email,
                ["Username"] = "jummyweak",
                ["Password"] = "password", // 8 chars but no upper/digit: passes MVC, fails command
                ["RoleIds"] = roleId.ToString(),
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner)
            }));

        attempt.StatusCode.Should().Be(HttpStatusCode.OK, "a weak password must re-render, not 500");
        var html = await attempt.Content.ReadAsStringAsync();
        html.Should().Contain("New staff user");
        // asp-validation-for renders the first error per field: "password" fails the
        // uppercase rule first.
        html.Should().Contain("Password must contain an uppercase letter.");

        // A password failing only the digit rule surfaces that message instead.
        var attempt2 = await owner.PostAsync("/admin/users/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "Jummy",
                ["LastName"] = "Weak",
                ["Email"] = email,
                ["Username"] = "jummyweak",
                ["Password"] = "Password",
                ["RoleIds"] = roleId.ToString(),
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner)
            }));
        attempt2.StatusCode.Should().Be(HttpStatusCode.OK);
        (await attempt2.Content.ReadAsStringAsync()).Should().Contain("Password must contain a digit.");
    }

    [Fact]
    public async Task CreateStaff_NoRole_RerendersFormWithRoleError()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var email = $"staff-norole-{Guid.NewGuid():N}@example.com";

        var attempt = await owner.PostAsync("/admin/users/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "Jummy",
                ["LastName"] = "Norole",
                ["Email"] = email,
                ["Username"] = "jummynorole",
                ["Password"] = "StrongPass1",
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner)
            }));

        attempt.StatusCode.Should().Be(HttpStatusCode.OK, "a missing role must re-render, not 500");
        var html = await attempt.Content.ReadAsStringAsync();
        html.Should().Contain("Select at least one role.");
    }

    [Fact]
    public async Task CreateStaff_ValidInput_CreatesUserWithRole()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var roleId = await SalesStaffRoleIdAsync();
        var email = $"staff-ok-{Guid.NewGuid():N}@example.com";

        var created = await owner.PostAsync("/admin/users/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "Ada",
                ["LastName"] = "Sales",
                ["Email"] = email,
                ["Username"] = "adasales",
                ["Password"] = "StrongPass1",
                ["RoleIds"] = roleId.ToString(),
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner)
            }));

        created.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var user = await db.Users.Include(u => u.Roles)
                .FirstOrDefaultAsync(u => u.Email.Value == email);
            user.Should().NotBeNull();
            user!.Roles.Select(ur => ur.RoleId).Should().Contain(roleId);
            user.Username!.Value.Should().Be("adasales");
        }
        var list = await owner.GetAsync("/admin/users?search=staff-ok-");
        (await list.Content.ReadAsStringAsync()).Should().Contain(email);
    }
}
