using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DeChimex.Persistence.Context;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Verifies the customer wishlist feature on the storefront + portal MVC surface:
/// a signed-in customer can add a product to their wishlist from the product details
/// page, view saved items in the portal, and remove them. Anonymous visitors get a
/// "sign in to save" prompt instead of an add action.
/// </summary>
public class WishlistTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public WishlistTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<Guid> SeedProductAsync(string name)
        => await TestSeed.SeedProductAsync(_factory.Services, name);

    private async Task<HttpClient> RegisterAndLoginCustomerAsync()
    {
        var email = $"wish-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email);
        await TestSeed.VerifyCustomerAsync(_factory.Services, email); // gated signup: verify before login

        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();
        return browser;
    }

    [Fact]
    public async Task ProductDetails_Anonymous_ShowsSignInToSave()
    {
        var productId = await SeedProductAsync("Wishlist Product");

        var response = await CreateBrowser().GetAsync($"/products/{productId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Sign in to save");
        body.Should().NotContain("Add to wishlist");
        body.Should().NotContain("Saved");
    }

    [Fact]
    public async Task ProductDetails_AsCustomer_ShowsAddToWishlist()
    {
        var productId = await SeedProductAsync("Wishlist Product");

        var client = await RegisterAndLoginCustomerAsync();
        var response = await client.GetAsync($"/products/{productId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Add to wishlist");
    }

    [Fact]
    public async Task AddToWishlist_ThenShowInPortal_ThenRemove()
    {
        var productId = await SeedProductAsync("Wishlist Product");

        var client = await RegisterAndLoginCustomerAsync();

        // 1) Load the product page as the customer and capture the anti-forgery token.
        var detailsPage = await client.GetAsync($"/products/{productId}");
        detailsPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailsHtml = await detailsPage.Content.ReadAsStringAsync();
        var antiforgeryToken = TestSeed.ExtractAntiforgeryToken(detailsHtml);

        // 2) Add to wishlist -> redirect back to the product details page.
        var add = await client.PostAsync($"/products/wishlist/{productId}", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = antiforgeryToken }));
        add.StatusCode.Should().Be(HttpStatusCode.Redirect);
        add.Headers.Location!.ToString().Should().Contain($"/products/{productId}");

        // 3) The product page now reflects the saved state.
        var afterAdd = await client.GetAsync($"/products/{productId}");
        (await afterAdd.Content.ReadAsStringAsync()).Should().Contain("Saved");

        // 4) The portal wishlist lists the saved product.
        var portal = await client.GetAsync("/portal/wishlist");
        portal.StatusCode.Should().Be(HttpStatusCode.OK);
        var portalHtml = await portal.Content.ReadAsStringAsync();
        portalHtml.Should().Contain("Wishlist Product");

        // 5) Remove via the portal and confirm it is gone.
        var remove = await client.PostAsync($"/products/wishlist/{productId}/remove", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = antiforgeryToken }));
        remove.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var portalEmpty = await client.GetAsync("/portal/wishlist");
        (await portalEmpty.Content.ReadAsStringAsync()).Should().NotContain("Wishlist Product");
    }

    [Fact]
    public async Task WishlistPage_WithoutAuth_RedirectsToLogin()
    {
        var client = CreateBrowser();
        var response = await client.GetAsync("/portal/wishlist");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
    }
    private static async Task<string> Token(HttpClient client,string url)
        => TestSeed.ExtractAntiforgeryToken(await (await client.GetAsync(url)).Content.ReadAsStringAsync());
    private static Task<HttpResponseMessage> Submit(HttpClient client,string url,string token,string? back=null)
        => client.PostAsync(url,new FormUrlEncodedContent(new Dictionary<string,string>{["__RequestVerificationToken"]=token,["returnUrl"]=back??""}));

    [Fact]
    public async Task PortalShop_Controls_AddRepeatRemoveReadd_AndKeepFilters()
    {
        var id=await SeedProductAsync("Portal favourite");var c=await RegisterAndLoginCustomerAsync();
        var url="/portal/shop?search=Portal&sort=low&page=1";
        (await (await c.GetAsync(url)).Content.ReadAsStringAsync()).Should().Contain($"/portal/wishlist/add/{id}");
        var token=await Token(c,url);
        for(int i=0;i<2;i++){
            var response=await Submit(c,$"/portal/wishlist/add/{id}",token,url);
            response.StatusCode.Should().Be(HttpStatusCode.Redirect);response.Headers.Location!.OriginalString.Should().Be(url);
        }
        var detail=await (await c.GetAsync($"/portal/shop/{id}")).Content.ReadAsStringAsync();
        detail.Should().Contain("Remove from wishlist");detail.Should().Contain("aria-pressed=\"true\"");
        var saved=await (await c.GetAsync("/portal/wishlist")).Content.ReadAsStringAsync();
        saved.Should().Contain($"/portal/shop/{id}");saved.Should().Contain("View product");
        for(int i=0;i<2;i++)(await Submit(c,$"/portal/wishlist/remove/{id}",token,url)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await (await c.GetAsync($"/portal/shop/{id}")).Content.ReadAsStringAsync()).Should().Contain($"/portal/wishlist/add/{id}");
        (await Submit(c,$"/portal/wishlist/add/{id}",token)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var scope=_factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var rows=await db.WishlistItems.IgnoreQueryFilters().Where(w=>w.ProductId==id).ToListAsync();
        rows.Should().ContainSingle();rows[0].IsDeleted.Should().BeFalse();rows[0].DeletedDateUtc.Should().BeNull();
    }

    [Fact]
    public async Task PortalWishlist_MembershipIsCustomerScoped_AndDoesNotEmitBusinessFeedNoise()
    {
        var id=await SeedProductAsync("Private favourite");var a=await RegisterAndLoginCustomerAsync();var b=await RegisterAndLoginCustomerAsync();
        var ta=await Token(a,"/portal/shop");var tb=await Token(b,"/portal/shop");
        await Submit(a,$"/portal/wishlist/add/{id}",ta);
        (await (await b.GetAsync("/portal/wishlist")).Content.ReadAsStringAsync()).Should().NotContain("Private favourite");
        await Submit(b,$"/portal/wishlist/remove/{id}",tb);
        (await (await a.GetAsync("/portal/wishlist")).Content.ReadAsStringAsync()).Should().Contain("Private favourite");
        using var scope=_factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.AuditLogs.AnyAsync(x=>x.EntityName=="WishlistItem"&&x.ActivityType!=null)).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://example.com/steal")]
    [InlineData("//example.com")]
    [InlineData("/\\example.com")]
    public async Task ExternalReturnUrlsAreNeverRedirectTargets(string back)
    {
        var id=await SeedProductAsync("Safe redirect");var c=await RegisterAndLoginCustomerAsync();var token=await Token(c,"/portal/shop");
        var response=await Submit(c,$"/portal/wishlist/add/{id}",token,back);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);response.Headers.Location!.OriginalString.Should().Be("/portal/wishlist");
    }

    [Fact]
    public async Task MutationRequiresCustomerCsrfAndAnAvailableProduct()
    {
        var id=await SeedProductAsync("Available initially");var c=await RegisterAndLoginCustomerAsync();var token=await Token(c,"/portal/shop");
        (await c.PostAsync($"/portal/wishlist/add/{id}",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.GetAsync($"/portal/wishlist/add/{id}")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await Submit(CreateBrowser(),$"/portal/wishlist/add/{id}",token)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        var owner=await TestSeed.LoginOwnerMvcAsync(_factory.Services,CreateBrowser);
        (await Submit(owner,$"/portal/wishlist/add/{id}",await Token(owner,"/admin/products"))).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden,HttpStatusCode.Redirect);
        (await Submit(c,$"/portal/wishlist/add/{Guid.NewGuid()}",token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var scope=_factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var product=await db.Products.FindAsync(id);product!.Deactivate();await db.SaveChangesAsync();
        (await Submit(c,$"/portal/wishlist/add/{id}",token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Submit(c,$"/products/wishlist/{id}",token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await db.WishlistItems.IgnoreQueryFilters().AnyAsync(w=>w.ProductId==id)).Should().BeFalse();
    }

}
