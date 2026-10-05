namespace DeChimex.Host;

/// <summary>Chat rendering labels shared by the workspace conversation partial.
/// (Kept out of the .cshtml because Razor mangles &lt; inside @functions blocks.)</summary>
public static class ChatDisplay
{
    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };

    public static string Duration(int? seconds)
    {
        var s = seconds is null or < 0 ? 0 : seconds.Value;
        return $"{s / 60}:{(s % 60):D2}";
    }
}
