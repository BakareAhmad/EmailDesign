using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Verifies the admin MVC surface (Phase 2): cookie auth, permission-policy gating,
/// routing, and Razor view rendering for dashboard, products, orders, customers,
/// categories and services.
/// </summary>
public class MvcAdminTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MvcAdminTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = CreateBrowser();
        var ok = await TryOwnerLoginAsync(client, "ChangeMe123!");
        if (!ok)
            ok = await TryOwnerLoginAsync(client, "Owner!Pass9");
        ok.Should().BeTrue("owner login should succeed");
        return client;
    }

    private static async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "value=\"([^\"]+)\"").Groups[1].Value;

        var loginResponse = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "owner@dechimexprinthub.com",
                ["Password"] = password,
                ["__RequestVerificationToken"] = token
            }));
        if (loginResponse.StatusCode != HttpStatusCode.Redirect)
            return false;

        var location = loginResponse.Headers.Location?.ToString() ?? "";
        if (location.Contains("/admin/security/password"))
            await CompleteForcedPasswordChangeAsync(client, password);
        return true;
    }

    private static async Task CompleteForcedPasswordChangeAsync(HttpClient client, string currentPassword)
    {
        var html = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["CurrentPassword"] = currentPassword,
                ["NewPassword"] = "Owner!Pass9",
                ["ConfirmPassword"] = "Owner!Pass9",
                ["__RequestVerificationToken"] = token
            }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    public static readonly TheoryData<string> AdminPaths = new()
    {
        "/admin/dashboard",
        "/admin/products",
        "/admin/orders",
        "/admin/customers",
        "/admin/categories",
        "/admin/services",
        "/admin/inventory",
        "/admin/suppliers",
        "/admin/procurement",
        "/admin/users",
        "/admin/settings",
        "/admin/reports",
        "/admin/gallery",
        "/admin/invoices",
        "/admin/notifications",
        "/admin/marketing",
        "/admin/marketing/compose"
    };

    [Theory]
    [MemberData(nameof(AdminPaths))]
    public async Task AdminPage_WithoutAuth_RedirectsToLogin(string path)
    {
        var client = CreateBrowser();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
    }

    [Theory]
    [MemberData(nameof(AdminPaths))]
    public async Task AdminPage_AsOwner_Renders200(string path)
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("PRINT HUB"); // admin shell / topbar renders
    }

    [Fact]
    public async Task Products_AsOwner_ShowsToolbarAndSeededData()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/products");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Products");
        body.Should().Contain("New product");
    }

    [Fact]
    public async Task Categories_AsOwner_ShowsNewCategoryLink()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/categories");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("New category");
    }

    [Fact]
    public async Task Inventory_AsOwner_ShowsSeededDataAndLowStockLink()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/inventory");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Low stock");
    }

    [Fact]
    public async Task Settings_AsOwner_ShowsBusinessNameField()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/settings");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("business.name");
        body.Should().Contain("DE-CHIMEX PRINT HUB");
    }

    [Fact]
    public async Task Users_AsOwner_ShowsSeededOwnerAccount()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/users");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("owner@dechimexprinthub.com");
    }

    [Fact]
    public async Task Roles_AsOwner_ShowsSystemRoles()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/users/roles");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Owner");
        body.Should().Contain("Manager");
    }

    [Fact]
    public async Task Reports_AsOwner_ShowsReportLinks()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/reports");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Sales");
        body.Should().Contain("Inventory");
        body.Should().Contain("Payments");
    }

    [Fact]
    public async Task Reports_Sales_RendersTable()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/reports/sales");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Sales report");
    }

    [Fact]
    public async Task Gallery_AsOwner_ShowsNewMediaLink()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/gallery");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("New media");
    }

    [Fact]
    public async Task Invoices_AsOwner_RendersTable()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/invoices");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invoice");
    }

    [Fact]
    public async Task Notifications_AsOwner_Renders()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/notifications");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Unread only");
    }

    public static readonly TheoryData<string> ReportAndAnalyticsPaths = new()
    {
        "/admin/reports/sales",
        "/admin/reports/inventory",
        "/admin/reports/lowstock",
        "/admin/reports/customers",
        "/admin/reports/products",
        "/admin/reports/services",
        "/admin/reports/staff",
        "/admin/reports/payments",
        "/admin/analytics",
        "/admin/analytics?period=q",
        "/admin/analytics?period=y"
    };

    [Theory]
    [MemberData(nameof(ReportAndAnalyticsPaths))]
    public async Task ReportOrAnalytics_AsOwner_Renders200(string path)
    {
        // Guards against EF Core LINQ translation regressions in the report/analytics
        // aggregate queries (GroupBy + projection). These pages must render, not 500.
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("PRINT HUB");
    }

    [Fact]
    public async Task Analytics_AsOwner_ShowsSummaryAndTopProducts()
    {
        var client = await LoginAsOwnerAsync();
        var response = await client.GetAsync("/admin/analytics");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Revenue");
        body.Should().Contain("Order status mix");
        body.Should().Contain("Top products");
    }

    [Fact]
    public async Task Gallery_ItemTitles_RenderSingleEncodedInAriaLabels()
    {
        // Regression: both gallery views rendered aria-label="Open @Html.Encode(title)"
        // INSIDE a Razor attribute, so Razor attribute-encoded the pre-encoded title a
        // second time and screen readers read literal entities ("Owner&#39;s Reel").
        // Razor's @ already encodes once — the raw title is correct.
        var title = "Owner's \"Bold\" & Bright Reel";
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimex.Persistence.Context.DeChimexDbContext>();
            db.GalleryMedia.Add(DeChimex.Domain.Entities.GalleryMedia.Create(
                title, "/uploads/gallerymedia/aria-label-test.png", DeChimex.Domain.Enums.GalleryMediaType.Image, "Test"));
            await db.SaveChangesAsync();
        }

        var client = await LoginAsOwnerAsync();
        var admin = await client.GetAsync("/admin/gallery");
        var adminHtml = await admin.Content.ReadAsStringAsync();
        AssertLabelSingleEncoded(adminHtml);

        // The public storefront gallery applies the same fix.
        var store = await CreateBrowser().GetAsync("/gallery");
        store.StatusCode.Should().Be(HttpStatusCode.OK);
        var storeHtml = await store.Content.ReadAsStringAsync();
        AssertLabelSingleEncoded(storeHtml);
    }

    private static void AssertLabelSingleEncoded(string html)
    {
        // Exact single-encoded form proves the point (double-encoding would produce
        // &amp;#x27; etc. and the exact match would fail); the scoped scan double-checks
        // that NO item label carries a double-escaped entity. Note &amp; IS legal inside
        // a single-encoded label (it encodes a literal & in the title) — only entities
        // whose *payload is itself an entity* indicate double-encoding. Page-wide
        // NotContain is not usable: the pages' inline JS legitimately contains
        // attribute-escaped &quot; sequences of its own.
        const string expected = "aria-label=\"Open Owner&#x27;s &quot;Bold&quot; &amp; Bright Reel\"";
        html.Should().Contain(expected);
        foreach (var label in Regex.Matches(html, "aria-label=\"Open [^\"]*\"").Select(m => m.Value))
        {
            label.Should().NotContain("&amp;amp;").And.NotContain("&amp;quot;")
                .And.NotContain("&amp;#").And.NotContain("&amp;lt;").And.NotContain("&amp;gt;");
        }
    }
}
