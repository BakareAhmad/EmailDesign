using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Html;

namespace DeChimex.Host.DevPortal;

/// <summary>Small HTML helpers shared by the Developer Portal views (all output HTML-encoded).</summary>
public static class DevUi
{
    public static IHtmlContent Status(string? status)
    {
        var s = status ?? "Unknown";
        var cls = s switch
        {
            DevStatus.Healthy or "Configured" or "Success" or "Succeeded" or "Processed" or "Applied" or "Enabled" or "Live" or "Test" => "ok",
            DevStatus.Warning or "Disabled" or "Not set" or "Skipped" or "Duplicate" or "Ignored" or "Pending" or "Running" or "Development" => "warn",
            DevStatus.Degraded or "Stale" or "Failing" or "Unreachable" => "degr",
            DevStatus.Unavailable or "Missing" or "Invalid" or "Failure" or "Failed" or "Exception" or "Rejected" or "AmountMismatch" or "CurrencyMismatch" or "UnknownReference" => "bad",
            _ => "neutral"
        };
        return new HtmlString($"<span class=\"dv-badge dv-{cls}\">{Enc(s)}</span>");
    }

    public static IHtmlContent Severity(string? severity)
    {
        var s = severity ?? "Information";
        var cls = s switch { "Critical" => "crit", "Error" => "bad", "Warning" => "warn", _ => "info" };
        return new HtmlString($"<span class=\"dv-sev dv-sev-{cls}\">{Enc(s)}</span>");
    }

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var d = DateTime.UtcNow - utc.Value;
        if (d.TotalSeconds < 0) return "just now";
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 48) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public static string Utc(DateTime? utc) => utc is null ? "—" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    public static string Naira(decimal v) => "₦" + v.ToString("#,0.00", CultureInfo.InvariantCulture);
    public static string Num(long v) => v.ToString("#,0", CultureInfo.InvariantCulture);
    public static string Pct(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    public static string Duration(TimeSpan t) => t.TotalMinutes < 1 ? $"{Math.Max(0, (int)t.TotalSeconds)}s" : t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
    public static string Ms(double? ms) => ms is null ? "—" : ms.Value >= 1000 ? $"{ms.Value / 1000:0.0} s" : $"{ms.Value:0} ms";

    /// <summary>Builds a query string preserving current filters while overriding some values.</summary>
    public static string Query(Microsoft.AspNetCore.Http.IQueryCollection current, params (string Key, string? Value)[] overrides)
    {
        var dict = current.ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides)
        {
            if (string.IsNullOrEmpty(v)) dict.Remove(k); else dict[k] = v;
        }
        return dict.Count == 0 ? "" : "?" + string.Join("&", dict.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
