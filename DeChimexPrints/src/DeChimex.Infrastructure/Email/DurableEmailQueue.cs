using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeChimex.Infrastructure.Email;

/// <summary>
/// Durable transactional-email queue (AUD-003). The previous implementation dispatched on a
/// fire-and-forget <c>Task.Run</c>, so an app restart, crash or pool recycle silently
/// discarded every queued email. This implementation writes an <see cref="EmailOutbox"/>
/// row (its own immediate save) and returns; the <see cref="OutboxEmailProcessor"/> drains
/// Pending rows with per-row retries and exponential backoff. Crash-safe, at-least-once,
/// and visible in the Developer Portal job monitor. Callers keep the same IEmailQueue API.
/// </summary>
public sealed class DurableEmailQueue(IServiceScopeFactory scopeFactory, ILogger<DurableEmailQueue> logger)
    : IEmailQueue
{
    public async Task EnqueueAsync(EmailMessage message, CancellationToken ct = default)
    {
        // Own scope + context: the caller's SaveChanges/transaction is untouched (no
        // premature flush of pending business changes, no early domain-event dispatch).
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimex.Persistence.Context.DeChimexDbContext>();
        db.EmailOutboxes.Add(EmailOutbox.Enqueue(
            message.ToEmail, message.ToName, message.Subject, message.HtmlBody,
            message.PlainTextBody, message.Template, message.Category));
        await db.SaveChangesAsync(ct);
        logger.LogDebug("Queued email to {To} (template {Template}) in the durable outbox.",
            message.ToEmail, message.Template ?? "none");
    }
}
