using System.Text;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Email;
using DeChimex.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

/// <summary>
/// Renders EVERY email template with realistic (and hostile) data: long names, markup
/// injection attempts, missing optional fields. Asserts the design-system invariants
/// (encoded values, no leftover placeholders, locked subjects, brand shell) and, when
/// EMAIL_PREVIEW_DIR is set (or by default under the temp dir), writes each rendered
/// email to disk for visual/desktop+mobile screenshot review.
/// </summary>
public class EmailTemplateRendererTests
{
    /// <summary>Realistic superset model — templates pick what they need.</summary>
    private static Dictionary<string, string> Superset() => new()
    {
        ["FirstName"] = "O'Neil <b>Attridge-Smith-Worthington the Third</b>",
        ["Token"] = "483920",
        ["Otp"] = "771204",
        ["VerificationLink"] = "https://dechimexprinthub.com/Account/VerifyEmail?userId=abc&token=t1",
        ["ResetLink"] = "https://dechimexprinthub.com/Account/ResetPassword?token=r1",
        ["OrderNumber"] = "DCX-2026-000123",
        ["Status"] = "In Production",
        ["Amount"] = "\u20a61,250,999.50",
        ["TrackingLink"] = "https://dechimexprinthub.com/portal/orders/track?orderNumber=DCX-2026-000123",
        ["WorkspaceLink"] = "https://dechimexprinthub.com/portal/orders/11111111-2222-3333-4444-555555555555/workspace",
        ["MessagePreview"] = "Thanks! <script>alert('x')</script> & \"quotes\": the teal boxes look great, but can we make the logo 2cm wider on the mugs and still hit Friday? Let me know!",
        ["Name"] = "Chukwuemeka <i>Okafor-Williams</i>",
        ["Email"] = "emeka@example.com",
        ["Phone"] = "+234 801 234 5678",
        ["Service"] = "Corporate branding packages",
        ["Subject"] = "Re: your enquiry about 200 branded flasks",
        ["Signature"] = "Ada - DE-CHIMEX PRINT HUB",
        ["RawBody"] = "<p style='margin:0 0 12px'>Hello there,</p><p style='margin:0 0 12px'>Your flasks quote is attached.</p>",
        ["RawMessage"] = "",
        ["UnsubscribeLink"] = "https://dechimexprinthub.com/unsubscribe/3f6b1c2e-9d47-4a58-8f21-7c5b0e9a4d63"
    };

    private static EmailTemplateRenderer New() =>
        new(Options.Create(new AppOptions()));

