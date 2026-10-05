using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Verifies the MVC (cookie auth) surface added in the API-to-MVC migration:
/// routing, the login flow, cookie issuance, permission/role claims over the cookie,
/// and Razor view rendering.
/// </summary>
public class MvcAccountTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MvcAccountTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    [Fact]
    public async Task Home_WithoutAuth_RedirectsToLogin()
    {
        var client = CreateBrowser();

        var response = await client.GetAsync("/Home");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
    }

    [Fact]
    public async Task Login_WithOwner_IssuesCookie_And_HomeRendersWithClaims()
    {
        var client = CreateBrowser();

        // 1. Load the login page to obtain the anti-forgery token.
        var loginPage = await client.GetAsync("/Account/Login");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = ExtractAntiForgeryToken(html);

        // 2. Submit credentials.
        var post = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "owner@dechimexprinthub.com",
                ["Password"] = "ChangeMe123!",
                ["__RequestVerificationToken"] = token
            }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // 3. The cookie should now authenticate the browser and render the view,
        //    including the role + permission claims written into the cookie.
        var home = await client.GetAsync("/Home");
        home.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await home.Content.ReadAsStringAsync();

        body.Should().Contain("Welcome");
        body.Should().Contain("Owner account");          // role claim -> IsInRole(RoleNames.Owner)
        body.Should().Contain("Your permissions");       // permission claims present
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShowsError_AndDoesNotAuthenticate()
    {
        var client = CreateBrowser();

        var loginPage = await client.GetAsync("/Account/Login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = ExtractAntiForgeryToken(html);

        var post = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "owner@dechimexprinthub.com",
                ["Password"] = "WrongPassword123!",
                ["__RequestVerificationToken"] = token
            }));
        post.StatusCode.Should().Be(HttpStatusCode.OK); // re-renders the form

        var body = await post.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid email or password.");

        // Still not authenticated: /Home must redirect back to login.
        var home = await client.GetAsync("/Home");
        home.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    private static string ExtractAntiForgeryToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*__RequestVerificationToken[^>]*>").Value;
        var value = Regex.Match(tag, "value=\"([^\"]+)\"");
        return value.Success
            ? value.Groups[1].Value
            : throw new Xunit.Sdk.XunitException("Anti-forgery token not found in login page.");
    }
}
