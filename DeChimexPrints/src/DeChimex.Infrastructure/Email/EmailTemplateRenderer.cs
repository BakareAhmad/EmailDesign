using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Email;

/// <summary>
/// Renders branded HTML e-mail in ONE shared, email-client-safe shell
/// (table layout, inline styles + guarded media queries, presentation tables).
///
/// DESIGN CONTRACT (EMAIL_DESIGN_STANDARD.md, AI_HANDOFF §101): every token below is
/// the app's own token from wwwroot/css/app.css (:root) or customer.css (.cs-* rules),
/// never an email-only palette. The email is the DeChimex Hub UI translated into
/// email-safe markup: warm paper canvas, white cards with a warm hairline, ink-indigo
/// actions, slate text, status pills with a leading dot, alert panels with a hairline
/// and a tone label. A unit test (EmailDesignSystemTests) re-reads app.css and fails
/// if any token here drifts from the app, so future templates stay in the same
/// visual language automatically.
///
/// Templates use <c>{{Placeholder}}</c> substitution with two rules:
///   * values whose key starts with "Raw" are inserted as-is (trusted, caller-encoded
///     HTML fragments such as message paragraphs); every other value is HTML-encoded,
///   * <c>{{#Key}}…{{/Key}}</c> is an optional block that disappears when the value is
///     missing or blank, so optional fields never leave broken gaps.
///
/// Reusable components (Title/Lead/Rows/Total/Badge/Callout/Code/Alert/Button/Note)
/// keep every email visually consistent.
/// </summary>
public sealed class EmailTemplateRenderer(IOptions<AppOptions> app) : IEmailTemplateRenderer
{
    private readonly AppOptions _app = app.Value;

