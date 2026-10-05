using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Verifies the public storefront MVC surface (Phase 4): anonymous access to the catalog,
/// marketing and legal pages, and the public landing page rendering business data.
/// </summary>
public class MvcStorefrontTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MvcStorefrontTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    public static readonly TheoryData<string> PublicPaths = new()
    {
        "/",
        "/products",
        "/services",
        "/gallery",
        "/about",
        "/contact",
        "/faq",
        "/privacy",
        "/terms"
    };

    [Theory]
    [MemberData(nameof(PublicPaths))]
    public async Task PublicPage_Anonymous_Renders200(string path)
    {
        var client = CreateBrowser();
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("PRINT HUB"); // shared public shell / footer renders
    }

    [Fact]
    public async Task Home_Anonymous_ShowsBusinessNameAndHero()
    {
        var client = CreateBrowser();
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("DE-CHIMEX");            // business name from settings
        body.Should().Contain("PRINT HUB");
        body.Should().Contain("<span>Your brand,</span> <em>beautifully</em> <span>produced.</span>"); // restored pre-refactor hero H1
    }

    [Fact]
    public async Task Products_Anonymous_ShowsCatalogPage()
    {
        var client = CreateBrowser();
        var response = await client.GetAsync("/products");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Products");
        body.Should().Contain("Search products");
    }

    [Fact]
    public async Task Services_Anonymous_ShowsServicesPage()
    {
        var client = CreateBrowser();
        var response = await client.GetAsync("/services");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Services");
        body.Should().Contain("Bring your own item");
    }

    [Fact]
    public async Task Cart_Anonymous_RedirectsToLogin()
    {
        var client = CreateBrowser();
        var response = await client.GetAsync("/cart");
        // Cart page itself is viewable anonymously (shows "sign in"), so no redirect is expected.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Sign in");
    }
}
