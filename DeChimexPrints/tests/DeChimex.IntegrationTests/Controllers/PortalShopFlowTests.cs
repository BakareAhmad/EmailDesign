using System.Net;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Owner directive 2026-09-10: product ordering happens ENTIRELY inside the customer
/// portal's Shop section. The public /products section is browse-only; any ordering
/// attempt there is redirected into /portal/shop. These tests pin the separation:
/// public page exposes no ordering actions, the public order endpoints redirect,
/// the portal product page carries the cart + custom-order actions, guests are
/// challenged with a portal ReturnUrl, and the cart/checkout render inside the
/// portal layout for signed-in customers.
/// </summary>
public class PortalShopFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PortalShopFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private const string PortalLayoutMarker = "data-shell=\"portal\"";
    private const string PublicLayoutMarker = "main-content";

    private static async Task<Guid> SeedProductAsync(IServiceProvider services, string name,
        decimal price, bool customizable)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create($"{name} Cat", "portal shop flow seed", 1);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var product = Product.Create(name, "SKU-" + Guid.NewGuid().ToString("N")[..10],
            "Portal shop flow product", category.Id, price, isCustomizable: customizable);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<HttpClient> CustomerBrowserAsync(string email)
    {
        await TestSeed.RegisterCustomerAsync(_factory.Services, email, firstName: "Shop", lastName: "Per");
        await TestSeed.VerifyCustomerAsync(_factory.Services, email);
        var client = CreateBrowser();
        (await TestSeed.LoginMvcAsync(client, email, TestSeed.DefaultPassword)).Should().BeTrue();
        return client;
    }

    [Fact]
    public async Task PublicProductPage_IsBrowseOnly_AndHandsOrderingToThePortal()
    {
        var productId = await SeedProductAsync(_factory.Services, "Browse Only Product", 900m, customizable: false);
        var guest = CreateBrowser();

        var page = await guest.GetAsync($"/products/{productId}");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();

        // The ordering CTA points into the portal shop...
        html.Should().Contain($"/portal/shop/{productId}");
        html.Should().Contain("Buy now");
        // ...and the page carries NO ordering actions of its own.
        html.Should().NotContain("action=\"/cart/add\"", "the public product page must not post to the cart");
        html.Should().NotContain($"action=\"/products/{productId}/order\"", "the public product page must not post an order request");

        // A customizable product likewise links to the portal (never to the public order form).
        var customId = await SeedProductAsync(_factory.Services, "Browse Only Custom", 700m, customizable: true);
        var customHtml = await (await guest.GetAsync($"/products/{customId}")).Content.ReadAsStringAsync();
        customHtml.Should().Contain($"\"/portal/shop/{customId}\"");
        customHtml.Should().Contain("Order / Customize");
        customHtml.Should().NotContain($"action=\"/products/{customId}/order\"");
    }

    [Fact]
    public async Task PublicOrderEndpoints_RedirectEveryoneIntoThePortalShop()
    {
        var productId = await SeedProductAsync(_factory.Services, "Redirect Probe Product", 800m, customizable: true);

        // Unauthenticated: the redirect sends the visitor INTO the portal shop; the portal's
        // own [Authorize] then challenges with the PORTAL url as the login ReturnUrl, so the
        // visitor comes back to the shop after signing in — never to the public section.
        var guest = CreateBrowser();
        var guestGet = await guest.GetAsync($"/products/{productId}/order");
        guestGet.StatusCode.Should().Be(HttpStatusCode.Redirect);
        guestGet.Headers.Location!.ToString().Should().Be($"/portal/shop/{productId}/order");
        var challenged = await guest.GetAsync($"/portal/shop/{productId}/order");
        challenged.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var loginLocation = challenged.Headers.Location!.ToString();
        loginLocation.Should().Contain("/Account/Login", "the portal challenges guests to sign in");
        loginLocation.Should().Contain(Uri.EscapeDataString($"/portal/shop/{productId}/order"),
            "the ReturnUrl points BACK INTO the portal shop, not the public section");

        // A stale public order POST (old bookmarked form) also lands in the portal.
        var guestPost = await guest.PostAsync($"/products/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["quantity"] = "1" }));
        guestPost.StatusCode.Should().Be(HttpStatusCode.Redirect);
        guestPost.Headers.Location!.ToString().Should().Be($"/portal/shop/{productId}/order");

        // Authenticated customer: same redirects, straight through to the portal.
        var customer = await CustomerBrowserAsync($"flow-{Guid.NewGuid():N}@example.com");
        var customerGet = await customer.GetAsync($"/products/{productId}/order");
        customerGet.StatusCode.Should().Be(HttpStatusCode.Redirect);
        customerGet.Headers.Location!.ToString().Should().Be($"/portal/shop/{productId}/order");
    }

    [Fact]
    public async Task PortalShop_CardsOpenPortalProductPages_NotPublicOnes()
    {
        var productId = await SeedProductAsync(_factory.Services, "Card Link Product", 650m, customizable: false);
        var client = await CustomerBrowserAsync($"flow-{Guid.NewGuid():N}@example.com");

        var html = await client.GetStringAsync("/portal/shop");
        html.Should().Contain($"/portal/shop/{productId}", "shop cards must open the portal product page");
        html.Should().NotContain($"href=\"/products/{productId}\"", "shop cards must not send customers to the public section");
    }

    [Fact]
    public async Task PortalShop_ProductPage_RendersOrderingActions_InPortalLayout()
    {
        var plainId = await SeedProductAsync(_factory.Services, "Portal Plain Product", 1200m, customizable: false);
        var customId = await SeedProductAsync(_factory.Services, "Portal Custom Product", 1500m, customizable: true);
        var client = await CustomerBrowserAsync($"flow-{Guid.NewGuid():N}@example.com");

        var plain = await client.GetAsync($"/portal/shop/{plainId}");
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        var plainHtml = await plain.Content.ReadAsStringAsync();
        plainHtml.Should().Contain(PortalLayoutMarker, "the product page renders inside the customer portal");
        plainHtml.Should().Contain("Add to cart");
        plainHtml.Should().Contain($"/portal/shop/{plainId}/add-to-cart", "add-to-cart posts into the portal, not /cart/add");

        var custom = await client.GetAsync($"/portal/shop/{customId}");
        var customHtml = await custom.Content.ReadAsStringAsync();
        customHtml.Should().Contain(PortalLayoutMarker);
        customHtml.Should().Contain("Order / Customize");
        customHtml.Should().Contain($"/portal/shop/{customId}/order");
        customHtml.Should().NotContain("action=\"/cart/add\"");
    }

    [Fact]
    public async Task PortalShop_AddToCart_ThenCartAndCheckoutStayInsideThePortal()
    {
        var productId = await SeedProductAsync(_factory.Services, "Checkout Stay Product", 2000m, customizable: false);
        var client = await CustomerBrowserAsync($"flow-{Guid.NewGuid():N}@example.com");

        var productPage = await client.GetAsync($"/portal/shop/{productId}");
        var token = TestSeed.ExtractAntiforgeryToken(await productPage.Content.ReadAsStringAsync());

        // Add to cart from the portal product page.
        var add = await client.PostAsync($"/portal/shop/{productId}/add-to-cart", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["quantity"] = "2",
                ["__RequestVerificationToken"] = token
            }));
        add.StatusCode.Should().Be(HttpStatusCode.Redirect);
        add.Headers.Location!.ToString().Should().Be("/cart", "the existing cart/checkout flow takes over");

        // The cart renders in the portal layout for the signed-in customer...
        var cart = await client.GetAsync("/cart");
        cart.StatusCode.Should().Be(HttpStatusCode.OK);
        var cartHtml = await cart.Content.ReadAsStringAsync();
        cartHtml.Should().Contain(PortalLayoutMarker);
        cartHtml.Should().Contain("Checkout Stay Product");
        cartHtml.Should().Contain("Proceed to checkout");
        cartHtml.Should().Contain("/portal/shop", "continue-shopping points back into the portal shop");

        // ...and so does the existing checkout page.
        var checkout = await client.GetAsync("/checkout");
        checkout.StatusCode.Should().Be(HttpStatusCode.OK);
        var checkoutHtml = await checkout.Content.ReadAsStringAsync();
        checkoutHtml.Should().Contain(PortalLayoutMarker);
        checkoutHtml.Should().Contain("Checkout Stay Product");
    }

    [Fact]
    public async Task PortalShop_CustomOrderFlow_EndsInPortalWorkspace_WithoutPublicPages()
    {
        var services = _factory.Services;
        var productId = await SeedProductAsync(services, "Portal Custom Flow Product", 3000m, customizable: true);
        var client = await CustomerBrowserAsync($"flow-{Guid.NewGuid():N}@example.com");

        // The portal order form is the entry point (the public page links here).
        var form = await client.GetAsync($"/portal/shop/{productId}/order");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var formHtml = await form.Content.ReadAsStringAsync();
        formHtml.Should().Contain(PortalLayoutMarker);
        formHtml.Should().Contain($"action=\"/portal/shop/{productId}/order\"");

        var token = TestSeed.ExtractAntiforgeryToken(formHtml);
        var submit = await client.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "1",
                ["notes"] = "Portal-only flow",
                ["__RequestVerificationToken"] = token
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);
        submit.Headers.Location!.ToString().Should().Contain("/portal/orders/", "submission lands in the portal workspace");

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var order = await db.Orders.OrderByDescending(o => o.CreatedDateUtc).FirstAsync();
        order.Status.Should().Be(OrderStatus.AwaitingReview);
        order.CustomerNotes.Should().Contain("Portal-only flow");
    }

    [Fact]
    public async Task PortalNav_ExposesCart_SoCustomersReachCheckoutFromAnywhere()
    {
        var client = await CustomerBrowserAsync($"flow-{Guid.NewGuid():N}@example.com");
        var html = await client.GetStringAsync("/portal");
        html.Should().Contain("href=\"/cart\"", "the portal sidebar must link to the cart");
    }
}