    // ------------------------------------------------------------------
    // Design tokens — mirrored 1:1 from the app (wwwroot/css/app.css :root
    // and customer.css .cs-* rules). Keep the CSS variable name in the comment.
    // ------------------------------------------------------------------
    private const string Ink = "#17161C";          // --n-900  headings
    private const string Strong = "#1E293B";       // --n-800  emphasised values
    private const string BodyText = "#475569";     // --n-600  body copy
    private const string Muted = "#64748B";        // --n-500  labels / fine print
    private const string Line = "#E7E4DC";         // --border hairlines between blocks
    private const string Hairline = "#F2F0EB";     // --n-100  separators inside tables
    private const string Card = "#FFFFFF";         // --n-0    card surface
    private const string Subtle = "#FCFBF9";       // --n-25   quiet surface (footer strip)
    private const string PageBg = "#F7F6F2";       // --canvas warm paper
    private const string Violet = "#4338CA";       // --primary  actions & links
    private const string VioletDeep = "#312E81";   // --primary-800 deep ink indigo
    private const string AccentSoft = "#EEF2FF";   // --primary-50  tinted panels
    private const string AccentLine = "#E0E7FF";   // --primary-100
    private const string NeutralChip = "#F2F0EB";  // --n-100  neutral chip
    // Status tones: customer.css .cs-status / .cs-callout (pills, panels)
    private const string OkText = "#065F46"; private const string OkBg = "#ECFDF5";
    private const string WarnText = "#92400E"; private const string WarnBg = "#FFFBEB";
    private const string BadText = "#991B1B"; private const string BadBg = "#FEF2F2";
    private const string InfoText = "#3730A3"; private const string InfoBg = "#EEF2FF";
    // Alert tones: app.css .flash / .alert (tinted panel + border)
    private const string FlashOkLine = "#A7F3D0";
    private const string FlashWarnLine = "#FDE68A";
    private const string FlashBadLine = "#FECACA";
    private const string FlashInfoLine = "#BFDBFE";
    private const string FlashInfoBg = "#EFF6FF"; private const string FlashInfoText = "#1E40AF";
    private const string UrlFaint = "#94A3B8";     // link-fallback line, quieter than muted
    private const string FontStack = "'Inter',-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    public EmailContent Render(EmailTemplate template, IReadOnlyDictionary<string, string> model)
    {
        var (subject, preheader, bodyInner) = template switch
        {
            EmailTemplate.Welcome => (
                "Welcome to DE-CHIMEX PRINT HUB",
                "Your account is verified and ready.",
                Title("Welcome, {{FirstName}}")
                + Lead("Your email is verified and your DE-CHIMEX account is ready. Browse ready-to-buy products, or send your own items for custom printing and branding.")
                + Rows(("Account", "{{FirstName}}"), ("Status", "Verified"))
                // The welcome send carries FirstName only (UserEmailVerifiedEventHandler), so the
                // CTA targets the storefront itself instead of inventing a placeholder key.
                + Button("Start shopping", (_app.FrontendBaseUrl ?? string.Empty).TrimEnd('/'))
                + Note("Questions? Just reply to any email. A real person reads them.")),

            EmailTemplate.EmailVerification => (
                "Verify your DE-CHIMEX email",
                "Confirm your email address to activate your DE-CHIMEX account.",
                Title("Verify your email address")
                + Lead("Hi {{FirstName}}, please confirm this address to activate your account. Enter this code on the verification page:")
                + Code("{{Token}}", label: "Your verification code")
                + Alert("The code expires in 24 hours. You can also use the button below.", "info")
                + Button("Verify email address", "{{VerificationLink}}")),

            EmailTemplate.ForgotPassword => (
                "Reset your DE-CHIMEX password",
                "Reset your DE-CHIMEX PRINT HUB password. The link expires in 1 hour.",
                Title("Reset your password")
                + Lead("Hi {{FirstName}}, we received a request to reset the password on your DE-CHIMEX account.")
                + Button("Reset password", "{{ResetLink}}")
                + Alert("This link expires in 1 hour. If you didn't request it, you can ignore this email and your password stays unchanged.", "info")),

            EmailTemplate.PasswordResetConfirmation => (
                "Your DE-CHIMEX password was changed",
                "Your password was changed successfully.",
                Title("Your password was changed")
                + Lead("Hi {{FirstName}}, your DE-CHIMEX password has been changed successfully.")
                + Alert("If this wasn't you, contact support immediately at " + _app.SupportEmail + ".", "warn")),

            EmailTemplate.Otp => (
                "Your DE-CHIMEX verification code",
                "Your one-time DE-CHIMEX verification code.",
                Title("Your verification code")
                + Lead("Hi {{FirstName}}, use this one-time code to continue:")
                + Code("{{Otp}}", label: "One-time code")
                + Note("The code is short-lived. Never share it with anyone.")),

            EmailTemplate.OrderConfirmation => (
                "Your order {{OrderNumber}} is confirmed",
                "Order {{OrderNumber}} is confirmed and moving into production.",
                Title("Your order is confirmed")
                + Lead("Thank you, {{FirstName}}. We've received your payment and your order is now being prepared.")
                + Badge("Payment received", "ok")
                + Rows(("Order number", "{{OrderNumber}}"), ("Payment status", "Paid"))
                + Total("Amount paid", "{{Amount}}")
                + Lead("We'll email you at every status change. You can follow progress any time:")
                + Button("Track your order", "{{TrackingLink}}")),

            EmailTemplate.OrderStatusUpdate => (
                "Update on your order {{OrderNumber}}",
                "New status for order {{OrderNumber}}: {{Status}}.",
                Title("Order update")
                + Lead("Hi {{FirstName}}, your order has a new status.")
                + Badge("{{Status}}", "info")
                + Rows(("Order number", "{{OrderNumber}}"), ("Current status", "{{Status}}"))
                + Button("Track your order", "{{TrackingLink}}")),

            EmailTemplate.PaymentReceipt => (
                "Payment receipt for order {{OrderNumber}}",
                "We've received your payment of {{Amount}} for order {{OrderNumber}}.",
                Title("Payment receipt")
                + Lead("Hi {{FirstName}}, we've received your payment. Thank you.")
                + Badge("Paid", "ok")
                + Rows(("Order number", "{{OrderNumber}}"))
                + Total("Amount paid", "{{Amount}}")
                + Note("Keep this email as your receipt. A printable receipt is also available in your account.")
                + Button("View order", "{{TrackingLink}}")),

            EmailTemplate.PaymentFailed => (
                "Payment failed for {{OrderNumber}}",
                "Your payment for order {{OrderNumber}} didn't go through. No charge was recorded.",
                Title("Payment didn't go through")
                + Lead("Hi {{FirstName}}, we couldn't process your payment for order {{OrderNumber}}.")
                + Badge("Payment failed", "bad")
                + Rows(("Order number", "{{OrderNumber}}"), ("Amount attempted", "{{Amount}}"))
                + Alert("No charge was recorded. If your bank declined it, no money has left your account and you can safely try again.", "bad")
                + Button("Try payment again", "{{TrackingLink}}")
                + Note("Prefer another way to pay? Contact us at " + _app.SupportEmail + " and we'll help you complete the order.")),

            EmailTemplate.OrderRequestReceived => (
                "We received your order request {{OrderNumber}}",
                "Custom order request {{OrderNumber}} is in. Our team is reviewing it now.",
                Title("We've received your request")
                + Lead("Hi {{FirstName}}, thanks. Your custom order request has landed with our team.")
                + Badge("Under review", "info")
                + Rows(("Request number", "{{OrderNumber}}"))
                + Lead("We're checking the details now. You'll hear from us in the conversation as soon as we have a quote or a question.")
                + Button("Open your request", "{{WorkspaceLink}}")),

            EmailTemplate.OrderMessageNotification => (
                "New message on your order {{OrderNumber}}",
                "There's a new message in the conversation for order {{OrderNumber}}.",
                Title("New message on your order")
                + Lead("Hi {{FirstName}}, there's a new message about your order:")
                + Message("{{MessagePreview}}", from: "DE-CHIMEX team")
                + Rows(("Order number", "{{OrderNumber}}"))
                + Button("Open conversation", "{{WorkspaceLink}}")),

            EmailTemplate.OrderQuoteReady => (
                "Your quote for {{OrderNumber}} is ready",
                "Requirements and pricing for {{OrderNumber}} are finalized. Review and pay to start production.",
                Title("Your quote is ready")
                + Lead("Hi {{FirstName}}, we've finalized the requirements and pricing for your order.")
                + Badge("Quote ready", "ok")
                + Rows(("Request number", "{{OrderNumber}}"))
                + Lead("Review the quote and complete payment to move it into production.")
                + Button("Review quote and pay", "{{WorkspaceLink}}")),

            EmailTemplate.DesignProofReady => (
                "A design proof for {{OrderNumber}} is ready for approval",
                "Your design proof for {{OrderNumber}} is waiting for review.",
                Title("Design proof ready")
                + Lead("Hi {{FirstName}}, we've prepared a design proof for your order.")
                + Badge("Awaiting your approval", "warn")
                + Rows(("Request number", "{{OrderNumber}}"))
                + Alert("Nothing goes into production until you approve the design or ask for changes.", "warn")
                + Button("Review the design", "{{WorkspaceLink}}")),

            EmailTemplate.DesignProofApproved => (
                "Design approved for {{OrderNumber}}",
                "Design approval recorded for {{OrderNumber}}. Production can proceed.",
                Title("Design approved")
                + Lead("Hi {{FirstName}}, your design approval has been recorded. Production can now proceed.")
                + Badge("Approved", "ok")
                + Rows(("Request number", "{{OrderNumber}}"))
                + Button("View order", "{{WorkspaceLink}}")),

            EmailTemplate.ContactMessageReceived => (
                "New contact form message from {{Name}}",
                "New website enquiry from {{Name}}.",
                Title("New contact enquiry")
                + Badge("New enquiry", "info")
                + RowsOf(
                    Row("Name", "{{Name}}"),
                    Row("Email", "{{Email}}"),
                    Row("Interested in", "{{Service}}"),
                    "{{#Phone}}" + Row("Phone", "{{Phone}}", last: true) + "{{/Phone}}")
                + Panel("{{RawBody}}", label: "Message")
                + Note("Reply to this email to respond directly to {{Name}}.")),

            EmailTemplate.ContactReply => (
                "{{Subject}}",
                "A message from the DE-CHIMEX team.",
                Title("{{Subject}}")
                + "{{RawBody}}"
                + "{{#Signature}}" + Signature("{{Signature}}") + "{{/Signature}}"),

            EmailTemplate.ProviderTestEmail => (
                "DE-CHIMEX test email - delivery works",
                "Test email from your DE-CHIMEX PRINT HUB app.",
                Title("Test email")
                + Lead("This is a test email from your DE-CHIMEX PRINT HUB app. If you can read it, email delivery is working.")
                + Alert("Sent from the admin provider settings. No customer received this message.", "info")
                + "{{RawMessage}}"),

            _ => (
                "DE-CHIMEX PRINT HUB",
                "",
                Title("A message from DE-CHIMEX PRINT HUB")
                + "{{RawMessage}}")
        };

        subject = Substitute(subject, model, encode: false);   // subjects are plain text
        var pre = Substitute(preheader, model, encode: true);  // hidden div lives in HTML
        var inner = ApplyOptionalBlocks(bodyInner, model);
        inner = Substitute(inner, model, encode: true);
        var html = Substitute(Wrap(pre, inner, marketingFooter: null), model, encode: true);
        var plain = BuildPlainText(pre, inner, model);
        return new EmailContent(subject, html, plain);
    }

