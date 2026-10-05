using System.Text.RegularExpressions;

namespace DeChimex.Shared.Constants;

/// <summary>
/// Sanitizes admin-composed marketing HTML before it is stored and emailed.
/// Defense-in-depth: broadcast authoring is already restricted to the
/// marketing.manage permission, but the content is still user input that ends
/// up in customer inboxes, so active/scripting content is stripped.
///
/// This is a deliberate, dependency-free blocklist sanitizer (no new package):
/// it removes the vectors that matter in an email context - script, embedded
/// frames/objects, external resource loading, interactive form controls (mail
/// clients break or strip them anyway; leftover inputs enable phishing UI),
/// inline event handlers and javascript:/data:text/html URLs. It is NOT a full
/// whitelist filter and is not suitable for sanitizing untrusted
/// user-generated content elsewhere.
/// </summary>
public static partial class MarketingHtmlSanitizer
{
    [GeneratedRegex(@"<script\b[^>]*>[\s\S]*?</script\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlocks();

    [GeneratedRegex(@"</?(script|iframe|object|embed|form|input|button|select|textarea|link|meta|base)\b[^>]*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousTags();

    [GeneratedRegex(@"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttributes();

    [GeneratedRegex(@"(href|src|action)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlAttributes();

    /// <summary>Strips scripts, active content, inline handlers and dangerous URL schemes.</summary>
    public static string Sanitize(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var result = ScriptBlocks().Replace(html, string.Empty);
        result = DangerousTags().Replace(result, string.Empty);
        result = EventAttributes().Replace(result, string.Empty);

        // Neutralize javascript:, vbscript: and data:text/html URLs inside href/src/action.
        result = UrlAttributes().Replace(result, m =>
        {
            var value = m.Groups[2].Value.Trim('"', '\'');
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme is "javascript" or "vbscript"
                || value.TrimStart().StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
            {
                return $"{m.Groups[1].Value}=\"#\"";
            }
            return m.Value;
        });

        return result;
    }
}
