using DeChimex.Application.Common.Interfaces;

namespace DeChimex.IntegrationTests.Fakes;

/// <summary>
/// Deterministic email health check for integration tests (the real one calls Brevo).
/// Tests configure the returned result; by default it reports a healthy connection.
/// </summary>
public sealed class FakeEmailHealthCheck : IEmailHealthCheck
{
    public EmailHealthResult Result { get; set; } =
        new(true, "Connected to Brevo (test fake)", "Fake provider status for tests.");

    public string? LastTestRecipient { get; private set; }

    public Task<EmailHealthResult> CheckAsync(string? testRecipient, CancellationToken ct = default)
    {
        LastTestRecipient = testRecipient;
        return Task.FromResult(Result);
    }
}
