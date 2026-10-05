using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests;

/// <summary>
/// Regression coverage for the intermittent blank-page bug (2026-09-09): when the global
/// per-IP rate limiter rejected a request it emitted a bare 429 with an EMPTY body, so the
/// browser rendered a completely blank page at the correct URL until the window reset.
/// These tests pin the fix: rejections must always carry a real response (styled HTML for
/// browser requests, JSON envelope for /api-style requests, Retry-After for both), and the
/// authenticated SignalR channel under /hubs must never consume the browsing budget.
/// </summary>
public class RateLimitingTests : IClassFixture<RateLimitingTests.RateLimitedFactory>, IDisposable
{
    /// <summary>
    /// Keeps the app's real rate-limiter pipeline (unlike the default factory which permits
    /// all) and shrinks it to 3 requests per 1-second window so rejections are cheap to
    /// trigger deterministically.
    /// </summary>
    public sealed class RateLimitedFactory : CustomWebApplicationFactory
    {
        protected override bool DisableRateLimiting => false;

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("RateLimiting:PermitLimit", "3");
            builder.UseSetting("RateLimiting:WindowSeconds", "1");
            base.ConfigureWebHost(builder);
        }
    }

    private readonly RateLimitedFactory _factory;
    private readonly HttpClient _client;

    public RateLimitingTests(RateLimitedFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Blocks until the limiter window has rolled over and requests succeed again, then waits
    /// out the rest of that window. The successful probe can land as the LAST permit of the
    /// current window (observed under suite-wide CPU contention), so without the trailing
    /// pause the test's own requests could still straddle an exhausted window. After the
    /// pause the current window is guaranteed untouched and fully budgeted.
    /// </summary>
    private async Task WaitForFreshWindowAsync()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var probe = await _client.GetAsync("/products");
            if (probe.StatusCode == HttpStatusCode.OK)
            {
                await Task.Delay(1150); // window is 1s (RateLimiting:WindowSeconds=1)
                return;
            }
            await Task.Delay(120);
        }
        throw new InvalidOperationException("Rate-limit window never reset - test environment broken.");
    }

    [Fact]
    public async Task OverLimit_BrowserRequest_Returns429HtmlErrorPageWithRetryAfter()
    {
        await WaitForFreshWindowAsync();

        // Burn the 3-permit budget (the probe above already consumed one in this window,
        // so at most two more pass before rejection; keep going until we are rejected).
        HttpResponseMessage rejected = null!;
        for (var i = 0; i < 10; i++)
        {
            var response = await _client.GetAsync("/admin/users");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
                break;
            }
            response.Dispose();
        }

        Assert.NotNull(rejected);
        // The whole point of the fix: the rejection body must NOT be empty.
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body), "429 response body must not be blank");
        Assert.Contains("text/html", rejected.Content.Headers.ContentType?.MediaType ?? string.Empty);
        Assert.Contains("Too many requests", body);
        Assert.Contains("Try again", body);            // recovery affordance, user-initiated
        Assert.True(rejected.Headers.RetryAfter is not null, "Retry-After header must be set");
    }

    [Fact]
    public async Task OverLimit_ApiStyleRequest_Returns429JsonEnvelope()
    {
        await WaitForFreshWindowAsync();

        HttpResponseMessage rejected = null!;
        for (var i = 0; i < 10; i++)
        {
            var attempt = new HttpRequestMessage(HttpMethod.Get, "/admin/notifications/unread-count");
            attempt.Headers.Accept.ParseAdd("application/json");
            var response = await _client.SendAsync(attempt);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
                break;
            }
            response.Dispose();
        }

        Assert.NotNull(rejected);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body), "429 API response body must not be blank");
        Assert.Equal("application/json", rejected.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("too.many.requests", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.True(rejected.Headers.RetryAfter is not null, "Retry-After header must be set");
    }

    [Fact]
    public async Task HubRequests_AreExempt_AndDoNotConsumeTheBrowsingBudget()
    {
        await WaitForFreshWindowAsync();

        // The SignalR negotiate endpoint sits under /hubs and must bypass the global limiter:
        // hammer it far past the permit limit, then confirm a normal page still loads.
        for (var i = 0; i < 8; i++)
        {
            using var negotiate = await _client.PostAsync(
                "/hubs/notifications/negotiate?negotiateVersion=1", content: null);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, negotiate.StatusCode);
        }

        using var page = await _client.GetAsync("/products");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task WithinLimit_NormalBrowsing_IsNeverRejected()
    {
        await WaitForFreshWindowAsync();

        // Two page loads stay inside the 3-permit window and must both render fully.
        using var first = await _client.GetAsync("/products");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Contains("<html", firstBody, StringComparison.OrdinalIgnoreCase);
    }
}