    // ------------------------------------------------------------------
    // Reusable components (all return email-safe inline-styled HTML).
    // Each one is the app component named in the comment, translated.
    // ------------------------------------------------------------------

    /// <summary>Page heading — customer.css .cs-page-head h1 (24px / 800 / tight).</summary>
    private static string Title(string text) =>
        $"<h1 style='margin:0 0 12px;font-size:24px;line-height:1.2;font-weight:800;color:{Ink};letter-spacing:-.02em'>{text}</h1>";

    /// <summary>Supporting paragraph — customer.css .cs-page-head p (15px / muted).</summary>
    private static string Lead(string text) =>
        $"<p style='margin:0 0 16px;font-size:15px;line-height:1.6;color:{BodyText}'>{text}</p>";

    /// <summary>Fine print — .field-hint / .ck-sum-hint (12.5px / muted).</summary>
    private static string Note(string text) =>
        $"<p style='margin:16px 0 0;font-size:12.5px;line-height:1.6;color:{Muted}'>{text}</p>";

    /// <summary>
    /// Label/value rows — the workspace summary table (.ws-page .table td:
    /// 14px cell text with a --n-100 hairline, values right-aligned and tabular).
    /// </summary>
    private static string Rows(params (string Label, string Value)[] rows)
        => RowsOf(rows.Select(r => Row(r.Label, r.Value)).ToArray());

