using System.Globalization;

namespace DeChimex.Application.Common;

/// <summary>
/// The configurable service fee: the portion of Paystack's transaction charge passed on
/// to the customer (Service Fee = Paystack Fee × percent / 100). Stored as the business
/// setting <see cref="Key"/> (default <see cref="DefaultPercent"/>, range
/// <see cref="MinPercent"/>–<see cref="MaxPercent"/>); 100 preserves the owner directive
/// of 2026-09-08 (full pass-through) until the owner reconfigures. The maximum of 100%
/// is a business-requirement bound: the fee is "a configurable portion of Paystack's
/// transaction cost", and a portion cannot exceed the cost being passed through.
/// </summary>
public static class ServiceFeeSettings
{
    public const string Key = "service_fee.percent";
    public const decimal DefaultPercent = 100m;
    public const decimal MinPercent = 0m;
    public const decimal MaxPercent = 100m;

    /// <summary>Read-side parse: a missing/blank value falls back to the default; garbage
    /// never flows into pricing (falls back to the default rather than throwing).</summary>
    public static decimal Parse(string? raw)
        => TryParse(raw, out var percent) ? percent : DefaultPercent;

    /// <summary>Write-side validation: rejects non-numeric, NaN/infinite, negative,
    /// over-maximum and over-precision (more than 2 decimal places) values.</summary>
    public static bool TryParse(string? raw, out decimal percent)
    {
        percent = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim().TrimEnd('%').Trim();
        if (text.Contains("NaN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Inf", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            return false;
        if (value is < MinPercent or > MaxPercent) return false;
        // Percentages are stored with at most 2 decimal places (e.g. 12.5).
        if (decimal.Round(value, 2) != value) return false;
        percent = value;
        return true;
    }

    /// <summary>Canonical display/storage form (no trailing zeros: "50", "12.5").</summary>
    public static string Format(decimal percent)
        => percent.ToString("0.##", CultureInfo.InvariantCulture);
}
