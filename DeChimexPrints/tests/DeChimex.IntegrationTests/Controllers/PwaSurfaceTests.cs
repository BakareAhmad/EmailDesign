using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeChimex.Infrastructure.Persistence;
using DeChimex.Persistence.Context;
using DeChimex.Domain.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// PWA plumbing over real HTTP: manifest serving (content type + brand fields),
/// never-cache headers for service-worker/manifest/offline shell, the install
/// help page, and the Web Push endpoint surface (auth guard, antiforgery-protected
/// subscribe/unsubscribe lifecycle with multi-device + duplicate-endpoint handling,
/// and the seeded VAPID key pair idempotency).
/// </summary>
public class PwaSurfaceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PwaSurfaceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static string MetaCsrf(string html) =>
        Regex.Match(html, "name=\\\"csrf-token\\\"[^>]*content=\\\"([^\\\"]+)\\\"").Groups[1].Value;

    // ---------- manifest + static PWA assets ----------

    [Fact]
    public async Task Manifest_IsServedWithManifestType_AndBrandFields()
    {
        var browser = CreateBrowser();
        var res = await browser.GetAsync("/site.webmanifest");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/manifest+json");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("DE-CHIMEX PRINT HUB");
        root.GetProperty("display").GetString().Should().Be("standalone");
        root.GetProperty("start_url").GetString().Should().Be("/");
        root.GetProperty("theme_color").GetString().Should().NotBeNullOrEmpty();

        var icons = root.GetProperty("icons");
        icons.GetArrayLength().Should().BeGreaterThan(0);
        foreach (var icon in icons.EnumerateArray())
        {
            var src = icon.GetProperty("src").GetString();
            src.Should().NotBeNull().And.StartWith("/");
            var head = await browser.GetAsync(src!);
            head.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // manifest shortcuts: public routes only
        var shortcuts = root.GetProperty("shortcuts");
        shortcuts.GetArrayLength().Should().BeGreaterOrEqualTo(2);
        foreach (var sc in shortcuts.EnumerateArray())
        {
            var url = sc.GetProperty("url").GetString()!;
            var target = await browser.GetAsync(url);
            // every shortcut target must be reachable without authentication
            // (302 to login would mean a private route leaked into the manifest)
            (target.StatusCode == HttpStatusCode.OK ||
             target.StatusCode == HttpStatusCode.Redirect).Should().BeTrue();
        }
    }

    [Fact]
    public async Task ServiceWorker_Manifest_OfflineHtml_AreNeverCached()
    {
        var browser = CreateBrowser();

        var sw = await browser.GetAsync("/service-worker.js");
        sw.StatusCode.Should().Be(HttpStatusCode.OK);
        sw.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
        sw.Headers.CacheControl!.NoCache.Should().BeTrue(
            "a cached service worker delays update detection (the stale-trap the spec forbids)");
        var swBody = await sw.Content.ReadAsStringAsync();
        swBody.Should().Contain("dcx-v2"); // versioned cache name
        swBody.Should().Contain("/checkout"); // checkout must never be served from cache
        swBody.Should().Contain("/hubs"); // SignalR paths never touched

        var manifest = await browser.GetAsync("/site.webmanifest");
        manifest.Headers.CacheControl!.NoCache.Should().BeTrue();

        var offline = await browser.GetAsync("/offline.html");
        offline.StatusCode.Should().Be(HttpStatusCode.OK);
        offline.Headers.CacheControl!.NoCache.Should().BeTrue();
        var offlineBody = await offline.Content.ReadAsStringAsync();
        offlineBody.Should().Contain("You're offline");
        // degraded, never fake success: the page must not pretend the shop works
        offlineBody.Should().NotContain("order placed");
    }

    [Fact]
    public async Task InstallHelp_RendersAndroidAndIosGuidance()
    {
        var browser = CreateBrowser();
        var res = await browser.GetAsync("/install-help");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain("Add to Home Screen"); // iOS path
        html.Should().Contain("Install app");        // Android Chrome menu item
        html.Should().Contain("&#8942;");            // Chrome overflow-menu marker (⋮ entity)
    }

    // ---------- Web Push endpoints ----------

    [Fact]
    public async Task PushEndpoints_RequireAuthentication()
    {
        var browser = CreateBrowser();
        (await browser.GetAsync("/portal/notifications/push/vapid-public-key"))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // POSTs without the antiforgery token are rejected even when authenticated
        var (authed, _, _) = await SeedCustomerAsync("guard");
        var anon = new HttpRequestMessage(HttpMethod.Post, "/portal/notifications/push/subscribe")
        {
            Content = new StringContent("{\"endpoint\":\"https://fcm.googleapis.com/x\"}",
                Encoding.UTF8, "application/json")
        };
        (await authed.SendAsync(anon)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Subscribe_List_Unsubscribe_MultiDeviceAndDuplicateLifecycle()
    {
        var (browser, _, userId) = await SeedCustomerAsync("push");
        var csrf = await GetCsrfAsync(browser);

        var keyRes = await browser.GetAsync("/portal/notifications/push/vapid-public-key");
        keyRes.StatusCode.Should().Be(HttpStatusCode.OK);
        using var keyDoc = JsonDocument.Parse(await keyRes.Content.ReadAsStringAsync());
        var vapidKey = keyDoc.RootElement.GetProperty("key").GetString();
        vapidKey.Should().NotBeNullOrEmpty();
        vapidKey!.Length.Should().BeGreaterThan(80, "a P-256 VAPID public key is 87 base64url chars");

        var endpointA = "https://fcm.googleapis.com/fcm/send/dcx-device-a";
        var endpointB = "https://fcm.googleapis.com/fcm/send/dcx-device-b";

        (await PostJsonAsync(browser, csrf, "/portal/notifications/push/subscribe",
            new { endpoint = endpointA, keysP256dh = "B" + new string('p', 86), keysAuth = new string('a', 22), userAgent = "HeadlessTest" }))
            .Should().Be(HttpStatusCode.OK);
        (await PostJsonAsync(browser, csrf, "/portal/notifications/push/subscribe",
            new { endpoint = endpointB, keysP256dh = "B" + new string('q', 86), keysAuth = new string('b', 22), userAgent = "HeadlessTest" }))
            .Should().Be(HttpStatusCode.OK);

        // second device of the same user = second row; duplicate endpoint = updated, not duplicated
        (await PostJsonAsync(browser, csrf, "/portal/notifications/push/subscribe",
            new { endpoint = endpointA, keysP256dh = "B" + new string('r', 86), keysAuth = new string('a', 22), userAgent = "HeadlessTest2" }))
            .Should().Be(HttpStatusCode.OK);


        var rows = await RowsAsync(userId);
        rows.Count.Should().Be(2, "two devices, the duplicate endpoint updated in place");
        rows.Count.Should().Be(2);
        rows.Single(r => r.Endpoint == endpointA).P256dh.Should().Be("B" + new string('r', 86));

        // unsubscribing one device keeps the other
        (await PostJsonAsync(browser, csrf, "/portal/notifications/push/unsubscribe",
            new { endpoint = endpointA })).Should().Be(HttpStatusCode.OK);
        (await RowsAsync(userId)).Select(r => r.Endpoint).Should().ContainSingle().Which.Should().Be(endpointB);

        // unsubscribing an unknown endpoint is still a clean 200 (idempotent client cleanup)
        (await PostJsonAsync(browser, csrf, "/portal/notifications/push/unsubscribe",
            new { endpoint = "https://gone.example/none" })).Should().Be(HttpStatusCode.OK);
        (await RowsAsync(userId)).Should().HaveCount(1);

        // a browser resubscribing a previously-unsubscribed endpoint must not hit the
        // unique index: the soft-deleted row stays in the table but is excluded
        (await PostJsonAsync(browser, csrf, "/portal/notifications/push/subscribe",
            new { endpoint = endpointA, keysP256dh = "B" + new string('s', 86), keysAuth = new string('a', 22), userAgent = "HeadlessTest3" }))
            .Should().Be(HttpStatusCode.OK, "resubscribing a soft-deleted endpoint must succeed");
        var resub = await RowsAsync(userId);
        resub.Should().HaveCount(2);
        resub.Single(r => r.Endpoint == endpointA).P256dh.Should().Be("B" + new string('s', 86));
    }

    [Fact]
    public async Task Seeder_VapidKeys_Idempotent()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingRepository>();
        var before = (await settings.GetByKeyAsync("push.vapid.public_key"))?.Value;
        before.Should().NotBeNullOrEmpty("the seeder provisions the VAPID pair at startup");

        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();

        var after = (await settings.GetByKeyAsync("push.vapid.public_key"))?.Value;
        after.Should().Be(before, "re-seeding must never rotate the VAPID keys (would orphan all live subscriptions)");
    }

    // ---------- helpers ----------

    private async Task<(HttpClient Browser, string Email, Guid UserId)> SeedCustomerAsync(string tag)
    {
        var services = _factory.Services;
        var email = $"pwa-{tag}-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email);
        await TestSeed.VerifyCustomerAsync(services, email);
        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var userId = (await db.Users.FirstAsync(u => u.Email.Value == email)).Id;
        return (browser, email, userId);
    }

    private static async Task<string> GetCsrfAsync(HttpClient browser)
    {
        var page = await browser.GetAsync("/portal");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var csrf = MetaCsrf(await page.Content.ReadAsStringAsync());
        csrf.Should().NotBeNullOrEmpty("the customer layout must expose the antiforgery meta for JSON posts");
        return csrf;
    }

    private static async Task<HttpStatusCode> PostJsonAsync(HttpClient browser, string csrf, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("RequestVerificationToken", csrf);
        var res = await browser.SendAsync(req);
        return res.StatusCode;
    }

    private async Task<System.Collections.Generic.List<DeChimex.Domain.Entities.PushSubscription>> RowsAsync(Guid userId)
    {
        // through the repository: soft-deleted (unsubscribed) rows must be invisible
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<DeChimex.Domain.Repositories.IPushSubscriptionRepository>();
        return [.. await repo.ListByUserAsync(userId)];
    }
}