    private static string RowsOf(params string[] rowHtml)
    {
        var sb = new StringBuilder();
        sb.Append("<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='width:100%;border-collapse:collapse;margin:4px 0 18px'>");
        sb.Append(string.Concat(rowHtml));
        sb.Append("</table>");
        return sb.ToString();
    }

    private static string Row(string label, string value, bool last = false) =>
        "<tr>" +
        $"<td align='left' valign='middle' style='padding:11px 0;font-size:13px;color:{Muted};{(last ? "" : $"border-bottom:1px solid {Hairline};")}vertical-align:top'>{label}</td>" +
        $"<td align='right' valign='middle' style='padding:11px 0 11px 16px;font-size:14px;font-weight:600;color:{Strong};text-align:right;font-variant-numeric:tabular-nums;word-break:break-word;{(last ? "" : $"border-bottom:1px solid {Hairline};")}'>{value}</td>" +
        "</tr>";

    /// <summary>
    /// Amount emphasis — checkout summary "You will pay" row (customer.css
    /// .ck-sum-row.is-pay): --primary-50 panel, 12px radius, deep indigo figures.
    /// </summary>
    private static string Total(string label, string value) =>
        $"<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='width:100%;border-collapse:separate;margin:2px 0 18px'>" +
        $"<tr><td bgcolor='{AccentSoft}' style='background-color:{AccentSoft};border-radius:12px;padding:12px 14px'>" +
        $"<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'><tr>" +
        $"<td align='left' style='font-size:14px;font-weight:700;color:{VioletDeep};vertical-align:middle'>{label}</td>" +
        $"<td align='right' style='font-size:19px;font-weight:800;color:{VioletDeep};white-space:nowrap;font-variant-numeric:tabular-nums;vertical-align:middle'>{value}</td>" +
        "</tr></table></td></tr></table>";

