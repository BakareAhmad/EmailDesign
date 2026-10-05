using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DeChimex.Contracts.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Verifies the customer portal MVC surface (Phase 3): customer login, portal routing,
/// role gating, and Razor view rendering.
/// </summary>
public class MvcPortalTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MvcPortalTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> RegisterAndLoginCustomerAsync()
    {
        // Register a fresh customer through the shared Application layer (the old
        // /api/v1/auth/register has been removed with the superseded API surface).
        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email, firstName: "Ada", lastName: "Customer");
        await TestSeed.VerifyCustomerAsync(_factory.Services, email); // gated signup: verify before login

        // Log in via the MVC cookie flow.
        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();
        return browser;
    }

    public static readonly TheoryData<string> PortalPaths = new()
    {
        "/portal",
        "/portal/orders",
        "/portal/addresses",
        "/portal/profile",
        "/portal/payments",
        "/portal/invoices",
        "/portal/wishlist",
        "/portal/shop"
    };

    [Theory]
    [MemberData(nameof(PortalPaths))]
    public async Task PortalPage_WithoutAuth_RedirectsToLogin(string path)
    {
        var client = CreateBrowser();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
    }

    [Theory]
    [MemberData(nameof(PortalPaths))]
    public async Task PortalPage_AsCustomer_Renders200(string path)
    {
        var client = await RegisterAndLoginCustomerAsync();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("PRINT HUB"); // portal shell / topbar renders
    }

    [Fact]
    public async Task PortalDashboard_AsCustomer_ShowsWelcome()
    {
        var client = await RegisterAndLoginCustomerAsync();
        var response = await client.GetAsync("/portal");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Hi, Ada");
    }

    [Fact]
    public async Task PortalShop_AsCustomer_ShowsCatalogLikeStorefront_AndSidebarMenu()
    {
        await TestSeed.SeedProductAsync(_factory.Services, "Portal Shop Mug", 500m);
        var client = await RegisterAndLoginCustomerAsync();

        var response = await client.GetAsync("/portal/shop");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // The product section renders exactly as on the public storefront...
        body.Should().Contain("Portal Shop Mug");
        body.Should().Contain("500.00");
        body.Should().Contain("/portal/shop/");         // cards open the PORTAL product page (ordering lives there, 2026-09-10)
        body.Should().NotContain("href=\"/products/", "customers are never sent to the public product section to order");
        body.Should().Contain("Filter");
        // ...and the customer shell carries the Shop navigation.
        body.Should().Contain("Shop");
        body.Should().Contain("/portal/shop");
    }

    [Fact]
    public async Task AdminPage_AsCustomer_IsDenied()
    {
        var client = await RegisterAndLoginCustomerAsync();
        var response = await client.GetAsync("/admin/products");
        // Customer role has no admin permissions: policy authorization must reject.
        // Cookie auth redirects to the AccessDeniedPath for browser (non-API) requests.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
    }
}
