using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// 2026-08-29 feature: notifications are scoped per trigger to the staff who can act on it.
/// Owner/Manager see everything; SalesStaff sees order-side (NewOrder, PaymentReceived,
/// OrderCompleted); InventoryStaff sees stock-side (LowStock, ProcurementRequired);
/// Designer sees NewOrder only. Legacy null-permission rows stay visible to all staff.
/// The /api/v1/notifications JSON endpoint that this test originally exercised has been
/// removed with the superseded API surface; these tests now run over the MVC Admin
/// notifications list + mark-read + unread-count pages.
/// </summary>
public class NotificationScopingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public NotificationScopingTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> LoginOwnerMvcAsync()
        => await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);

    private async Task<HttpClient> LoginStaffMvcAsync(string email, string password = "Staff123!")
    {
        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, password)).Should().BeTrue();
        return browser;
    }

    private async Task<HttpClient> CreateStaffAndLoginAsync(string roleName, string email)
    {
        await TestSeed.CreateStaffUserAsync(_factory.Services, email, "Staff123!", roleName);
        return await LoginStaffMvcAsync(email);
    }

    private Task<Guid> SeedAsync(string title, string? requiredPermission, NotificationType type)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var n = Notification.Create(type, title, "scoped test message", link: "/admin/orders",
            requiredPermission: requiredPermission);
        db.Notifications.Add(n);
        return db.SaveChangesAsync().ContinueWith(_ => n.Id);
    }

    private async Task<IReadOnlyList<string>> FetchVisibleTitlesAsync(HttpClient browser, string run)
    {
        var resp = await browser.GetAsync("/admin/notifications");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        // The MVC Index page renders each notification title in a div. Match titles that are
        // exact-ish (contain the run token) and were not href-wrapped.
        return Regex.Matches(html, ">([^<]*)</div>")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(t => t.StartsWith(run, StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    [Fact]
    public async Task Owner_SeesEveryScopedNotificationType()
    {
        var run = "RUN-" + Guid.NewGuid().ToString("N")[..8];
        var browser = await LoginOwnerMvcAsync();

        var titles = new[]
        {
            $"{run}-NewOrder", $"{run}-Payment", $"{run}-Completed",
            $"{run}-LowStock", $"{run}-Procurement"
        };
        await SeedAsync(titles[0], "orders.view", NotificationType.NewOrder);
        await SeedAsync(titles[1], "orders.updatestatus", NotificationType.PaymentReceived);
        await SeedAsync(titles[2], "orders.updatestatus", NotificationType.OrderCompleted);
        await SeedAsync(titles[3], "inventory.view", NotificationType.LowStock);
        await SeedAsync(titles[4], "procurement.view", NotificationType.ProcurementRequired);

        (await FetchVisibleTitlesAsync(browser, run)).Should().BeEquivalentTo(titles);
    }

    [Fact]
    public async Task InventoryStaff_SeesStockSideOnly()
    {
        var run = "RUN-" + Guid.NewGuid().ToString("N")[..8];
        var email = $"inv-{run.ToLowerInvariant()}@example.com";
        var browser = await CreateStaffAndLoginAsync("InventoryStaff", email);

        await SeedAsync($"{run}-NewOrder", "orders.view", NotificationType.NewOrder);
        await SeedAsync($"{run}-Payment", "orders.updatestatus", NotificationType.PaymentReceived);
        await SeedAsync($"{run}-Completed", "orders.updatestatus", NotificationType.OrderCompleted);
        await SeedAsync($"{run}-LowStock", "inventory.view", NotificationType.LowStock);
        await SeedAsync($"{run}-Procurement", "procurement.view", NotificationType.ProcurementRequired);

        (await FetchVisibleTitlesAsync(browser, run))
            .Should().BeEquivalentTo(new[] { $"{run}-LowStock", $"{run}-Procurement" });
    }

    [Fact]
    public async Task SalesStaff_SeesOrderSideOnly()
    {
        var run = "RUN-" + Guid.NewGuid().ToString("N")[..8];
        var email = $"sales-{run.ToLowerInvariant()}@example.com";
        var browser = await CreateStaffAndLoginAsync("SalesStaff", email);

        await SeedAsync($"{run}-NewOrder", "orders.view", NotificationType.NewOrder);
        await SeedAsync($"{run}-Payment", "orders.updatestatus", NotificationType.PaymentReceived);
        await SeedAsync($"{run}-Completed", "orders.updatestatus", NotificationType.OrderCompleted);
        await SeedAsync($"{run}-LowStock", "inventory.view", NotificationType.LowStock);
        await SeedAsync($"{run}-Procurement", "procurement.view", NotificationType.ProcurementRequired);

        (await FetchVisibleTitlesAsync(browser, run))
            .Should().BeEquivalentTo(new[] { $"{run}-NewOrder", $"{run}-Payment", $"{run}-Completed" });
    }

    [Fact]
    public async Task Designer_SeesNewOrderOnly()
    {
        var run = "RUN-" + Guid.NewGuid().ToString("N")[..8];
        var email = $"designer-{run.ToLowerInvariant()}@example.com";
        var browser = await CreateStaffAndLoginAsync("Designer", email);

        await SeedAsync($"{run}-NewOrder", "orders.view", NotificationType.NewOrder);
        await SeedAsync($"{run}-Payment", "orders.updatestatus", NotificationType.PaymentReceived);
        await SeedAsync($"{run}-Completed", "orders.updatestatus", NotificationType.OrderCompleted);
        await SeedAsync($"{run}-LowStock", "inventory.view", NotificationType.LowStock);
        await SeedAsync($"{run}-Procurement", "procurement.view", NotificationType.ProcurementRequired);

        (await FetchVisibleTitlesAsync(browser, run))
            .Should().BeEquivalentTo(new[] { $"{run}-NewOrder" });
    }

    [Fact]
    public async Task LegacyNullPermissionRow_RemainsVisibleToAllStaff()
    {
        var run = "RUN-" + Guid.NewGuid().ToString("N")[..8];
        await SeedAsync($"{run}-Legacy", null, NotificationType.System);

        var inv = await CreateStaffAndLoginAsync("InventoryStaff", $"invlegacy-{run.ToLowerInvariant()}@example.com");
        var des = await CreateStaffAndLoginAsync("Designer", $"deslegacy-{run.ToLowerInvariant()}@example.com");

        (await FetchVisibleTitlesAsync(inv, run)).Should().Contain($"{run}-Legacy");
        (await FetchVisibleTitlesAsync(des, run)).Should().Contain($"{run}-Legacy");
    }

    [Fact]
    public async Task MarkRead_IsRejectedForStaffWithoutPermission_AndAllowedForStaffWithPermission()
    {
        var run = "RUN-" + Guid.NewGuid().ToString("N")[..8];
        var id = await SeedAsync($"{run}-LowStock", "inventory.view", NotificationType.LowStock);

        var sales = await CreateStaffAndLoginAsync("SalesStaff", $"salesmark-{run.ToLowerInvariant()}@example.com");
        var inv = await CreateStaffAndLoginAsync("InventoryStaff", $"invmark-{run.ToLowerInvariant()}@example.com");

        // SalesStaff lacks inventory.view: the MVC mark-read guard surfaces 404 and the row
        // stays unread.
        var listPage = await sales.GetAsync("/admin/notifications");
        var readToken = TestSeed.ExtractAntiforgeryToken(await listPage.Content.ReadAsStringAsync());
        var rejected = await sales.PostAsync($"/admin/notifications/{id}/read", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = readToken }));
        rejected.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == id)).IsRead.Should().BeFalse();
        }

        // InventoryStaff holds inventory.view: mark read succeeds.
        var invListPage = await inv.GetAsync("/admin/notifications");
        var invToken = TestSeed.ExtractAntiforgeryToken(await invListPage.Content.ReadAsStringAsync());
        var accepted = await inv.PostAsync($"/admin/notifications/{id}/read", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = invToken }));
        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == id)).IsRead.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Mvc_UnreadCount_Endpoint_IsScopedToTheUsersPermissions()
    {
        var run = "RUN-" + Guid.NewGuid().ToString("N")[..8];
        var email = $"descount-{run.ToLowerInvariant()}@example.com";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var baseline = await db.Notifications.Where(n => !n.IsRead).CountAsync();

            await SeedAsync($"{run}-Visible", "orders.view", NotificationType.NewOrder);
            await SeedAsync($"{run}-Hidden", "inventory.view", NotificationType.LowStock);

            var browser = await LoginOwnerMvcAsync();
            var resp = await browser.GetAsync("/admin/notifications/unread-count");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync<UnreadCountView>();
            body!.Count.Should().Be(baseline + 2); // both run rows are visible to the Owner
        }

        // Cross-check via the Designer: only the orders.view row is visible to them.
        var designer = await CreateStaffAndLoginAsync("Designer", email);
        var visible = await FetchVisibleTitlesAsync(designer, run);
        visible.Should().BeEquivalentTo(new[] { $"{run}-Visible" });
    }

    private sealed record UnreadCountView(int Count);
}