    /// <summary>
    /// Status pill — customer.css .cs-status: 999px pill, 12.5px / 700, tinted tone,
    /// with a leading dot so the state never depends on colour alone.
    /// </summary>
    private static string Badge(string text, string tone)
    {
        var (fg, bg) = tone switch
        {
            "ok" => (OkText, OkBg),
            "bad" => (BadText, BadBg),
            "warn" => (WarnText, WarnBg),
            "neutral" => (Strong, NeutralChip),
            _ => (InfoText, InfoBg)
        };
        return $"<p style='margin:0 0 14px'><span style='display:inline-block;padding:5px 12px;border-radius:999px;background-color:{bg};color:{fg};font-size:12.5px;font-weight:700;line-height:1.5'>" +
               $"<span style='display:inline-block;width:7px;height:7px;border-radius:50%;background-color:{fg};margin-right:6px;vertical-align:middle'>&nbsp;</span>{text}</span></p>";
    }

    /// <summary>
    /// Tinted information panel — customer.css .cs-callout (14px radius, tone tint,
    /// bold block title, no side rule). Used for contact-form messages and notes.
    /// </summary>
    private static string Panel(string html, string? label = null)
    {
        var labelHtml = label is null ? "" :
            $"<p style='margin:0 0 4px;font-size:12.5px;font-weight:800;color:{InfoText}'>{label}</p>";
        return $"<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='width:100%;border-collapse:separate;margin:0 0 18px'>" +
               $"<tr><td style='background-color:{InfoBg};border-radius:14px;padding:14px 16px;font-size:14.5px;line-height:1.6;color:{InfoText}'>{labelHtml}{html}</td></tr></table>";
    }

    /// <summary>
    /// Message excerpt — the workspace conversation bubble (customer.css
    /// .chat-bubble.theirs: white bubble, warm hairline, 18px radius, 6px tail).
    /// </summary>
    private static string Message(string html, string? from = null)
    {
        var head = from is null ? "" :
            $"<p style='margin:0 0 4px;font-size:11.5px;font-weight:600;color:{Muted}'>{from}</p>";
        return $"<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='width:100%;border-collapse:separate;margin:0 0 18px'>" +
               $"<tr><td width='88%' style='border:1px solid {Line};border-radius:18px 18px 18px 6px;padding:12px 16px;background-color:{Card}'>" +
               $"{head}<div style='font-size:15px;line-height:1.6;color:{Strong}'>&ldquo;{html}&rdquo;</div></td>" +
               "<td width='12%'>&nbsp;</td></tr></table>";
    }

    /// <summary>
    /// Verification code — the app's token input language (customer.css
    /// .cs-body .input: white, --border, 12px radius) with the code set large.
    /// </summary>
    private static string Code(string token, string? label = null)
    {
        var labelHtml = label is null ? "" :
            $"<p style='margin:0 0 8px;font-size:12.5px;font-weight:600;color:{Muted}'>{label}</p>";
        return $"<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='width:100%;border-collapse:separate;margin:0 0 16px'>" +
               $"<tr><td style='border:1px solid {Line};border-radius:12px;background-color:{Card};padding:16px'>" +
               $"{labelHtml}<p style='margin:0;font-size:30px;line-height:1.2;font-weight:800;letter-spacing:8px;color:{Ink};text-align:center;font-variant-numeric:tabular-nums'>{token}</p>" +
               "</td></tr></table>";
    }

