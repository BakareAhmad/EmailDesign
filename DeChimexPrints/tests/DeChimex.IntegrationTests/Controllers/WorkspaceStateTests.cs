using System.Net;
using System.Text.Json;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Live-workspace HTTP surface: the JSON state feeds behind reconnect resync /
/// structural pushes, and the portal notification-bell endpoints. Verifies the
/// same ownership/visibility guards as the HTML pages (owner-only state, the
/// customer redaction, customer-visible notifications only).
/// </summary>
public class WorkspaceStateTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public WorkspaceStateTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private async Task<(HttpClient Browser, Guid OrderId, Guid UserId)> SeedWorkspaceAsync(string tag)
    {
        var services = _factory.Services;
        var email = $"wsstate-{tag}-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email);
        await TestSeed.VerifyCustomerAsync(services, email);
        var productId = await SalesAssignmentTests.SeedCustomizableProductAsync(services, $"State {tag}?");
        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SalesAssignmentTests.SubmitRequestAsync(browser, productId, "Blue");
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var userId = (await db.Users.FirstAsync(u => u.Email.Value == email)).Id;
        return (browser, orderId, userId);
    }

    [Fact]
    public async Task PortalState_OwnerOnly_AndRedacted()
    {
        var (browser, orderId, _) = await SeedWorkspaceAsync("owner");
        var (stranger, _, _) = await SeedWorkspaceAsync("stranger");

        var state = await browser.GetAsync($"/portal/orders/{orderId}/workspace/state");
        state.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("workspace").GetProperty("orderNumber").GetString()
            .Should().NotBeNullOrEmpty();
        // The feed carries the same redaction as the HTML view: no sales name,
        // generic assignment text.
        doc.RootElement.GetProperty("workspace").GetProperty("assignedSalesName").ValueKind
            .Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("canPay").GetBoolean().Should().BeFalse();

        var foreign = await stranger.GetAsync($"/portal/orders/{orderId}/workspace/state");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var anon = await CreateBrowser().GetAsync($"/portal/orders/{orderId}/workspace/state");
        anon.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task AdminState_StaffOnly()
    {
        var (_, orderId, _) = await SeedWorkspaceAsync("admin");
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);

        var state = await owner.GetAsync($"/admin/orders/{orderId}/workspace/state");
        state.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("workspace").GetProperty("id").GetGuid().Should().Be(orderId);

        var missing = await owner.GetAsync($"/admin/orders/{Guid.NewGuid()}/workspace/state");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var anon = await CreateBrowser().GetAsync($"/admin/orders/{orderId}/workspace/state");
        anon.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PortalNotifications_ScopedToOwnRows_WithMarkRead()
    {
        var (browser, _, userId) = await SeedWorkspaceAsync("notif");
        var services = _factory.Services;
        Guid ownId;
        Guid staffOnlyId;
        Guid legacyBroadcastId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var own = Notification.Create(NotificationType.DesignProofReady, "Design proof ready",
                "A design proof is ready for your approval.", userId, "/portal/orders/00000000-0000-0000-0000-000000000000/workspace");
            var staffOnly = Notification.Create(NotificationType.NewOrder, "New order placed",
                "Staff broadcast.", null, "/admin/orders", Permissions.Orders.View);
            // Legacy all-staff broadcast: no recipient AND no permission. Previously this
            // leaked to customers (list, badge and live push); it is staff-audience only.
            var legacyBroadcast = Notification.Create(NotificationType.LowStock, "Legacy staff broadcast",
                "Internal: stock levels need attention.", null, "/admin/inventory", null);
            db.Notifications.Add(own);
            db.Notifications.Add(staffOnly);
            db.Notifications.Add(legacyBroadcast);
            await db.SaveChangesAsync();
            ownId = own.Id;
            staffOnlyId = staffOnly.Id;
            legacyBroadcastId = legacyBroadcast.Id;
        }

        var count = await browser.GetAsync("/portal/notifications/unread-count");
        count.StatusCode.Should().Be(HttpStatusCode.OK);
        (await count.Content.ReadAsStringAsync()).Should().Contain("\"count\":1");

        var recent = await browser.GetAsync("/portal/notifications/recent");
        recent.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        var items = doc.RootElement.EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("id").GetGuid().Should().Be(ownId);
        items[0].GetProperty("title").GetString().Should().Be("Design proof ready");

        // Mark-read needs the antiforgery token: any portal page carries one (logout form).
        var page = await browser.GetAsync("/portal/orders");
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        token.Should().NotBeEmpty();
        var read = await browser.PostAsync($"/portal/notifications/{ownId}/read",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        var countAfter = await browser.GetAsync("/portal/notifications/unread-count");
        (await countAfter.Content.ReadAsStringAsync()).Should().Contain("\"count\":0");

        // A staff-scoped notification is invisible to the customer, including mark-read.
        var foreignRead = await browser.PostAsync($"/portal/notifications/{staffOnlyId}/read",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        foreignRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var legacyRead = await browser.PostAsync($"/portal/notifications/{legacyBroadcastId}/read",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        legacyRead.StatusCode.Should().Be(HttpStatusCode.NotFound, "a staff broadcast is not visible to a customer");
        (await (await browser.GetAsync("/portal/notifications/recent")).Content.ReadAsStringAsync())
            .Should().NotContain("Legacy staff broadcast");

        // Staff still receive the legacy broadcast.
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var staffPage = await owner.GetAsync("/admin/notifications");
        staffPage.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staffPage.Content.ReadAsStringAsync()).Should().Contain("Legacy staff broadcast");

        var anon = await CreateBrowser().GetAsync("/portal/notifications/unread-count");
        anon.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
}
