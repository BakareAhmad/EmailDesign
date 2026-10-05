using System.Net;
using System.Security.Claims;
using System.Text;
using DeChimex.Host.Middleware;
using DeChimex.Shared.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>
/// Error pages offer the signed-in user's OWN home. Previously both the MVC error view and
/// the exception error page showed every signed-in user — customers included — an
/// "Admin dashboard" button.
/// </summary>
public class ErrorPageAudienceTests : IClassFixture<DevPortalFactory>
{
    private readonly DevPortalFactory _f;
    public ErrorPageAudienceTests(DevPortalFactory f) => _f = f;

    private async Task<HttpClient> CustomerAsync()
    {
        var email = $"errpage-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(_f.Services, email);
        await TestSeed.VerifyCustomerAsync(_f.Services, email);
        var c = _f.Browser();
        (await TestSeed.LoginMvcAsync(c, email, TestSeed.DefaultPassword)).Should().BeTrue();
        return c;
    }

    [Fact]
    public async Task Customer_error_page_links_to_their_account_not_the_admin_dashboard()
    {
        var c = await CustomerAsync();
        var r = await c.GetAsync("/Error?code=400");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var html = await r.Content.ReadAsStringAsync();
        html.Should().Contain("data-testid=\"error-home-link\"").And.Contain("href=\"/portal\"").And.Contain("My account");
        html.Should().NotContain("Admin dashboard").And.NotContain("/admin/dashboard");
    }

    [Fact]
    public async Task Developer_error_page_links_to_the_developer_portal()
    {
        var c = await _f.DeveloperAsync();
        var html = await (await c.GetAsync("/Error?code=400")).Content.ReadAsStringAsync();
        html.Should().Contain("href=\"/dev\"").And.Contain("Developer Portal");
        html.Should().NotContain("Admin dashboard");
    }

    [Fact]
    public async Task Staff_error_page_still_links_to_the_admin_dashboard()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_f.Services, _f.Browser);
        var html = await (await owner.GetAsync("/Error?code=400")).Content.ReadAsStringAsync();
        html.Should().Contain("data-testid=\"error-home-link\"").And.Contain("Admin dashboard");
    }

    private static async Task<string> RenderExceptionPageAsync(ClaimsPrincipal user)
    {
        var ctx = new DefaultHttpContext { User = user };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await ErrorPageWriter.WriteAsync(ctx, 400, "One or more validation errors occurred.", "trace-1");
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static ClaimsPrincipal SignedIn(params string[] roles)
        => new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)).Prepend(new Claim(ClaimTypes.Name, "u")), "test"));

    [Fact]
    public async Task Exception_error_page_offers_each_audience_its_own_home()
    {
        var customer = await RenderExceptionPageAsync(SignedIn(RoleNames.Customer));
        customer.Should().Contain("href='/portal'").And.Contain("My account").And.NotContain("Admin dashboard");

        var developer = await RenderExceptionPageAsync(SignedIn(RoleNames.Developer));
        developer.Should().Contain("href='/dev'").And.NotContain("Admin dashboard");

        var staff = await RenderExceptionPageAsync(SignedIn(RoleNames.SalesStaff));
        staff.Should().Contain("href='/admin/dashboard'").And.Contain("Admin dashboard");

        var anonymous = await RenderExceptionPageAsync(new ClaimsPrincipal(new ClaimsIdentity()));
        anonymous.Should().Contain("href='/Account/Login'").And.NotContain("Admin dashboard");
    }
}
