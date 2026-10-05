using System.Text.RegularExpressions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Email;
using DeChimex.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

/// <summary>
/// Drift guard for the email design system (EMAIL_DESIGN_STANDARD.md).
///
/// The email shell must be the app's own UI translated into email, so every colour
/// the renderer emits has to exist verbatim in the app stylesheets it mirrors
/// (wwwroot/css/app.css and wwwroot/css/customer.css). This suite re-reads those
/// files and fails when:
///   * an email colour appears that the app does not use (an invented token),
///   * the retired pre-redesign email palette creeps back in,
///   * the type stack is no longer the app's Inter stack.
/// Future templates therefore inherit the guard automatically: they render through
/// the same shell, and any new invented colour trips this test.
/// </summary>
public class EmailDesignSystemTests
{
    private static readonly string[] RetiredPreRedesignPalette =
    [
        "#7C3AED", // old email CTA violet
        "#F6F5FC", // old lavender page background
        "#3B3752", // old purple-grey body text
        "#1E1B2E", // old purple-ink heading
        "#6B6880", // old muted
        "#ECEAF5", // old hairline
        "#F3F0FF", // old accent-soft
        "#EC4899", // old gradient pink
        "#F97316"  // old gradient orange
    ];

    private static readonly Dictionary<string, string> Model = new()
    {
        ["FirstName"] = "Ada",
        ["Token"] = "483920",
        ["Otp"] = "771204",
        ["VerificationLink"] = "https://dechimexprinthub.com/Account/VerifyEmail?token=t1",
        ["ResetLink"] = "https://dechimexprinthub.com/Account/ResetPassword?token=r1",
        ["OrderNumber"] = "DCX-2026-000123",
        ["Status"] = "In Production",
        ["Amount"] = "\u20a61,250,999.50",
        ["TrackingLink"] = "https://dechimexprinthub.com/portal/orders/track?orderNumber=DCX-2026-000123",
        ["WorkspaceLink"] = "https://dechimexprinthub.com/portal/orders/1/workspace",
        ["MessagePreview"] = "Can we make the logo wider?",
        ["Name"] = "Ada Okafor",
        ["Email"] = "ada@example.com",
        ["Phone"] = "+234 801 234 5678",
        ["Service"] = "Corporate branding",
        ["Subject"] = "Your enquiry",
        ["Signature"] = "Ada, DE-CHIMEX PRINT HUB",
        ["RawBody"] = "<p>Hello there,</p>",
        ["RawMessage"] = "",
        ["UnsubscribeLink"] = "https://dechimexprinthub.com/unsubscribe/3f6b1c2e-9d47-4a58-8f21-7c5b0e9a4d63"
    };

    private static EmailTemplateRenderer New() => new(Options.Create(new AppOptions()));

    /// <summary>All rendered email HTML, one string per template plus marketing.</summary>
    private static List<string> AllRenderedHtml()
    {
        var renderer = New();
        var html = Enum.GetValues<EmailTemplate>()
            .Select(t => renderer.Render(t, Model).HtmlBody)
            .ToList();
        html.Add(renderer.RenderMarketing("Autumn sale", "<h2>Autumn sale</h2><p>20% off mugs.</p>", Model).HtmlBody);
        return html;
    }

    private static string? FindAppCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DeChimex.Host", "wwwroot", "css", "app.css");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static HashSet<string> AppPalette()
    {
        var appCss = FindAppCss();
        if (appCss is null) return [];
        var palette = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { appCss, Path.Combine(Path.GetDirectoryName(appCss)!, "customer.css") })
        {
            if (!File.Exists(file)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"#[0-9A-Fa-f]{6}\b"))
                palette.Add(m.Value.ToUpperInvariant());
        }
        return palette;
    }

    [Fact]
    public void Every_email_colour_comes_from_the_app_stylesheets()
    {
        var palette = AppPalette();
        if (palette.Count == 0) return; // stylesheets unavailable (packaged test run): skip

        var offenders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var html in AllRenderedHtml())
            foreach (Match m in Regex.Matches(html, @"#[0-9A-Fa-f]{6}\b"))
                if (!palette.Contains(m.Value.ToUpperInvariant()))
                    offenders.Add(m.Value.ToUpperInvariant());

        offenders.Should().BeEmpty(
            "the email must reuse the app palette (app.css :root / customer.css .cs-*). "
            + "Add the token to the app first, or use an existing app colour.");
    }

    [Fact]
    public void The_retired_pre_redesign_email_palette_is_gone()
    {
        foreach (var html in AllRenderedHtml())
            foreach (var dead in RetiredPreRedesignPalette)
                html.Should().NotContain(dead, $"the pre-redesign email palette ({dead}) was replaced by the app tokens");
    }

    [Fact]
    public void Typography_uses_the_app_inter_stack_and_the_logo_is_a_real_asset()
    {
        var html = New().Render(EmailTemplate.OrderConfirmation, Model).HtmlBody;

        html.Should().Contain("'Inter'");
        html.Should().NotContain("Segoe UI','Roboto");           // the retired email stack
        html.Should().Contain("name='color-scheme' content='light'"); // Gmail/Apple dark-mode handling
        html.Should().Contain("https://dechimexprinthub.com/img/logo.png");
        html.Should().Contain("alt=''");                          // decorative logo announced by the wordmark
        html.Should().Contain("max-width:600px");                 // email-safe fixed width
        html.Should().Contain("<table role='presentation'");      // layout, not divs
        html.Should().NotContain("display:flex");                 // Outlook cannot lay out flex
        html.Should().NotContain("position:absolute");
    }

    [Fact]
    public void Copy_stays_free_of_em_and_en_dashes()
    {
        foreach (var (template, html, plain) in AllRenderedTexts())
        {
            html.Should().NotContain("\u2014", $"{template} HTML copy must avoid em dashes").And
                .NotContain("\u2013", $"{template} HTML copy must avoid en dashes");
            plain.Should().NotContain("\u2014", $"{template} plain text must avoid em dashes").And
                .NotContain("\u2013", $"{template} plain text must avoid en dashes");
        }
    }

    private static List<(string Name, string Html, string Plain)> AllRenderedTexts()
    {
        var renderer = New();
        var list = Enum.GetValues<EmailTemplate>()
            .Select(t =>
            {
                var c = renderer.Render(t, Model);
                return (t.ToString(), c.HtmlBody, c.PlainTextBody);
            })
            .ToList();
        var m = renderer.RenderMarketing("Autumn sale", "<h2>Autumn sale</h2><p>20% off mugs.</p>", Model);
        list.Add(("Marketing", m.HtmlBody, m.PlainTextBody));
        return list;
    }
}
