using System.Net;
using DeChimex.Application.Common.Interfaces;
using DeChimex.IntegrationTests.Fakes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Admin Settings must expose email delivery health: provider-side blocks (like Brevo's
/// authorised-IP 401s or an exhausted daily quota) previously failed silently while the
/// UI claimed success.
/// </summary>
public class EmailHealthSettingsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EmailHealthSettingsTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> OwnerBrowserAsync() =>
        await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);

    [Fact]
    public async Task SettingsPage_ShowsEmailHealthCard()
    {
        var owner = await OwnerBrowserAsync();
        var page = await owner.GetAsync("/admin/settings");
        page.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await page.Content.ReadAsStringAsync();
        body.Should().Contain("Email delivery");
        body.Should().Contain("Connected to Brevo", "the fake health check reports healthy");
        body.Should().Contain("Send test email to my address");
    }

    [Fact]
    public async Task SettingsPage_WhenProviderBlocked_ShowsReasonAndFixLink()
    {
        var health = (FakeEmailHealthCheck)_factory.Services.GetRequiredService<IEmailHealthCheck>();
        health.Result = new EmailHealthResult(false, "Brevo is blocking this server's IP address",
            "We have detected you are using an unrecognised IP address 1.2.3.4.",
            "https://app.brevo.com/security/authorised_ips");
        try
        {
            var owner = await OwnerBrowserAsync();
            var body = await (await owner.GetAsync("/admin/settings")).Content.ReadAsStringAsync();
            body.Should().Contain("Brevo is blocking this server");
            body.Should().Contain("unrecognised IP address 1.2.3.4", "the exact provider message must be shown");
            body.Should().Contain("https://app.brevo.com/security/authorised_ips");
        }
        finally
        {
            health.Result = new EmailHealthResult(true, "Connected to Brevo (test fake)", "Fake provider status for tests.");
        }
    }

    [Fact]
    public async Task EmailTest_SendsToSignedInAdmin_AndReportsResult()
    {
        var owner = await OwnerBrowserAsync();
        var page = await (await owner.GetAsync("/admin/settings")).Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(page);

        var post = await owner.PostAsync("/admin/settings/email-test",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Assert the recipient BEFORE loading the settings page again - its status check
        // calls CheckAsync(null) and would overwrite the recorded recipient.
        var health = (FakeEmailHealthCheck)_factory.Services.GetRequiredService<IEmailHealthCheck>();
        health.LastTestRecipient.Should().Be(TestSeed.OwnerEmail, "the test email goes to the signed-in admin");

        var after = await (await owner.GetAsync("/admin/settings")).Content.ReadAsStringAsync();
        after.Should().Contain("Connected to Brevo", "the healthy test result is flashed");
    }
}
