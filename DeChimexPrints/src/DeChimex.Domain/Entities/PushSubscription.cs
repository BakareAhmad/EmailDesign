using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A Web Push subscription (PWA push notifications). One row per browser/device: a user may
/// have several. The endpoint is the browser's private push-service URL; the p256dh/auth keys
/// are the per-subscription encryption secrets. Rows are removed when the user disables
/// notifications or when the push service reports the subscription as gone (410/404).
/// Privacy: rows never leave the server; the public key half (p256dh) is only useful together
/// with the server-held VAPID private key.
/// </summary>
public sealed class PushSubscription : AggregateRoot
{
    private PushSubscription() { } // EF

    private PushSubscription(Guid userId, string endpoint, string p256dh, string auth, string? userAgent)
    {
        UserId = userId;
        Endpoint = endpoint;
        P256dh = p256dh;
        Auth = auth;
        UserAgent = userAgent;
    }

    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = default!;
    public string P256dh { get; private set; } = default!;
    public string Auth { get; private set; } = default!;
    public string? UserAgent { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }

    public static PushSubscription Create(Guid userId, string endpoint, string p256dh, string auth, string? userAgent)
        => new(userId, endpoint.Trim(), p256dh.Trim(), auth.Trim(), string.IsNullOrWhiteSpace(userAgent) ? null : userAgent!.Trim());

    /// <summary>Browser key rotation: refresh the stored encryption keys for an existing endpoint.</summary>
    public void UpdateKeys(string p256dh, string auth)
    {
        P256dh = p256dh.Trim();
        Auth = auth.Trim();
    }

    public void MarkSuccess() => LastSuccessUtc = DateTime.UtcNow;
}
