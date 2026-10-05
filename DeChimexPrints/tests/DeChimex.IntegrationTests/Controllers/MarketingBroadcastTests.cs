using System.Net;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// End-to-end coverage for the marketing broadcast system: the admin compose →
/// queue flow (including the duplicate-send guard) and the customer-facing
/// unsubscribe landing. Sending itself is done by the background worker and is
/// intentionally not exercised here - these tests verify queueing, consent and
/// the domain guards. Assertions are written to be robust even if the test
/// host's background poller picks a queued broadcast up mid-test (statuses may
/// advance Queued→Sending→Completed, but the recipient snapshot and the
/// Draft-only re-send guard are invariant).
/// </summary>
public class MarketingBroadcastTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MarketingBroadcastTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
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
        var token = ExtractToken(html);

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
        {
            // First boot: the seeded owner must change the bootstrap password.
            var page = await client.GetAsync("/admin/security/password");
            var changeHtml = await page.Content.ReadAsStringAsync();
            var changeToken = ExtractToken(changeHtml);
            var changed = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["CurrentPassword"] = password,
                    ["NewPassword"] = "Owner!Pass9",
                    ["ConfirmPassword"] = "Owner!Pass9",
                    ["__RequestVerificationToken"] = changeToken
                }));
            changed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }
        return true;
    }

    private static string ExtractToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*__RequestVerificationToken[^>]*>").Value;
        return Regex.Match(tag, "value=\"([^\"]+)\"").Groups[1].Value;
    }

    private static async Task<(HttpStatusCode Status, string Html)> GetPageAsync(HttpClient client, string path)
    {
        var page = await client.GetAsync(path);
        return (page.StatusCode, await page.Content.ReadAsStringAsync());
    }

    private static async Task<string> HarvestAntiForgeryTokenAsync(HttpClient client, string path)
    {
        var (status, html) = await GetPageAsync(client, path);
        status.Should().Be(HttpStatusCode.OK, $"page {path} should render");
        var token = ExtractToken(html);
        token.Should().NotBeNullOrWhiteSpace($"an antiforgery token should be present on {path}");
        return token;
    }

    // -------------------------------------------------------------------
    // Unsubscribe landing (anonymous, token-based)
    // -------------------------------------------------------------------

    [Fact]
    public async Task Unsubscribe_UnknownToken_ShowsInvalidLink()
    {
        var client = CreateBrowser();
        var response = await client.GetAsync($"/unsubscribe/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Link not recognised");
    }

    [Fact]
    public async Task Unsubscribe_ValidToken_ShowsConfirmThenOptsCustomerOut()
    {
        var email = $"unsub{Guid.NewGuid():N}@test.local";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email);

        Guid token;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var customer = await db.Customers.SingleAsync(c => c.Email!.Value == email);
            customer.MarketingSubscribed.Should().BeTrue("customers opt in by default");
            token = customer.UnsubscribeToken;
        }

        var client = CreateBrowser();

        // GET shows the confirmation form (no opt-out yet).
        var confirmPage = await client.GetAsync($"/unsubscribe/{token}");
        confirmPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await confirmPage.Content.ReadAsStringAsync();
        html.Should().Contain("Unsubscribe from marketing emails?");

        // POST confirms the opt-out.
        var afToken = ExtractToken(html);
        var post = await client.PostAsync($"/unsubscribe/{token}", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = afToken }));
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        (await post.Content.ReadAsStringAsync()).Should().ContainAny("You&#x27;re unsubscribed", "You're unsubscribed");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var customer = await db.Customers.SingleAsync(c => c.Email!.Value == email);
            customer.MarketingSubscribed.Should().BeFalse("confirming the unsubscribe opts the customer out");
        }
    }

    [Fact]
    public async Task Unsubscribe_WithoutAuth_RedirectsNowhere_AnonymousAllowed()
    {
        // The unsubscribe link must work from an email client: no login, no cookie.
        var client = CreateBrowser();
        var response = await client.GetAsync($"/unsubscribe/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -------------------------------------------------------------------
    // Admin compose → queue → duplicate-send guard
    // -------------------------------------------------------------------

    [Fact]
    public async Task Compose_AsOwner_SanitizesContentAndCreatesDraft()
    {
        var client = await LoginAsOwnerAsync();
        var afToken = await HarvestAntiForgeryTokenAsync(client, "/admin/marketing/compose");

        var response = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Subject"] = "Autumn promo test",
                ["HtmlContent"] = "<h1>Autumn sale!</h1><script>alert('x')</script>"
                    + "<p onclick=\"steal()\">Big discounts</p><a href=\"javascript:steal()\">Shop</a>",
                ["__RequestVerificationToken"] = afToken
            }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith("/admin/marketing/");

        var id = Guid.Parse(location.Split('/').Last());
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var broadcast = await db.MarketingBroadcasts.AsNoTracking().SingleAsync(b => b.Id == id);
        broadcast.Subject.Should().Be("Autumn promo test");
        broadcast.Status.Should().Be(MarketingBroadcastStatus.Draft);
        broadcast.HtmlContent.Should().Contain("<h1>Autumn sale!</h1>")
            .And.NotContainAny("script", "onclick", "javascript:", "steal");
    }

    [Fact]
    public async Task Compose_MissingSubject_RedrawsWithError()
    {
        var client = await LoginAsOwnerAsync();
        var afToken = await HarvestAntiForgeryTokenAsync(client, "/admin/marketing/compose");

        var response = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Subject"] = "",
                ["HtmlContent"] = "<p>content</p>",
                ["__RequestVerificationToken"] = afToken
            }));
        response.StatusCode.Should().Be(HttpStatusCode.OK); // re-rendered form
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("A subject is required.");
    }

    [Fact]
    public async Task Send_AsOwner_SnapshotsRecipientsOnce_AndSecondSendIsRejected()
    {
        // Two registered customers = the expected audience. Walk-in guests and
        // staff are excluded structurally (staff are Users, guests have no email).
        var email1 = $"mark1{Guid.NewGuid():N}@test.local";
        var email2 = $"mark2{Guid.NewGuid():N}@test.local";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email1);
        await TestSeed.RegisterCustomerAsync(_factory.Services, email2);

        var client = await LoginAsOwnerAsync();

        // 1. Create the draft.
        var composeToken = await HarvestAntiForgeryTokenAsync(client, "/admin/marketing/compose");
        var compose = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Subject"] = "Duplicate-guard test",
                ["HtmlContent"] = "<p>Only one of me should ever arrive.</p>",
                ["__RequestVerificationToken"] = composeToken
            }));
        compose.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var id = Guid.Parse(compose.Headers.Location!.ToString().Split('/').Last());

        // 2. First Send: queues the broadcast and snapshots recipients.
        var detailsToken = await HarvestAntiForgeryTokenAsync(client, $"/admin/marketing/{id}");
        var firstSend = await client.PostAsync($"/admin/marketing/{id}/send",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = detailsToken,
                ["id"] = id.ToString()
            }));
        firstSend.StatusCode.Should().Be(HttpStatusCode.Redirect);
        firstSend.Headers.Location!.ToString().Should().Be($"/admin/marketing/{id}");

        var (status, recipientCount, queuedAt) = await ReadBroadcastAsync(id);
        status.Should().BeOneOf(
            new[] { MarketingBroadcastStatus.Queued, MarketingBroadcastStatus.Sending, MarketingBroadcastStatus.Completed },
            "send queues; the background poller may already have advanced it");
        queuedAt.Should().NotBeNull();
        recipientCount.Should().Be(2, "both registered customers are eligible recipients");

        // 3. Second Send (double click / refresh): the Draft-only domain guard
        //    must reject it instead of duplicating the send.
        var retryToken = await HarvestAntiForgeryTokenAsync(client, $"/admin/marketing/{id}");
        var secondSend = await client.PostAsync($"/admin/marketing/{id}/send",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = retryToken,
                ["id"] = id.ToString()
            }));
        secondSend.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await (await client.GetAsync(secondSend.Headers.Location!)).Content.ReadAsStringAsync())
            .Should().Contain("already been queued");

        // No duplicate audience was snapshotted.
        var (statusAfter, recipientCountAfter, _) = await ReadBroadcastAsync(id);
        recipientCountAfter.Should().Be(2, "a rejected second send must not add recipients");
        statusAfter.Should().BeOneOf(
            new[] { MarketingBroadcastStatus.Queued, MarketingBroadcastStatus.Sending, MarketingBroadcastStatus.Completed });
    }

    [Fact]
    public async Task MarketingPage_WithoutMarketingPermission_ForbiddenForCustomer()
    {
        var email = $"noperm{Guid.NewGuid():N}@test.local";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email);
        await TestSeed.VerifyCustomerAsync(_factory.Services, email);

        var client = CreateBrowser();
        var loginPage = await client.GetAsync("/Account/Login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = ExtractToken(html);
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = TestSeed.DefaultPassword,
                ["__RequestVerificationToken"] = token
            }));

        var response = await client.GetAsync("/admin/marketing");
        // MVC requests redirect to the access-denied page (403 is only for /api paths).
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the Customer role must not carry marketing permissions");
        response.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");
    }

    private async Task<(Domain.Enums.MarketingBroadcastStatus Status, int RecipientCount, DateTime? QueuedAtUtc)>
        ReadBroadcastAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var broadcast = await db.MarketingBroadcasts.AsNoTracking()
            .Include(b => b.Recipients)
            .SingleAsync(b => b.Id == id);
        return (broadcast.Status, broadcast.Recipients.Count, broadcast.QueuedAtUtc);
    }

    // -------------------------------------------------------------------
    // Preview encoding (Details page)
    // -------------------------------------------------------------------

    [Fact]
    public async Task BroadcastPreview_RendersSanitizedHtmlWithoutDoubleEncoding()
    {
        // Regression: the Details preview rendered
        // srcdoc="@WebUtility.HtmlEncode(Model.HtmlContent)" INSIDE a Razor attribute,
        // so Razor encoded the pre-encoded content a second time and the iframe showed
        // literal entity soup (&lt;h2&gt;...) instead of the email. Content is already
        // sanitized on save (MarketingHtmlSanitizer) and the iframe is fully sandboxed,
        // so the raw value rendered through Razor's single attribute-encoding is correct.
        var client = await LoginAsOwnerAsync();
        var afToken = await HarvestAntiForgeryTokenAsync(client, "/admin/marketing/compose");

        var create = await client.PostAsync("/admin/marketing/compose", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Subject"] = "Encoding preview test",
                ["HtmlContent"] = "<h2>It's \"Mid-Year\" & Mega Sale</h2><p>Enjoy 50% + free delivery</p>"
                    + "<script>alert('x')</script>",
                ["__RequestVerificationToken"] = afToken
            }));
        create.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var id = Guid.Parse(create.Headers.Location!.ToString().Split('/').Last());

        var details = await client.GetAsync($"/admin/marketing/{id}");
        details.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await details.Content.ReadAsStringAsync();

        // Single-encoded HTML inside the srcdoc attribute (Razor escapes ' as &#x27;
        // and + as &#x2B;), the iframe stays sandboxed, and the script was stripped at save.
        html.Should().Contain("srcdoc=\"&lt;h2&gt;It&#x27;s &quot;Mid-Year&quot; &amp; Mega Sale&lt;/h2&gt;&lt;p&gt;Enjoy 50% &#x2B; free delivery&lt;/p&gt;\"");
        html.Should().Contain("<iframe srcdoc=");
        html.Should().Contain("sandbox");
        html.Should().NotContain("&amp;lt;").And.NotContain("&amp;#x27;").And.NotContain("&amp;quot;");
        html.Should().NotContain("alert");
    }
}