    private static string PreviewDir()
    {
        var dir = Environment.GetEnvironmentVariable("EMAIL_PREVIEW_DIR")
                  ?? Path.Combine(Path.GetTempPath(), "email-previews");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData(EmailTemplate.Welcome, "Welcome to DE-CHIMEX PRINT HUB")]
    [InlineData(EmailTemplate.EmailVerification, "Verify your DE-CHIMEX email")]
    [InlineData(EmailTemplate.ForgotPassword, "Reset your DE-CHIMEX password")]
    [InlineData(EmailTemplate.PasswordResetConfirmation, "Your DE-CHIMEX password was changed")]
    [InlineData(EmailTemplate.Otp, "Your DE-CHIMEX verification code")]
    [InlineData(EmailTemplate.OrderConfirmation, "Your order DCX-2026-000123 is confirmed")]
    [InlineData(EmailTemplate.OrderStatusUpdate, "Update on your order DCX-2026-000123")]
    [InlineData(EmailTemplate.PaymentReceipt, "Payment receipt for order DCX-2026-000123")]
    [InlineData(EmailTemplate.PaymentFailed, "Payment failed for DCX-2026-000123")]
    [InlineData(EmailTemplate.OrderRequestReceived, "We received your order request DCX-2026-000123")]
    [InlineData(EmailTemplate.OrderMessageNotification, "New message on your order DCX-2026-000123")]
    [InlineData(EmailTemplate.OrderQuoteReady, "Your quote for DCX-2026-000123 is ready")]
    [InlineData(EmailTemplate.DesignProofReady, "A design proof for DCX-2026-000123 is ready for approval")]
    [InlineData(EmailTemplate.DesignProofApproved, "Design approved for DCX-2026-000123")]
    [InlineData(EmailTemplate.ContactMessageReceived, "New contact form message from Chukwuemeka <i>Okafor-Williams</i>")]
    [InlineData(EmailTemplate.ContactReply, "Re: your enquiry about 200 branded flasks")]
    [InlineData(EmailTemplate.ProviderTestEmail, "DE-CHIMEX test email - delivery works")]
    public void Every_template_renders_in_the_brand_shell_with_encoded_values(EmailTemplate template, string expectedSubject)
    {
        var content = New().Render(template, Superset());

        // Subject (plain text, exact; test-locked fragments preserved)
        content.Subject.Should().Be(expectedSubject);

        // Brand shell + email-client essentials
        content.HtmlBody.Should().Contain("lang='en'");
        content.HtmlBody.Should().Contain("name='viewport'");
        content.HtmlBody.Should().Contain("Creativity with Ease.");
        content.HtmlBody.Should().Contain("hello@dechimexprinthub.com");
        content.HtmlBody.Should().Contain("role='presentation'");
        // Body typography is normalized for composed content (marketing bodies)
        content.HtmlBody.Should().Contain(".dc-body h1");
        content.HtmlBody.Should().Contain("class='dc-pad dc-body'");

        // No template placeholders may survive with the superset model
        content.HtmlBody.Should().NotContain("{{");
        content.PlainTextBody.Should().NotContain("{{");
        // Plain text keeps the user's literal characters (a "<b>" in a name is their
        // text, faithfully), but all TEMPLATE markup must be stripped.
        content.PlainTextBody.Should().NotContain("<p");
        content.PlainTextBody.Should().NotContain("<a ");
        content.PlainTextBody.Should().NotContain("<td");
        content.PlainTextBody.Should().NotContain("<table");
        content.PlainTextBody.Should().NotContain("\u2014");
        content.PlainTextBody.Should().NotContain("\u2013");
        content.PlainTextBody.Should().NotBeNullOrWhiteSpace();

        // Hostile values are encoded in HTML (never interpreted).
        content.HtmlBody.Should().NotContain("<b>Attridge");
        content.HtmlBody.Should().NotContain("<script>alert");

        // Currency convention
        content.HtmlBody.Should().NotContain("NGN {{");
        content.HtmlBody.Should().NotContain(">NGN ");

        var stem = $"{(int)template + 1:D2}-{template.ToString()}";
        File.WriteAllText(Path.Combine(PreviewDir(), stem + ".html"), content.HtmlBody, Encoding.UTF8);
        // Plain-text alternative: the same copy for clients that block HTML.
        File.WriteAllText(Path.Combine(PreviewDir(), stem + ".txt"), content.PlainTextBody, Encoding.UTF8);
    }

