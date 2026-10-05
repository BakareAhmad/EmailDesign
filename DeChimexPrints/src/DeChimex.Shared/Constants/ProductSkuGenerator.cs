using System.Text;
using System.Text.RegularExpressions;

namespace DeChimex.Shared.Constants;

/// <summary>
/// Single source of truth for the product SKU format and its generation.
///
/// A SKU is always exactly 9 characters and follows <c>NAME-PREFIX-SUFFIX</c>:
/// <code>PBC-7K4M2</code> = 3-char name prefix + '-' + 5-char unique code.
/// The prefix is derived from the product name so the SKU visibly relates to the
/// product; the random suffix guarantees practical uniqueness. The client UI mirrors
/// these rules (see product-sku.js) so suggestions shown to the user match what the
/// server accepts.
/// </summary>
public static class ProductSkuGenerator
{
    /// <summary>Characters allowed inside the SKU (prefix + suffix).</summary>
    public const string AllowedChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>Regex a saved SKU must match: 3 alnum + '-' + 5 alnum.</summary>
    public const string Pattern = "^[A-Z0-9]{3}-[A-Z0-9]{5}$";

    /// <summary>Total SKU length including the hyphen.</summary>
    public const int Length = 9;

    /// <summary>Prefix length (before the hyphen).</summary>
    public const int PrefixLength = 3;

    /// <summary>Suffix length (after the hyphen).</summary>
    public const int SuffixLength = 5;

    /// <summary>Fallback prefix when a name has no meaningful tokens.</summary>
    public const string FallbackPrefix = "PRD";

    private static readonly Regex _valid = new(Pattern, RegexOptions.Compiled);

    // Common words dropped when deriving the prefix so the meaningful words lead.
    private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "AN", "THE", "OF", "AND", "FOR", "WITH", "ON", "IN", "AT",
        "BY", "FROM", "TO", "OR", "AS", "IS", "ARE", "IT", "THIS", "THAT"
    };

    /// <summary>Whether a string is a well-formed 9-character SKU.</summary>
    public static bool IsValidFormat(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return false;
        var s = sku.Trim();
        return s.Length == Length && _valid.IsMatch(s);
    }

    /// <summary>
    /// Derives the 3-char uppercase prefix from a product name using the initials of
    /// up to three meaningful words, padding from the name if there are fewer words.
    /// Always returns exactly 3 upper alphanumeric characters.
    /// </summary>
    public static string BuildPrefix(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return FallbackPrefix;

        var upper = name.ToUpperInvariant();
        var tokens = Regex.Split(upper, "[^A-Z0-9]+")
            .Where(t => t.Length > 0)
            .ToList();

        var meaningful = tokens.Where(t => !_stopWords.Contains(t)).ToList();
        var use = meaningful.Count > 0 ? meaningful : tokens;
        if (use.Count == 0)
            return FallbackPrefix;

        var initials = new string(use.Select(t => t[0]).ToArray());
        if (initials.Length >= PrefixLength)
            return initials[..PrefixLength];

        // Pad to exactly 3 characters from the remaining name characters, skipping
        // letters already used, so e.g. "Mug" -> "MUG" and "T-Shirt" -> "TSH".
        var used = new HashSet<char>(initials);
        var sb = new StringBuilder(initials);
        foreach (var ch in string.Concat(use))
        {
            var c = char.ToUpperInvariant(ch);
            if (!char.IsLetterOrDigit(c) || used.Contains(c)) continue;
            used.Add(c);
            sb.Append(c);
            if (sb.Length == PrefixLength) break;
        }
        while (sb.Length < PrefixLength) sb.Append('X');
        return sb.ToString()[..PrefixLength];
    }

    /// <summary>Generates a random 5-character uppercase alphanumeric suffix.</summary>
    public static string GenerateSuffix()
    {
        var chars = new char[SuffixLength];
        for (var i = 0; i < SuffixLength; i++)
            chars[i] = AllowedChars[Random.Shared.Next(AllowedChars.Length)];
        return new string(chars);
    }

    /// <summary>Builds a random, well-formed SKU from a product name.</summary>
    public static string Suggest(string? name)
        => $"{BuildPrefix(name)}-{GenerateSuffix()}";

    /// <summary>Builds a well-formed SKU from a name, unique against the supplied existing SKUs.</summary>
    public static string Suggest(string? name, IEnumerable<string?> existingSkus)
    {
        var used = BuildUsedSet(existingSkus);
        for (var i = 0; i < 50; i++)
        {
            var candidate = Suggest(name);
            if (used.Add(candidate)) return candidate;
        }
        return $"{BuildPrefix(name)}-{GenerateSuffix()}";
    }

    /// <summary>
    /// Builds a well-formed SKU that keeps <paramref name="prefix"/> (falling back to the
    /// name-derived prefix when it isn't 3 clean chars) and a fresh suffix unique against
    /// <paramref name="existingSkus"/>. Used when a submitted SKU collides.
    /// </summary>
    public static string SuggestKeepingPrefix(string? name, string? prefix, IEnumerable<string?> existingSkus)
    {
        var used = BuildUsedSet(existingSkus);
        var cleanPrefix = NormalizePrefix(prefix);
        var effectivePrefix = cleanPrefix ?? BuildPrefix(name);
        for (var i = 0; i < 50; i++)
        {
            var candidate = $"{effectivePrefix}-{GenerateSuffix()}";
            if (used.Add(candidate)) return candidate;
        }
        return $"{effectivePrefix}-{GenerateSuffix()}";
    }

    private static HashSet<string> BuildUsedSet(IEnumerable<string?> existingSkus)
        => new(existingSkus.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim().ToUpperInvariant()), StringComparer.Ordinal);

    private static string? NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        var clean = new string(prefix.Trim().ToUpperInvariant()
            .Where(char.IsLetterOrDigit).Take(PrefixLength).ToArray());
        return clean.Length == PrefixLength ? clean : null;
    }
}
