namespace DeChimex.Shared.Constants;

/// <summary>
/// Curated palette of soft, low-impact accent colors used for service icons.
/// A single source of truth shared by the service auto-assignment logic and by
/// every view that renders a service icon, so the app's visual language stays
/// consistent (no ad-hoc random RGB/HSL values anywhere).
///
/// The palette is built around the existing DeChimex brand system (an indigo
/// primary #4338CA with a slate/canvas neutral scale). The hues below are the
/// mid-lightest "soft accent" tier of each family: clearly distinguishable from
/// one another but intentionally subtle - not dark, not bright/neon, not highly
/// saturated - so an icon reads as a gentle accent rather than a dominant UI
/// element. Use them with a light tint background (e.g. <code>color + "22"</code>,
/// ~13% alpha) so the icon sits on a very light, premium pastel chip.
/// </summary>
public static class ServiceIconPalette
{
    /// <summary>
    /// The curated palette, ordered by hue family and aligned with the DeChimex
    /// brand: indigo (primary), blue, sky/cyan, teal, emerald, violet, purple,
    /// pink, orange and amber.
    /// </summary>
    public static readonly IReadOnlyList<string> Colors = new[]
    {
        "#818CF8", // indigo  (brand primary family)
        "#60A5FA", // blue
        "#38BDF8", // sky     (cyan family)
        "#2DD4BF", // teal
        "#34D399", // emerald
        "#A78BFA", // violet
        "#C084FC", // purple
        "#F9A8D4", // pink     (soft, pastel)
        "#FDBA74", // orange   (soft, pastel)
        "#FCD34D", // amber    (soft, pastel)
    };

    /// <summary>
    /// Default accent used when a service has not had a color assigned yet (e.g.
    /// legacy data or a null IconColor), so nothing renders with an empty/black
    /// icon. Deliberately the indigo brand accent.
    /// </summary>
    public const string DefaultColor = "#818CF8";

    /// <summary>
    /// Selects a palette color automatically. When <paramref name="existingColors"/>
    /// is supplied, the color with the lowest current usage is preferred (random
    /// among ties). This makes newly-created services spread sensibly across the
    /// palette and avoids repeatedly landing on the exact same accent, while still
    /// picking from the curated set only. With no existing colors, a random palette
    /// color is returned.
    /// </summary>
    /// <param name="existingColors">Icon colors already in use (may contain nulls or values outside the palette, which are ignored).</param>
    public static string Pick(IReadOnlyCollection<string?>? existingColors = null)
    {
        if (existingColors is { Count: > 0 })
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Colors) counts[c] = 0;
            foreach (var c in existingColors)
                if (!string.IsNullOrWhiteSpace(c) && counts.ContainsKey(c))
                    counts[c]++;

            var lowest = counts.Values.Min();
            var candidates = Colors.Where(c => counts[c] == lowest).ToList();
            return candidates[Random.Shared.Next(candidates.Count)];
        }

        return Colors[Random.Shared.Next(Colors.Count)];
    }

    /// <summary>Whether a color belongs to the curated palette.</summary>
    public static bool IsPaletteColor(string? color)
        => !string.IsNullOrWhiteSpace(color) &&
           Colors.Contains(color, StringComparer.OrdinalIgnoreCase);
}
