namespace DeChimex.Shared.Diagnostics;

/// <summary>
/// Ambient technical context for the current logical flow (request or background task).
/// Set by the Host's CorrelationIdMiddleware; flows into Task.Run continuations (AsyncLocal),
/// so fire-and-forget work such as the email queue keeps the originating correlation id.
/// </summary>
public static class TechnicalContext
{
    private static readonly AsyncLocal<string?> Correlation = new();
    private static readonly AsyncLocal<bool> Suppress = new();

    public static string? CorrelationId
    {
        get => Correlation.Value;
        set => Correlation.Value = value;
    }

    /// <summary>
    /// When true, the technical-event capture ignores log events raised on this flow. Used by
    /// the diagnostics writer itself so a failing diagnostics write can never feed back into
    /// the capture pipeline (no infinite loop).
    /// </summary>
    public static bool CaptureSuppressed
    {
        get => Suppress.Value;
        set => Suppress.Value = value;
    }
}
