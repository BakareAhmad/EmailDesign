using System.Text;

namespace DeChimex.Host;

/// <summary>
/// Inline SVG icon set for the customer-facing views (no icon font / CDN).
/// Stroke-based 24×24 glyphs; use <c>@Html.Raw(CustomerIcons.Svg("cart", 22))</c>.
/// </summary>
public static class CustomerIcons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = "M3 10.5 12 3l9 7.5|M5 9.5V21h14V9.5",
        ["shop"] = "M3 9l1.5-5h15L21 9|M3 9h18|M5 9v12h14V9|M9 21v-6h6v6",
        ["cart"] = "M2 3h3l2.6 12.4a2 2 0 0 0 2 1.6h8.8a2 2 0 0 0 2-1.6L22 7H6|M9 21a1 1 0 1 0 0-2 1 1 0 0 0 0 2z|M19 21a1 1 0 1 0 0-2 1 1 0 0 0 0 2z",
        ["orders"] = "M9 4h6l1 2h3v15H5V6h3z|M9 12h6|M9 16h4",
        ["user"] = "M20 21a8 8 0 1 0-16 0|M12 13a4 4 0 1 0 0-8 4 4 0 0 0 0 8z",
        ["search"] = "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z|m21 21-4.3-4.3",
        ["heart"] = "M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.6l-1-1a5.5 5.5 0 1 0-7.8 7.8l1 1L12 21l7.8-7.6 1-1a5.5 5.5 0 0 0 0-7.8z",
        ["bell"] = "M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9|M13.7 21a2 2 0 0 1-3.4 0",
        ["menu"] = "M3 6h18|M3 12h18|M3 18h18",
        ["close"] = "M18 6 6 18|m6 6 12 12",
        ["chev-right"] = "m9 18 6-6-6-6",
        ["chev-down"] = "m6 9 6 6 6-6",
        ["arrow-left"] = "M19 12H5|m12 19-7-7 7-7",
        ["arrow-right"] = "M5 12h14|m12 5 7 7-7 7",
        ["check"] = "M20 6 9 17l-5-5",
        ["check-circle"] = "M22 11.1V12a10 10 0 1 1-5.9-9.1|M22 4 12 14l-3-3",
        ["clock"] = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z|M12 6v6l4 2",
        ["truck"] = "M1 3h15v13H1z|M16 8h4l3 3v5h-7V8z|M5.5 21a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z|M18.5 21a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z",
        ["pin"] = "M20 10c0 6-8 12-8 12s-8-6-8-12a8 8 0 0 1 16 0z|M12 13a3 3 0 1 0 0-6 3 3 0 0 0 0 6z",
        ["card"] = "M2 5h20v14H2z|M2 10h20|M6 15h4",
        ["receipt"] = "M4 2h16v20l-3-2-2 2-2-2-2 2-2-2-3 2z|M8 7h8|M8 11h8|M8 15h5",
        ["shield"] = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z|m9 12 2 2 4-4",
        ["sparkle"] = "M12 3v4|M12 17v4|M3 12h4|M17 12h4|m5.6 5.6 2.8 2.8|m15.6 15.6 2.8 2.8|m5.6 18.4 2.8-2.8|m15.6 8.4 2.8-2.8",
        ["brush"] = "m9.1 14.9 4.2-4.2|M18.4 2.6a2.1 2.1 0 0 1 3 3L14 13l-4 1 1-4z|M7 17a3 3 0 0 0-4 3c0 1-1 2-1 2s3 0 4-1a3 3 0 0 0 1-4z",
        ["box"] = "M21 8v8a2 2 0 0 1-1 1.7l-7 4a2 2 0 0 1-2 0l-7-4A2 2 0 0 1 3 16V8a2 2 0 0 1 1-1.7l7-4a2 2 0 0 1 2 0l7 4A2 2 0 0 1 21 8z|M3.3 7 12 12l8.7-5|M12 22V12",
        ["chat"] = "M21 12a8 8 0 0 1-8 8H6l-3 3V12a8 8 0 0 1 8-8h2a8 8 0 0 1 8 8z",
        ["image"] = "M3 5h18v14H3z|M8 11a2 2 0 1 0 0-4 2 2 0 0 0 0 4z|m21 15-5-5-9 9",
        ["file"] = "M14 2H6v20h12V8z|M14 2v6h6|M8 13h8|M8 17h5",
        ["mail"] = "M3 5h18v14H3z|m3 7 9 6 9-6",
        ["phone"] = "M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1.9.4 1.8.7 2.7a2 2 0 0 1-.5 2.1L8 9.8a16 16 0 0 0 6 6l1.3-1.3a2 2 0 0 1 2.1-.4c.9.3 1.8.6 2.7.7a2 2 0 0 1 1.9 2.1z",
        // Official WhatsApp glyph (filled). Rendered with fill=currentColor, see Filled below.
        ["whatsapp"] = "M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347m-5.421 7.403h-.004a9.87 9.87 0 0 1-5.031-1.378l-.361-.214-3.741.982.998-3.648-.235-.374a9.86 9.86 0 0 1-1.51-5.26c.001-5.45 4.436-9.884 9.888-9.884 2.64 0 5.122 1.03 6.988 2.898a9.825 9.825 0 0 1 2.893 6.994c-.003 5.45-4.437 9.884-9.885 9.884m8.413-18.297A11.815 11.815 0 0 0 12.05 0C5.495 0 .16 5.335.157 11.892c0 2.096.547 4.142 1.588 5.945L.057 24l6.305-1.654a11.882 11.882 0 0 0 5.683 1.448h.005c6.554 0 11.89-5.335 11.893-11.893a11.821 11.821 0 0 0-3.48-8.413Z",
        ["instagram"] = "M3 8a5 5 0 0 1 5-5h8a5 5 0 0 1 5 5v8a5 5 0 0 1-5 5H8a5 5 0 0 1-5-5z|M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8z|M17.5 6.5h.01",
        ["tiktok"] = "M14 3v11a3.5 3.5 0 1 1-3.5-3.5|M14 3c.5 3 2.5 5 5.5 5.5",
        ["share"] = "M4 12v8h16v-8|M12 3v13|m7 8 5-5 5 5",
        ["zoom"] = "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z|m21 21-4.3-4.3|M11 8v6|M8 11h6",
        ["plus"] = "M12 5v14|M5 12h14",
        ["minus"] = "M5 12h14",
        ["trash"] = "M3 6h18|M8 6V4h8v2|M6 6l1 14h10l1-14",
        ["filter"] = "M3 5h18|M6 12h12|M10 19h4",
        ["star"] = "m12 2 3.1 6.3 6.9 1-5 4.9 1.2 6.8L12 17.8 5.8 21l1.2-6.8-5-4.9 6.9-1z",
        ["lock"] = "M5 11h14v10H5z|M8 11V7a4 4 0 0 1 8 0v4",
        ["logout"] = "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4|M16 17l5-5-5-5|M21 12H9",
        ["settings"] = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z|M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z",
        ["info"] = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z|M12 16v-4|M12 8h.01",
        ["alert"] = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z|M12 8v4|M12 16h.01",
        ["refresh"] = "M21 12a9 9 0 1 1-2.6-6.4|M21 3v6h-6",
        ["eye"] = "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z|M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z",
        ["tag"] = "M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8z|M7 7h.01",
        ["printer"] = "M6 9V2h12v7|M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2|M6 14h12v8H6z",
        ["download"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4|M7 10l5 5 5-5|M12 15V3",
    };

    /// <summary>Icons drawn as filled silhouettes (brand glyphs) rather than 1.9px strokes.</summary>
    private static readonly HashSet<string> Filled = new(StringComparer.Ordinal) { "whatsapp" };

    public static string Svg(string name, int size = 20, string? cssClass = null, double strokeWidth = 1.9)
    {
        if (!Paths.TryGetValue(name, out var d)) d = Paths["box"];
        if (Filled.Contains(name))
        {
            var f = new StringBuilder(1200);
            f.Append("<svg width=\"").Append(size).Append("\" height=\"").Append(size)
             .Append("\" viewBox=\"0 0 24 24\" fill=\"currentColor\" stroke=\"none\" aria-hidden=\"true\" focusable=\"false\"");
            if (!string.IsNullOrEmpty(cssClass)) f.Append(" class=\"").Append(cssClass).Append('"');
            f.Append("><path d=\"").Append(d).Append("\"/></svg>");
            return f.ToString();
        }
        var sb = new StringBuilder(256);
        sb.Append("<svg width=\"").Append(size).Append("\" height=\"").Append(size)
          .Append("\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"")
          .Append(strokeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\"");
        if (!string.IsNullOrEmpty(cssClass)) sb.Append(" class=\"").Append(cssClass).Append('"');
        sb.Append('>');
        foreach (var p in d.Split('|'))
            sb.Append("<path d=\"").Append(p).Append("\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}