    [Fact]
    public void User_values_are_html_encoded_in_bodies_wherever_they_appear()
    {
        var welcome = New().Render(EmailTemplate.Welcome, Superset());
        welcome.HtmlBody.Should().Contain("&lt;b&gt;Attridge");
        // The apostrophe in the name survives literally in the plain-text fallback.
        welcome.PlainTextBody.Should().Contain("O'Neil");
        New().Render(EmailTemplate.ContactMessageReceived, Superset()).HtmlBody.Should().Contain("&lt;i&gt;Okafor-Williams&lt;/i&gt;");
        New().Render(EmailTemplate.OrderMessageNotification, Superset()).HtmlBody.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Message_preview_is_quoted_and_encoded_not_raw()
    {
        var html = New().Render(EmailTemplate.OrderMessageNotification, Superset()).HtmlBody;
        html.Should().Contain("&lt;script&gt;");
        html.Should().NotContain("<script>");
    }

    [Fact]
    public void Optional_blocks_disappear_when_values_are_missing()
    {
        var model = Superset();
        model["Phone"] = "";
        var html = New().Render(EmailTemplate.ContactMessageReceived, model).HtmlBody;
        html.Should().NotContain("Phone");
        html.Should().NotContain("{{#Phone}}");

        model["Phone"] = "+234 801 234 5678";
        html = New().Render(EmailTemplate.ContactMessageReceived, model).HtmlBody;
        html.Should().Contain("+234 801 234 5678");
    }

    [Fact]
    public void Raw_fragments_pass_through_unencoded()
    {
        var html = New().Render(EmailTemplate.ContactReply, Superset()).HtmlBody;
        html.Should().Contain("<p style='margin:0 0 12px'>Hello there,</p>");
    }

    [Fact]
    public void Ctas_are_bulletproof_buttons_with_link_fallback()
    {
        var html = New().Render(EmailTemplate.OrderConfirmation, Superset()).HtmlBody;
        html.Should().Contain("bgcolor='#4338CA'");   // --primary (app.css :root)
        html.Should().Contain("border-radius:12px");     // .cs-body .btn radius
        html.Should().Contain("Track your order");
        html.Should().Contain("Button not working? Copy this link:");
        html.Should().Contain("https://dechimexprinthub.com/portal/orders/track?orderNumber=DCX-2026-000123");
    }

    [Fact]
    public void Plain_text_fallback_keeps_every_action_link_reachable()
    {
        // Text-only clients cannot follow an <a>, so the fallback keeps the URL inline.
        var content = New().Render(EmailTemplate.OrderConfirmation, Superset());
        content.PlainTextBody.Should().Contain("Track your order");
        content.PlainTextBody.Should().Contain(
            "https://dechimexprinthub.com/portal/orders/track?orderNumber=DCX-2026-000123");
        var verify = New().Render(EmailTemplate.EmailVerification, Superset());
        verify.PlainTextBody.Should().Contain("https://dechimexprinthub.com/Account/VerifyEmail");
    }

    [Fact]
    public void Marketing_renders_with_a_mandatory_unsubscribe_footer()
    {
        var content = New().RenderMarketing("Hi {{FirstName}}, autumn sale is on",
            "<h2>Autumn sale</h2><p>20% off mugs.</p>", Superset());
        content.Subject.Should().Be("Hi O'Neil <b>Attridge-Smith-Worthington the Third</b>, autumn sale is on");
        content.HtmlBody.Should().Contain("Unsubscribe");
        content.HtmlBody.Should().Contain("https://dechimexprinthub.com/unsubscribe/3f6b1c2e-9d47-4a58-8f21-7c5b0e9a4d63");
        content.HtmlBody.Should().NotContain("{{");
        File.WriteAllText(Path.Combine(PreviewDir(), "98-Marketing.html"), content.HtmlBody, Encoding.UTF8);
        File.WriteAllText(Path.Combine(PreviewDir(), "98-Marketing.txt"), content.PlainTextBody, Encoding.UTF8);
    }

    [Fact]
    public void Missing_values_render_a_sensible_fallback_not_placeholders()
    {
        // Only the keys a call site would truly have — nothing else may leak as {{...}}.
        var minimal = new Dictionary<string, string>
        {
            ["FirstName"] = "Ada",
            ["OrderNumber"] = "DCX-2026-9",
            ["Amount"] = "",
            ["TrackingLink"] = "https://dechimexprinthub.com/portal/orders/track?orderNumber=DCX-2026-9"
        };
        var html = New().Render(EmailTemplate.OrderConfirmation, minimal).HtmlBody;
        html.Should().NotContain("{{");
        File.WriteAllText(Path.Combine(PreviewDir(), "99-OrderConfirmation-Minimal.html"), html, Encoding.UTF8);
        File.WriteAllText(Path.Combine(PreviewDir(), "99-OrderConfirmation-Minimal.txt"),
            New().Render(EmailTemplate.OrderConfirmation, minimal).PlainTextBody, Encoding.UTF8);
    }
}
