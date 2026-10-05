using DeChimex.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeChimex.Infrastructure.Email;

/// <summary>
/// Fire-and-forget email queue abstraction. The default implementation dispatches on a
/// background task using a fresh DI scope, decoupling request latency from email delivery.
/// Swap this for a durable queue (Hangfire/Service Bus) later without changing callers.
/// </summary>
public sealed class BackgroundEmailQueue(IServiceScopeFactory scopeFactory, ILogger<BackgroundEmailQueue> logger, IJobMonitor jobs)
    : IEmailQueue
{
    public const string JobKey = "email-queue";

    public Task EnqueueAsync(EmailMessage message, CancellationToken ct = default)
    {
        _ = Task.Run(async () =>
        {
            // Developer Portal: each dispatch is a job run (the correlation id of the
            // originating request flows here via AsyncLocal).
            using var run = jobs.Begin(JobKey);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                await emailService.SendAsync(message, CancellationToken.None);
                run.Succeeded(message.Template);
            }
            catch (Exception ex)
            {
                run.Failed(ex, $"Queued email ({message.Template ?? "unspecified template"}) could not be dispatched: {ex.GetType().Name}", recordEvent: false);
                logger.LogError(ex, "Failed to dispatch queued email to {To}.", message.ToEmail);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }
}
