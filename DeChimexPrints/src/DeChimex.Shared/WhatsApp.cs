namespace DeChimex.Shared;

/// <summary>
/// Canonical handling for the business.whatsapp setting. The setting stores a phone
/// number (digits, country code) only. The wa.me deep link is always generated here on
/// the consumer side so no view or admin screen hand-builds a URL. Accepts either a raw
/// number or a legacy wa.me/whatsapp URL and normalizes both to digits.
/// </summary>
public static class WhatsApp
{
    /// <summary>
    /// Extracts and normalizes a phone number from either a raw number
    /// ("+234 906 205 1934", "09062051934") or a legacy link
    /// ("https://wa.me/2349062051934?text=hi"). Returns digits only, or "" when none.
    /// Nigerian local numbers (0XXXXXXXXXX) are rewritten to 234XXXXXXXXXX.
    /// </summary>
    public static string NormalizeNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim();

        // Legacy deep link: take the number segment after wa.me/ and before any query.
        var marker = s.IndexOf("wa.me/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            s = s[(marker + "wa.me/".Length)..];
        var query = s.IndexOf('?');
        if (query >= 0)
            s = s[..query];

        var digits = new string(s.Where(char.IsDigit).ToArray());

        // Nigerian local 0XXXXXXXXXX (11 digits) to international 234XXXXXXXXXX.
        if (digits.Length == 11 && digits.StartsWith("0"))
            digits = "234" + digits[1..];

        return digits;
    }

    /// <summary>
    /// Builds the canonical wa.me link for the stored setting value. Returns null when
    /// the setting is blank, so callers can omit the button entirely.
    /// </summary>
    public static string? BuildUrl(string? raw)
    {
        var digits = NormalizeNumber(raw);
        return digits.Length == 0 ? null : $"https://wa.me/{digits}";
    }
}
