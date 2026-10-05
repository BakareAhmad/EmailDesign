using System.Net;
using FluentAssertions;
using Xunit;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>
/// The hand-rolled status-code re-execute (Program.cs) must route the re-executed request to the
/// error endpoint. It used to keep the endpoint the first pass had selected, so UseRouting skipped
/// matching and the ORIGINAL action ran a second time: an admin action returning NotFound() gave a
/// blank 404 document instead of the styled error page.
/// </summary>
public class StatusCodeReExecuteTests : IClassFixture<DevPortalFactory>
{
    private readonly DevPortalFactory _f;
    public StatusCodeReExecuteTests(DevPortalFactory f) => _f = f;

    [Theory]
    [InlineData("/admin/customers/00000000-0000-0000-0000-000000000123")]
    [InlineData("/admin/customers/00000000-0000-0000-0000-000000000123/edit")]
    public async Task Action_returning_NotFound_renders_the_styled_404_page_not_a_blank_document(string path)
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_f.Services, _f.Browser);
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.ParseAdd("text/html");
        var r = await owner.SendAsync(req);

        r.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        var html = await r.Content.ReadAsStringAsync();
        html.Should().NotBeNullOrWhiteSpace($"{path} must not be a blank page");
        html.Should().Contain("data-testid=\"error-home-link\"", "the styled error page (with its home link) is rendered")
            .And.Contain("Admin dashboard", "staff get their own home link");
    }

    [Fact]
    public async Task Browser_POST_failing_antiforgery_gets_a_styled_400_page_not_blank_and_not_405()
    {
        var owner = await TestSeed.LoginOwnerMvcAsync(_f.Services, _f.Browser);
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/customers/create")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["FullName"] = "No Token" })
        };
        req.Headers.Accept.ParseAdd("text/html");
        var r = await owner.SendAsync(req);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the anti-forgery rejection status is preserved");
        var html = await r.Content.ReadAsStringAsync();
        html.Should().NotBeNullOrWhiteSpace("a rejected form must not leave a blank page");
        html.Should().Contain("data-testid=\"error-home-link\"").And.Contain("Admin dashboard");
    }

    [Fact]
    public async Task Unknown_urls_and_explicit_error_codes_still_render_error_pages()
    {
        var c = _f.Browser();
        var nf = await c.GetAsync("/definitely-not-a-page");
        nf.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await nf.Content.ReadAsStringAsync()).Should().Contain("Page not found", "anonymous users get the styled 404 (Login link only)");

        var bad = await c.GetAsync("/Error?code=400");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("<!DOCTYPE html>").And.Contain("400");
    }
}
