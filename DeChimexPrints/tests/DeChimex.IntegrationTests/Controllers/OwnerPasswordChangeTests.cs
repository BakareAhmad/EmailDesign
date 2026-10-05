using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// ISS-20: the seeded bootstrap owner must change the default password on first
/// login. The rest of /admin is locked until the change completes, and the new
/// password works from the next sign-in while the bootstrap one stops working.
/// </summary>
public class OwnerPasswordChangeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OwnerPasswordChangeTests(CustomWebApplicationFactory factory) => _factory = factory;

    private const string OwnerEmail = "owner@dechimexprinthub.com";
    private const string BootstrapPassword = "ChangeMe123!";
    private const string ChangedPassword = "Owner!Pass9";

    [Fact]
    public async Task First_Owner_Login_Forces_Password_Change_And_Unlocks_Admin()
    {
        var client = CreateBrowser();

        // 1. Bootstrap login must land on the forced change screen, not the dashboard.
        var (status, location) = await PostLoginAsync(client, BootstrapPassword);
        status.Should().Be(HttpStatusCode.Redirect);
        location.Should().Contain("/admin/security/password");

        // 2. While the flag is set, other admin pages bounce back to the change screen.
        var bounced = await client.GetAsync("/admin/dashboard");
        bounced.StatusCode.Should().Be(HttpStatusCode.Redirect);
        bounced.Headers.Location!.ToString().Should().Contain("/admin/security/password");

        // 3. The change screen shows the security notice for forced mode.
        var page = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
        page.Should().Contain("Security notice");

        // 4. Complete the change.
        var changeToken = ExtractToken(page);
        var changed = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPassword"] = BootstrapPassword,
            ["NewPassword"] = ChangedPassword,
            ["ConfirmPassword"] = ChangedPassword,
            ["__RequestVerificationToken"] = changeToken
        }));
        changed.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // 5. The same session can now open the admin dashboard (claim cleared).
        var dashboard = await client.GetAsync("/admin/dashboard");
        dashboard.StatusCode.Should().Be(HttpStatusCode.OK);

        // 6. The bootstrap password no longer works on a fresh session.
        var stale = CreateBrowser();
        var (staleStatus, _) = await PostLoginAsync(stale, BootstrapPassword);
        staleStatus.Should().Be(HttpStatusCode.OK, "bootstrap password must be rejected after the change");

        // 7. The new password signs in straight to the dashboard (no forced screen).
        var fresh = CreateBrowser();
        var (freshStatus, freshLocation) = await PostLoginAsync(fresh, ChangedPassword);
        freshStatus.Should().Be(HttpStatusCode.Redirect);
        freshLocation.Should().NotContain("/admin/security/password");
        freshLocation.Should().Contain("/admin/dashboard");
    }

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<(HttpStatusCode, string)> PostLoginAsync(HttpClient client, string password)
    {
        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = ExtractToken(html);
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = OwnerEmail,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        return (response.StatusCode, response.Headers.Location?.ToString() ?? "");
    }

    private static string ExtractToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*__RequestVerificationToken[^>]*>").Value;
        var value = Regex.Match(tag, "value=\"([^\"]+)\"");
        return value.Success ? value.Groups[1].Value : throw new Xunit.Sdk.XunitException("Anti-forgery token not found.");
    }
}
