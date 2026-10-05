using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Host.Controllers.Mvc.Dev;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>
/// A browser that drops a Developer Portal request (reload, double-click on Sign in, navigating
/// away while the dashboard is still loading) must end quietly as 499 inside the controller:
/// no OperationCanceledException escaping user code (the Visual Studio "Exception User-Unhandled"
/// break reported on the dashboard's service-fee query), no error page, no warning log.
/// Cancellations that are NOT the client going away still propagate.
/// </summary>
public class DevPortalClientAbortTests : IClassFixture<DevPortalFactory>
{
    private readonly DevPortalFactory _f;
    public DevPortalClientAbortTests(DevPortalFactory f) => _f = f;

    private sealed class Probe : DevControllerBase
    {
        public Probe(CancellationToken aborted) =>
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = aborted } };

        public Task<IActionResult> Run(Func<Task<IActionResult>> action) => UnlessClientAborted(action);
    }

    [Fact]
    public async Task Client_abort_during_the_action_becomes_a_quiet_499()
    {
        using var cts = new CancellationTokenSource();
        var probe = new Probe(cts.Token);

        var result = await probe.Run(async () =>
        {
            await Task.Yield();
            cts.Cancel();                              // the browser goes away mid-query…
            cts.Token.ThrowIfCancellationRequested();  // …and EF throws, exactly like line 146
            return new OkResult();
        });

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(499);
    }

    [Fact]
    public async Task TaskCanceledException_from_an_aborted_client_is_also_handled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await new Probe(cts.Token).Run(() => throw new TaskCanceledException());
        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(499);
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_client_leaving_still_propagates()
    {
        // e.g. an internal timeout: the request itself is still alive, so this is a real problem
        // and must reach the global handler / logs as before.
        var probe = new Probe(CancellationToken.None);
        var act = () => probe.Run(() => throw new OperationCanceledException("internal timeout"));
        await act.Should().ThrowAsync<OperationCanceledException>().WithMessage("internal timeout");
    }

    [Fact]
    public async Task Other_exceptions_and_normal_results_are_untouched()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // even with the client gone, only cancellations are swallowed
        var failing = () => new Probe(cts.Token).Run(() => throw new InvalidOperationException("boom"));
        await failing.Should().ThrowAsync<InvalidOperationException>();

        var ok = await new Probe(CancellationToken.None).Run(() => Task.FromResult<IActionResult>(new OkResult()));
        ok.Should().BeOfType<OkResult>();
    }

    [Theory]
    [InlineData("/dev")]
    [InlineData("/dev/api/status")]
    [InlineData("/dev/service-fees")]
    public async Task Whole_pipeline_aborted_dev_request_ends_as_499_without_an_error_page(string path)
    {
        var cookie = await DeveloperCookieAsync();

        // Sanity: the same request with a live connection renders normally.
        var live = await _f.Server.SendAsync(ctx => Prepare(ctx, path, cookie));
        live.Response.StatusCode.Should().Be(200, $"{path} renders for a signed-in developer");

        // The browser has already gone: every query sees a cancelled RequestAborted.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var aborted = await _f.Server.SendAsync(ctx =>
        {
            Prepare(ctx, path, cookie);
            ctx.RequestAborted = cts.Token;
        });
        aborted.Response.StatusCode.Should().Be(499, $"{path}: a dropped request is a quiet 499");
        aborted.Response.ContentType.Should().BeNull("no styled error page / JSON error body is written for a client that left");
    }

    [Fact]
    public async Task Normal_developer_sign_in_still_lands_on_a_rendered_dashboard()
    {
        var c = await _f.DeveloperAsync();
        var r = await c.GetAsync("/dev");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().Contain("data-dv-tiles");
    }

    private static void Prepare(HttpContext ctx, string path, string cookie)
    {
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        ctx.Request.Headers.Cookie = cookie;
        ctx.Request.Headers.Accept = path.Contains("/api/") ? "application/json" : "text/html";
    }

    /// <summary>Signs the seeded developer in without a cookie container and returns the Cookie header.</summary>
    private async Task<string> DeveloperCookieAsync()
    {
        var c = _f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var login = await c.GetAsync("/Account/Login");
        var jar = Cookies(login);
        var post = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = DevPortalFactory.DevEmail, ["Password"] = DevPortalFactory.DevPassword,
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await login.Content.ReadAsStringAsync())
            })
        };
        post.Headers.Add("Cookie", string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}")));
        var signedIn = await c.SendAsync(post);
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signedIn.Headers.Location!.ToString().Should().Be("/dev");
        foreach (var kv in Cookies(signedIn)) jar[kv.Key] = kv.Value;
        return string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static Dictionary<string, string> Cookies(HttpResponseMessage r) =>
        (r.Headers.TryGetValues("Set-Cookie", out var v) ? v : Array.Empty<string>())
            .Select(s => Regex.Match(s, "^([^=;]+)=([^;]*)"))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
}