    /// <summary>
    /// Tone notice — app.css .flash / .alert (tinted panel + matching hairline + label,
    /// never colour alone).
    /// </summary>
    private static string Alert(string text, string tone)
    {
        var (fg, bg, line, label) = tone switch
        {
            "bad" => (BadText, BadBg, FlashBadLine, "Important"),
            "warn" => (WarnText, WarnBg, FlashWarnLine, "Security"),
            "ok" => (OkText, OkBg, FlashOkLine, "Done"),
            _ => (FlashInfoText, FlashInfoBg, FlashInfoLine, "Note")
        };
        return $"<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='width:100%;border-collapse:separate;margin:0 0 18px'>" +
               $"<tr><td style='background-color:{bg};border:1px solid {line};border-radius:8px;padding:11px 14px;font-size:13.5px;line-height:1.55;color:{fg}'>" +
               $"<strong style='font-weight:800'>{label}:</strong> {text}</td></tr></table>";
    }

    /// <summary>
    /// Signature block — a hairline then the sender line (app.css .card-head divider),
    /// standing in for a sign-off without punctuation tricks.
    /// </summary>
    private static string Signature(string html) =>
        $"<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='width:100%;margin:24px 0 0'>" +
        $"<tr><td style='border-top:1px solid {Hairline};padding-top:14px;font-size:13px;color:{Muted}'>{html}</td></tr></table>";

    /// <summary>
    /// Bulletproof CTA — the app's primary large button (.btn-primary .btn-lg on the
    /// customer shell: --primary, 12px radius, 15px / 700) plus a fine-print URL
    /// fallback for clients that strip or block the anchor.
    /// </summary>
    private static string Button(string text, string url) =>
        $@"<table role='presentation' cellpadding='0' cellspacing='0' border='0' class='dc-btn-wrap' style='margin:6px 0 4px'>
<tr><td align='center' bgcolor='{Violet}' class='dc-btn-wrap' style='border-radius:12px'>
<a class='dc-btn' href='{url}' style='display:inline-block;padding:13px 26px;font-size:15px;font-weight:700;line-height:1.2;color:#FFFFFF;text-decoration:none;border-radius:12px;background-color:{Violet};font-family:{FontStack}' target='_blank'>{text}</a>
</td></tr></table>
<p style='margin:10px 0 18px;font-size:12px;line-height:1.55;color:{UrlFaint}'>Button not working? Copy this link: <span style='color:{BodyText};word-break:break-all'>{url}</span></p>";

    // ------------------------------------------------------------------
    // Shell: app top bar -> content card -> app slim footer bar
    // ------------------------------------------------------------------

