using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using DeChimex.Host.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Brand images (owner-supplied logo + hero art): served from wwwroot/img, wired into every layout,
/// responsive hero (true-alpha cut-outs: portrait art ≤720 px, landscape above, WebP first, RGBA PNG
/// fallback), the restored pre-refactor hero copy, and a text/mosaic fallback when a file is missing.
/// </summary>
public class BrandAssetsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public BrandAssetsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Home_renders_the_responsive_hero_picture_with_every_variant()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");
        html.Should().Contain("data-testid=\"home-hero\"");
        // <=720 px: the portrait cut-out (as in the pre-refactor 360-640 px references), WebP then PNG.
        const string mobileWebp = "<source media=\"(max-width: 720px)\" type=\"image/webp\" srcset=\"/img/hero-mobile.webp\" width=\"800\" height=\"1093\">";
        const string mobilePng = "<source media=\"(max-width: 720px)\" srcset=\"/img/hero-mobile.png\" width=\"800\" height=\"1093\">";
        html.Should().Contain(mobileWebp).And.Contain(mobilePng);
        html.Should().Contain("srcset=\"/img/hero-768.webp 768w, /img/hero.webp 1536w\"");
        // The fallback is a full-alpha PNG: a JPG cannot carry transparency and would paint a box on the page.
        html.Should().Contain("src=\"/img/hero.png\" srcset=\"/img/hero-768.png 768w, /img/hero.png 1536w\"");
        html.Should().NotContain("/img/hero.jpg").And.NotContain("/img/hero-768.jpg");
        html.Should().Contain($"sizes=\"{CustomerShellService.HeroSizes}\"");
        html.Should().Contain("width=\"1536\" height=\"1024\"", "intrinsic size reserves space (no layout shift)");
        html.Should().NotContain("class=\"hm-mosaic\"", "the product mosaic is only the fallback when the hero art is missing");
        // First matching <source> wins: portrait (media-gated) first, then landscape WebP, then the <img> PNG.
        var order = new[] { mobileWebp, mobilePng, "srcset=\"/img/hero-768.webp", "src=\"/img/hero.png\"" }
            .Select(x => html.IndexOf(x, StringComparison.Ordinal)).ToArray();
        order.Should().BeInAscendingOrder().And.NotContain(-1);
    }

    [Fact]
    public async Task Home_hero_restores_the_reference_copy_and_ctas_without_a_hero_search()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");
        var hero = html[html.IndexOf("<section class=\"hm-hero\"", StringComparison.Ordinal)..];
        hero = hero[..(hero.IndexOf("</section>", StringComparison.Ordinal) + 10)];
        var text = WebUtility.HtmlDecode(hero);
        text.Should().Contain("<span class=\"hm-kicker\">Printing • Branding • Promotional</span>");
        text.Should().Contain("<h1 class=\"hm-title\" id=\"heroTitle\"><span>Your brand,</span> <em>beautifully</em> <span>produced.</span></h1>");
        // Content rewrite (2026-10-04): the hero lead names the products and both ways to
        // order; the old "endless possibilities" tagline was removed as marketing filler.
        text.Should().Contain("Branded mugs, jerseys, flasks, corporate gifts and print work, produced by DE-CHIMEX PRINT HUB. Order ready-to-buy products online, or send your own items for custom branding.");
        text.Should().NotContain("endless possibilities");
        hero.Should().MatchRegex("<a class=\"btn btn-primary btn-lg\" href=\"/products\">Shop products <svg");
        hero.Should().MatchRegex("<a class=\"btn btn-outline btn-lg\" href=\"/services\">Explore services <svg");
        // Search lives in the header only (desktop box + mobile toggle), as in the reference.
        hero.Should().NotContain("<form").And.NotContain("role=\"search\"");
        System.Text.RegularExpressions.Regex.Matches(html, "role=\"search\"").Count.Should().Be(2, "header desktop box + mobile panel");
        html.Should().Contain("<form class=\"cs-search\" role=\"search\" action=\"/products\" method=\"get\">");
        html.Should().Contain("id=\"csSearchToggle\"").And.Contain("id=\"csMobileSearch\"");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/products")]
    [InlineData("/Account/Login")]
    [InlineData("/no-such-page-brand")]
    public async Task Customer_and_auth_layouts_show_the_logo_and_brand_icons(string path)
    {
        var resp = await _factory.CreateClient().GetAsync(path);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("<img src=\"/img/logo.png\"", "the header shows the real logo instead of the DC text badge");
        html.Should().Contain("<link rel=\"icon\" href=\"/favicon.ico\" sizes=\"any\">");
        html.Should().Contain("<link rel=\"apple-touch-icon\" href=\"/img/apple-touch-icon.png\">");
        html.Should().Contain("<link rel=\"manifest\" href=\"/site.webmanifest\">");
    }

    [Theory]
    [InlineData("/img/logo.png", "image/png", 256, 256)]
    [InlineData("/img/favicon-32.png", "image/png", 32, 32)]
    [InlineData("/img/apple-touch-icon.png", "image/png", 180, 180)]
    [InlineData("/img/icon-192.png", "image/png", 192, 192)]
    [InlineData("/img/icon-512.png", "image/png", 512, 512)]
    public async Task Png_brand_files_are_served_at_their_expected_size(string url, string type, int w, int h)
    {
        var resp = await _factory.CreateClient().GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be(type);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        // PNG IHDR: width/height are big-endian ints at offsets 16 and 20.
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)).Should().Be(w);
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)).Should().Be(h);
    }

    [Theory]
    [InlineData("/img/hero.png", "image/png")]
    [InlineData("/img/hero-768.png", "image/png")]
    [InlineData("/img/hero-mobile.png", "image/png")]
    [InlineData("/img/hero.webp", "image/webp")]
    [InlineData("/img/hero-mobile.webp", "image/webp")]
    [InlineData("/img/hero-768.webp", "image/webp")]
    [InlineData("/favicon.ico", "image/x-icon")]
    public async Task Other_brand_files_are_served_with_the_right_content_type(string url, string type)
    {
        var resp = await _factory.CreateClient().GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be(type);
        (await resp.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(500);
    }

    [Theory]
    [InlineData("/img/hero.webp", 1536, 1024)]
    [InlineData("/img/hero-768.webp", 768, 512)]
    [InlineData("/img/hero-mobile.webp", 800, 1093)]
    public async Task Hero_webp_is_a_transparent_cut_out_so_it_blends_into_the_page(string url, int w, int h)
    {
        var bytes = await _factory.CreateClient().GetByteArrayAsync(url);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4).Should().Be("WEBP");
        // Extended format: "VP8X" chunk, flags byte bit 4 = alpha; canvas size is 24-bit (value-1).
        System.Text.Encoding.ASCII.GetString(bytes, 12, 4).Should().Be("VP8X", "an alpha WebP uses the extended container");
        (bytes[20] & 0x10).Should().Be(0x10, "the hero carries an alpha channel (transparent background)");
        (1 + (bytes[24] | bytes[25] << 8 | bytes[26] << 16)).Should().Be(w);
        (1 + (bytes[27] | bytes[28] << 8 | bytes[29] << 16)).Should().Be(h);
        System.Text.Encoding.ASCII.GetString(bytes).Should().Contain("ALPH", "the alpha data chunk is present");
    }

    [Theory]
    [InlineData("/img/hero.png", 1536, 1024)]
    [InlineData("/img/hero-768.png", 768, 512)]
    [InlineData("/img/hero-mobile.png", 800, 1093)]
    public async Task Hero_png_fallbacks_are_true_alpha_cut_outs(string url, int w, int h)
    {
        var bytes = await _factory.CreateClient().GetByteArrayAsync(url);
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)).Should().Be(w);
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)).Should().Be(h);
        bytes[24].Should().Be(8, "8 bits per channel");
        bytes[25].Should().Be(6, "PNG colour type 6 = RGBA (true alpha, not a palette or flattened RGB)");
    }

    [Fact]
    public void Hero_card_styling_is_gone_so_the_art_blends_into_the_page_background()
    {
        var css = File.ReadAllText(Path.Combine(FindHostRoot(), "wwwroot", "css", "customer.css"));
        var rule = System.Text.RegularExpressions.Regex.Match(css, @"\.hm-hero-img\{([^}]*)\}").Groups[1].Value;
        rule.Should().Contain("background:transparent");
        rule.Should().NotContain("box-shadow").And.NotContain("border-radius").And.NotContain("#fff");
        // app.css gives every <img> a grey loading placeholder; the hero <img> must opt out or a faint box shows.
        System.Text.RegularExpressions.Regex.Match(css, @"\.hm-hero-img img\{([^}]*)\}").Groups[1].Value
            .Should().Contain("background:transparent");
    }

    private static string FindHostRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DeChimex.Host")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "DeChimex.Host");
    }

    [Fact]
    public async Task Web_manifest_lists_icons_that_exist()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/site.webmanifest");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be("DE-CHIMEX PRINT HUB");
        foreach (var icon in doc.RootElement.GetProperty("icons").EnumerateArray())
            (await client.GetAsync(icon.GetProperty("src").GetString())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Shell_returns_null_urls_when_brand_files_are_missing_so_views_fall_back()
    {
        var empty = Directory.CreateTempSubdirectory("brand-empty-");
        try
        {
            var shell = new CustomerShellService(null!, null!, null!, new Env(empty.FullName));
            shell.LogoUrl.Should().BeNull();
            shell.HeroDesktopUrl.Should().BeNull();
            shell.HeroMobileUrl.Should().BeNull();
            shell.HeroTabletUrl.Should().BeNull();
            shell.HeroDesktopWebpUrl.Should().BeNull();
            shell.HeroMobileWebpUrl.Should().BeNull();

            Directory.CreateDirectory(Path.Combine(empty.FullName, "img"));
            File.WriteAllBytes(Path.Combine(empty.FullName, "img", "logo.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(empty.FullName, "img", "hero.png"), new byte[] { 1 });
            // Existence is cached per full path for a minute; an equivalent root spelled differently re-checks now.
            var shell2 = new CustomerShellService(null!, null!, null!, new Env(Path.Combine(empty.FullName, ".")));
            shell2.LogoUrl.Should().Be("/img/logo.png");
            shell2.HeroDesktopUrl.Should().Be("/img/hero.png");
            shell2.HeroMobileUrl.Should().BeNull("only the files that exist are offered");
            shell2.HeroTabletUrl.Should().BeNull();
        }
        finally { empty.Delete(true); }
    }

    [Fact]
    public void Srcset_lists_only_existing_desktop_variants()
    {
        CustomerShellService.Srcset(null, null).Should().BeNull();
        CustomerShellService.Srcset("/img/hero-768.png", null).Should().Be("/img/hero-768.png 768w");
        CustomerShellService.Srcset(null, "/img/hero.png").Should().Be("/img/hero.png 1536w");
        CustomerShellService.Srcset("/a.webp", "/b.webp").Should().Be("/a.webp 768w, /b.webp 1536w");
    }

    private sealed class Env(string webRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = webRoot;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = webRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
