using System.Net;
using System.Text.Json;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Staff usernames end-to-end over HTTP: uniqueness/invalid validation on the
/// admin create form, the username change page (with chat-history propagation),
/// and the customer-facing guarantee — staff chat identity is the username
/// ONLY (state feed, quotes, timeline, notifications carry no staff name/email).
/// </summary>
public class StaffUsernameTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public StaffUsernameTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static string UniqueUsername(string prefix)
        => $"{prefix}{Guid.NewGuid():N}"[..30];

    private async Task<Guid> SalesStaffRoleIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        return (await db.Roles.FirstAsync(r => r.Name == RoleNames.SalesStaff)).Id;
    }

    private static async Task<string> AntiforgeryAsync(HttpClient browser, string formUrl)
    {
        var form = await browser.GetAsync(formUrl);
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        return TestSeed.ExtractAntiforgeryToken(await form.Content.ReadAsStringAsync());
    }

    private async Task<(HttpClient Customer, Guid OrderId)> SeedWorkspaceAsync(string tag)
    {
        var services = _factory.Services;
        var email = $"staffuser-{tag}-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email);
        await TestSeed.VerifyCustomerAsync(services, email);
        var productId = await SalesAssignmentTests.SeedCustomizableProductAsync(services, $"UChat {tag}?");
        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SalesAssignmentTests.SubmitRequestAsync(browser, productId, "Blue");
        return (browser, orderId);
    }

    private async Task<HttpClient> LoginStaffAsync(string firstName, string lastName, string username)
    {
        var email = $"chatstaff-{Guid.NewGuid():N}@example.com";
        await TestSeed.CreateStaffUserAsync(_factory.Services, email, TestSeed.DefaultPassword,
            RoleNames.SalesStaff, firstName, lastName, username);
        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();
        return browser;
    }

    [Fact]
    public async Task CreateStaff_DuplicateUsername_RerendersFormWithTakenError()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var roleId = await SalesStaffRoleIdAsync();
        var username = UniqueUsername("dupe");

        var first = await owner.PostAsync("/admin/users/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "First",
                ["LastName"] = "Staffer",
                ["Email"] = $"uname-first-{Guid.NewGuid():N}@example.com",
                ["Username"] = username,
                ["Password"] = "StrongPass1",
                ["RoleIds"] = roleId.ToString(),
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner, "/admin/users/create")
            }));
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var second = await owner.PostAsync("/admin/users/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "Second",
                ["LastName"] = "Staffer",
                ["Email"] = $"uname-second-{Guid.NewGuid():N}@example.com",
                ["Username"] = username,
                ["Password"] = "StrongPass1",
                ["RoleIds"] = roleId.ToString(),
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner, "/admin/users/create")
            }));

        second.StatusCode.Should().Be(HttpStatusCode.OK, "a duplicate username must re-render, not 500");
        (await second.Content.ReadAsStringAsync()).Should().Contain("already taken");
    }

    [Fact]
    public async Task CreateStaff_ReservedUsername_RerendersFormWithReservedError()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var roleId = await SalesStaffRoleIdAsync();

        var attempt = await owner.PostAsync("/admin/users/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "Reserved",
                ["LastName"] = "Name",
                ["Email"] = $"uname-reserved-{Guid.NewGuid():N}@example.com",
                ["Username"] = "support",
                ["Password"] = "StrongPass1",
                ["RoleIds"] = roleId.ToString(),
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner, "/admin/users/create")
            }));

        attempt.StatusCode.Should().Be(HttpStatusCode.OK);
        (await attempt.Content.ReadAsStringAsync()).Should().Contain("reserved");
    }

    [Fact]
    public async Task CreateStaff_TooShortUsername_RerendersFormWithFieldError()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var roleId = await SalesStaffRoleIdAsync();

        var attempt = await owner.PostAsync("/admin/users/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "Short",
                ["LastName"] = "Name",
                ["Email"] = $"uname-short-{Guid.NewGuid():N}@example.com",
                ["Username"] = "ab",
                ["Password"] = "StrongPass1",
                ["RoleIds"] = roleId.ToString(),
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner, "/admin/users/create")
            }));

        attempt.StatusCode.Should().Be(HttpStatusCode.OK);
        (await attempt.Content.ReadAsStringAsync()).Should().Contain("between 3 and 30 characters");
    }

    [Fact]
    public async Task UsernameChange_RenamesUser_AndPropagatesToChatHistory()
    {
        var (customer, orderId) = await SeedWorkspaceAsync("rename");
        var oldUsername = UniqueUsername("oldname");
        var staff = await LoginStaffAsync("Rename", "Staffer", oldUsername);
        var portalWs = $"/portal/orders/{orderId}/workspace";
        var adminWs = $"/admin/orders/{orderId}/workspace";

        var sent = await staff.PostAsync($"{adminWs}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "Hi from the old name",
                ["__RequestVerificationToken"] = await AntiforgeryAsync(staff, adminWs)
            }));
        sent.StatusCode.Should().Be(HttpStatusCode.Redirect);

        Guid staffUserId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var user = await db.Users.FirstAsync(u => u.Username != null && u.Username.Value == oldUsername);
            staffUserId = user.Id;
            (await db.OrderMessages.FirstAsync(m => m.Body == "Hi from the old name"))
                .AuthorName.Should().Be(oldUsername);
        }

        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var newUsername = UniqueUsername("newname");
        var form = await owner.GetAsync($"/admin/users/{staffUserId}/username");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        (await form.Content.ReadAsStringAsync()).Should().Contain(oldUsername);

        var renamed = await owner.PostAsync($"/admin/users/{staffUserId}/username", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Username"] = newUsername,
                ["__RequestVerificationToken"] = await AntiforgeryAsync(owner, $"/admin/users/{staffUserId}/username")
            }));
        renamed.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Persisted state: the account AND its past messages carry the new name.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Users.FirstAsync(u => u.Id == staffUserId)).Username!.Value.Should().Be(newUsername);
            (await db.OrderMessages.FirstAsync(m => m.Body == "Hi from the old name"))
                .AuthorName.Should().Be(newUsername);
        }

        // The customer-facing read model follows: history shows the new username.
        var state = await customer.GetAsync($"{portalWs}/state");
        var json = await state.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var messages = doc.RootElement.GetProperty("workspace").GetProperty("messages");
        messages.EnumerateArray()
            .First(m => m.GetProperty("body").GetString() == "Hi from the old name")
            .GetProperty("authorName").GetString().Should().Be(newUsername);
        json.Should().NotContain(oldUsername);
    }

    [Fact]
    public async Task StaffSend_CustomerPayload_ShowsUsernameOnly()
    {
        var (customer, orderId) = await SeedWorkspaceAsync("identity");
        var username = UniqueUsername("shakur");
        var staff = await LoginStaffAsync("Addey", "Okaforson", username);
        var staffEmail = await StaffEmailAsync(username);
        var portalWs = $"/portal/orders/{orderId}/workspace";
        var adminWs = $"/admin/orders/{orderId}/workspace";

        // Customer asks first so the staff reply also exercises the quote path.
        var asked = await customer.PostAsync($"{portalWs}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "What ink do you use?",
                ["__RequestVerificationToken"] = await AntiforgeryAsync(customer, portalWs)
            }));
        asked.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var before = JsonDocument.Parse(await (await customer.GetAsync($"{portalWs}/state"))
            .Content.ReadAsStringAsync());
        var questionId = before.RootElement.GetProperty("workspace").GetProperty("messages")
            .EnumerateArray().First(m => m.GetProperty("body").GetString() == "What ink do you use?")
            .GetProperty("id").GetString()!;

        var answered = await staff.PostAsync($"{adminWs}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "Victory red, always.",
                ["replyToMessageId"] = questionId,
                ["__RequestVerificationToken"] = await AntiforgeryAsync(staff, adminWs)
            }));
        answered.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // State feed: the staff message + quote show the username; no staff
        // name or email anywhere in the customer payload (incl. timeline actors).
        var state = await customer.GetAsync($"{portalWs}/state");
        state.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await state.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var workspace = doc.RootElement.GetProperty("workspace");
        var reply = workspace.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("body").GetString() == "Victory red, always.");
        reply.GetProperty("authorName").GetString().Should().Be(username);
        json.Should().NotContain(staffEmail, "staff email must not reach the customer");
        json.Should().NotContain("Addey", "staff first name must not reach the customer");
        json.Should().NotContain("Okaforson", "staff last name must not reach the customer");
        foreach (var entry in workspace.GetProperty("timeline").EnumerateArray())
            entry.GetProperty("actorName").ValueKind.Should().Be(JsonValueKind.Null);

        // The rendered page agrees: one identity, the username.
        var page = await (await customer.GetAsync(portalWs)).Content.ReadAsStringAsync();
        page.Should().Contain(username);
        page.Should().NotContain(staffEmail);
        page.Should().NotContain("Okaforson");

        // Conversation notification: addressed by username, not email.
        var recent = await customer.GetAsync("/portal/notifications/recent");
        recent.StatusCode.Should().Be(HttpStatusCode.OK);
        var notes = await recent.Content.ReadAsStringAsync();
        notes.Should().Contain(username);
        notes.Should().NotContain(staffEmail);
    }

    private async Task<string> StaffEmailAsync(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        return (await db.Users.FirstAsync(u => u.Username != null && u.Username.Value == username))
            .Email.Value;
    }
}
