using System.Text.RegularExpressions;

namespace DeChimex.Shared.Diagnostics;

/// <summary>
/// Redacts secrets and personal data from technical text before it is persisted or shown in
/// the Developer Portal. Applied to every technical event message, stack trace, property and
/// integration error excerpt. Conservative by design: false positives (over-masking) are
/// acceptable, leaks are not.
/// </summary>
public static partial class SensitiveDataRedactor
{
    [GeneratedRegex(@"\b(sk|pk)_(test|live)_[A-Za-z0-9]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex PaystackKey();

    [GeneratedRegex(@"xkeysib-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex BrevoKey();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-\._~\+/=]{8,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)(""?(?<![A-Za-z_])(password|pwd|secret|secretkey|apikey|api-key|api_key|token|access_token|refresh_token|authorization_code|signature|otp|code|cvv|pin)""?\s*[:=]\s*""?)([^""&,;\s}]{1,200})")]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"(?i)(Password|Pwd)=[^;]*")]
    private static partial Regex ConnectionPassword();

    [GeneratedRegex(@"\b(?:\d[ \-]?){12,18}\d\b")]
    private static partial Regex LongDigits();

    [GeneratedRegex(@"([A-Za-z0-9._%+\-])([A-Za-z0-9._%+\-]*)@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})")]
    private static partial Regex Email();

    public static string? Redact(string? text, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = text;
        s = PaystackKey().Replace(s, m => m.Groups[1].Value + "_" + m.Groups[2].Value + "_[REDACTED]");
        s = BrevoKey().Replace(s, "xkeysib-[REDACTED]");
        s = Bearer().Replace(s, "Bearer [REDACTED]");
        s = ConnectionPassword().Replace(s, m => m.Groups[1].Value + "=[REDACTED]");
        s = KeyValueSecret().Replace(s, m => m.Groups[1].Value + "[REDACTED]");
        s = LongDigits().Replace(s, m => LooksLikeCardNumber(m.Value.Replace(" ", "").Replace("-", "")) ? "[REDACTED-NUMBER]" : m.Value);
        s = Email().Replace(s, m => MaskEmailParts(m.Groups[1].Value, m.Groups[3].Value));
        return s.Length > maxLength ? s[..maxLength] + "…[truncated]" : s;
    }

    /// <summary>
    /// True when a structured property NAME denotes a secret or credential (its value is then
    /// redacted wholesale, whatever it looks like): password, secret, token, api key,
    /// authorization, cookie, signature, otp, pin, cvv, card number.
    /// </summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return k.Contains("password") || k.Contains("passwd") || k == "pwd" || k.Contains("secret") || k.Contains("token")
            || k.Contains("apikey") || k.Contains("authorization") || k.Contains("cookie") || k.Contains("signature")
            || k is "otp" or "pin" or "cvv" or "cvc" || k.Contains("cardnumber") || k == "pan";
    }

    /// <summary>"ada.lovelace@example.com" -> "a***@example.com".</summary>
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return email;
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        return MaskEmailParts(email[..1], email[(at + 1)..]);
    }

    private static string MaskEmailParts(string first, string domain) => first + "***@" + domain;

    /// <summary>
    /// A 13–19 digit run is treated as a card number (PAN) only if it passes the Luhn check and is
    /// not a yyyyMMddHHmmss-style timestamp. Payment references such as PAY-20260925235026-… embed a
    /// timestamp; masking it would break search/correlation. No card scheme issues PANs starting
    /// with 20 (Mastercard's 2-series starts at 2221), so real cards are never let through by this.
    /// </summary>
    public static bool LooksLikeCardNumber(string digits)
    {
        if (digits.Length is < 13 or > 19) return false;
        if (digits.Length >= 14 && digits[0] == '2' && digits[1] == '0'
            && int.TryParse(digits.AsSpan(4, 2), out var month) && month is >= 1 and <= 12)
            return false;
        var sum = 0; var dbl = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return sum % 10 == 0;
    }
}