    private string Wrap(string preheader, string inner, string? marketingFooter)
    {
        var brand = WebUtility.HtmlEncode(_app.Name);
        var support = WebUtility.HtmlEncode(_app.SupportEmail);
        var site = WebUtility.HtmlEncode((_app.FrontendBaseUrl ?? string.Empty).TrimEnd('/'));
        var marketing = marketingFooter ?? "";
        return $@"<!DOCTYPE html>
<html lang='en'><head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<meta name='x-apple-disable-message-reformatting'>
<meta name='format-detection' content='telephone=no,address=no,email=no'>
<meta name='color-scheme' content='light'>
<meta name='supported-color-schemes' content='light'>
<title>{brand}</title>
<style>
  :root {{ color-scheme: light; }}
  .dc-body h1 {{ font-size:24px; line-height:1.2; margin:0 0 12px; font-weight:800; color:{Ink}; letter-spacing:-.02em; }}
  .dc-body h2 {{ font-size:18px; line-height:1.3; margin:18px 0 8px; font-weight:800; color:{Ink}; letter-spacing:-.01em; }}
  .dc-body h3 {{ font-size:15px; line-height:1.4; margin:14px 0 6px; font-weight:700; color:{Ink}; }}
  .dc-body p {{ margin:0 0 12px; }}
  .dc-body ul, .dc-body ol {{ margin:0 0 12px; padding-left:22px; }}
  .dc-body li {{ margin:0 0 6px; }}
  .dc-body a {{ color:{Violet}; }}
  .dc-body img {{ max-width:100%; height:auto; border:0; outline:none; text-decoration:none; }}
  .dc-body strong {{ color:{Ink}; }}
  @media only screen and (max-width:620px) {{
    .dc-container {{ width:100% !important; border-radius:0 !important; }}
    .dc-pad {{ padding-left:20px !important; padding-right:20px !important; }}
    .dc-body h1 {{ font-size:21px !important; }}
    .dc-btn {{ display:block !important; width:100% !important; box-sizing:border-box !important; text-align:center !important; }}
    table.dc-btn-wrap, table.dc-btn-wrap td {{ width:100% !important; }}
  }}
</style>
</head>
<body style='margin:0;padding:0;background-color:{PageBg};font-family:{FontStack};color:{BodyText};-webkit-font-smoothing:antialiased'>
<div style='display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;line-height:1px;font-size:1px;mso-hide:all'>{preheader}&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;&zwnj;</div>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='width:100%;background-color:{PageBg}'>
<tr><td align='center' style='padding:24px 12px'>
<table role='presentation' class='dc-container' width='600' cellpadding='0' cellspacing='0' border='0' style='width:600px;max-width:600px;background-color:{Card};border:1px solid {Line};border-radius:18px;border-collapse:separate'>
  <tr><td class='dc-pad' style='padding:18px 24px 16px;border-bottom:1px solid {Line}'>
    <table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'><tr>
      <td width='40' valign='middle' style='width:40px'>
        <a href='{site}' style='text-decoration:none'>
        <table role='presentation' cellpadding='0' cellspacing='0' border='0'><tr>
          <td width='40' height='40' align='center' valign='middle' bgcolor='{VioletDeep}' style='width:40px;height:40px;border-radius:11px;background-color:{VioletDeep}'>
            <img src='{site}/img/logo.png' width='34' height='34' alt='' style='display:block;width:34px;height:34px;border:0;border-radius:8px;background-color:{Card}'>
          </td>
        </tr></table>
        </a>
      </td>
      <td valign='middle' style='padding-left:10px'>
        <span style='display:block;font-size:15px;font-weight:800;color:{Ink};letter-spacing:-.01em;line-height:1.2'>DE-CHIMEX</span>
        <span style='display:block;font-size:9.5px;font-weight:700;letter-spacing:2.4px;color:{Violet};line-height:1.4'>PRINT HUB</span>
      </td>
    </tr></table>
  </td></tr>
  <tr><td class='dc-pad dc-body' style='padding:26px 24px 28px'>
{inner}
  </td></tr>
  <tr><td class='dc-pad' style='padding:16px 24px 18px;border-top:1px solid {Line};background-color:{Subtle};border-radius:0 0 18px 18px'>
    <p style='margin:0 0 8px;font-size:12.5px;line-height:1.6;color:{Muted}'>Creativity with Ease. Custom printing, branded merchandise and personalised gifts, ordered online and produced with care.</p>
    <p style='margin:0 0 6px;font-size:12.5px;line-height:1.6;color:{Muted}'>
      <a href='{site}/privacy' style='color:{BodyText};text-decoration:underline'>Privacy</a> &nbsp;·&nbsp;
      <a href='{site}/terms' style='color:{BodyText};text-decoration:underline'>Terms</a> &nbsp;·&nbsp;
      <a href='{site}/contact' style='color:{BodyText};text-decoration:underline'>Contact</a> &nbsp;·&nbsp;
      <a href='mailto:{support}' style='color:{BodyText};text-decoration:underline'>{support}</a>
    </p>
    <p style='margin:0;font-size:12px;line-height:1.6;color:{Muted}'>&copy; {DateTime.UtcNow.Year} {brand}. All rights reserved. Secure payments by Paystack &middot; Prices in NGN</p>
{marketing}
  </td></tr>
</table>
</td></tr>
</table>
</body></html>";
    }

