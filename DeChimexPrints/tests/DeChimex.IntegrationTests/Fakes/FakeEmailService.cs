using System.Collections.Concurrent;
using DeChimex.Application.Common.Interfaces;

namespace DeChimex.IntegrationTests.Fakes;

/// <summary>
/// Deterministic in-memory email service that replaces the real Brevo HTTP client in
/// integration tests: no network calls, and tests can assert what would have been sent
/// and drive delivery success/failure (e.g. the staff message-reply flow must report a
/// failed delivery honestly instead of claiming "Reply emailed").
/// </summary>
public sealed class FakeEmailService : IEmailService
{
    /// <summary>Every message passed to SendAsync/TrySendAsync, in order.</summary>
    public ConcurrentBag<EmailMessage> Sent { get; } = new();

    /// <summary>When false, sends "fail" (provider rejected) so failure paths can be tested.</summary>
    public bool DeliverySucceeds { get; set; } = true;

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> TrySendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Sent.Add(message);
        return Task.FromResult(DeliverySucceeds);
    }
}