    // ------------------------------------------------------------------
    // Marketing (admin broadcasts) — contract unchanged
    // ------------------------------------------------------------------

    /// <summary>
    /// MARKETING email rendering (broadcasts). Separate entry point from Render() by design:
    /// free-form sanitized admin HTML inside the same brand shell, ALWAYS with an
    /// unsubscribe footer - transactional templates can never route through here and
    /// broadcasts can never reuse transactional templates.
    /// </summary>
    public EmailContent RenderMarketing(string subject, string htmlContent, IReadOnlyDictionary<string, string> model)
    {
        subject = Substitute(subject.Trim(), model, encode: false);
        var inner = Substitute(htmlContent, model, encode: true);

        // Unsubscribe footer is appended server-side so a broadcast author cannot omit it.
        var marketingFooter =
            $"    <p style='margin:10px 0 0;font-size:12px;line-height:1.6;color:{Muted}'>" +
            "You received this email because you are a registered DE-CHIMEX customer. " +
            $"<a href='{{{{UnsubscribeLink}}}}' style='color:{Violet};text-decoration:underline'>Unsubscribe</a> from marketing emails.</p>";

        var html = Substitute(Wrap("", inner, marketingFooter), model, encode: true);
        var plain = BuildPlainText("", inner, model, marketingFooter);
        return new EmailContent(subject, html, plain);
    }

    // ------------------------------------------------------------------
    // Substitution / encoding / optional blocks
    // ------------------------------------------------------------------

    /// <summary>
    /// Replaces {{Key}} with model values. Values are HTML-encoded unless the key
    /// starts with "Raw" (trusted, caller-encoded HTML fragments). When encoding,
    /// values are also attribute-safe for the single-quoted href='…' attributes.
    /// </summary>
    private static string Substitute(string template, IReadOnlyDictionary<string, string> model, bool encode)
    {
        foreach (var (key, value) in model)
        {
            var raw = key.StartsWith("Raw", StringComparison.Ordinal);
            var insert = (!encode || raw) ? value : WebUtility.HtmlEncode(value);
            template = template.Replace("{{" + key + "}}", insert);
        }
        return template;
    }

    /// <summary>Removes {{#Key}}…{{/Key}} blocks whose value is missing or blank.</summary>
    private static string ApplyOptionalBlocks(string template, IReadOnlyDictionary<string, string> model)
        => Regex.Replace(template, @"\{\{#(\w+)\}\}(.*?)\{\{/\1\}\}", m =>
            model.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrWhiteSpace(v)
                ? m.Groups[2].Value
                : "", RegexOptions.Singleline);

    /// <summary>Readable text fallback: block tags become newlines, entities decoded.</summary>
    private static string BuildPlainText(string preheader, string htmlInner, IReadOnlyDictionary<string, string> model, string footer = "")
    {
        var text = Substitute(htmlInner + footer, model, encode: false);
        text = ApplyOptionalBlocks(text, model);
        // Links keep their target in the text part so it stays usable without HTML.
        text = Regex.Replace(text, @"<a\b[^>]*href='([^']+)'[^>]*>(.*?)</a>", m =>
            WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", "")) + " " + m.Groups[1].Value, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|h1|h2|h3|tr|div|table|td)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|hr)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t\u00a0]+", " ");
        text = Regex.Replace(text, @" ?\n ?", "\n");
        text = Regex.Replace(text, @"\n\s+", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        var head = string.IsNullOrWhiteSpace(preheader) ? "" : preheader.Trim() + "\n\n";
        return head + text;
    }
}
