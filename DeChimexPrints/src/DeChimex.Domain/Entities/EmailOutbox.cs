using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>Status of a durable (outbox) email row.</summary>
public enum EmailOutboxStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2
}

/// <summary>
/// A transactional email persisted BEFORE it is sent (transactional-outbox pattern, AUD-003).
/// Enqueue writes this row in its own immediate save, so an application restart or crash can
/// no longer silently discard queued mail: the background processor drains Pending rows with
/// per-row attempt counts and exponential backoff, and Failed rows stay visible for diagnosis.
/// Sensitive content note: rows hold exactly the rendered message the sender already produced
/// (same as the previous in-memory queue carried) and are pruned with the diagnostics retention
/// window for Sent rows.
/// </summary>
public sealed class EmailOutbox : Entity
{
    private EmailOutbox() { } // EF

    private EmailOutbox(string toEmail, string toName, string subject, string htmlBody,
        string? plainTextBody, string? template, string? category)
    {
        ToEmail = toEmail;
        ToName = toName;
        Subject = subject;
        HtmlBody = htmlBody;
        PlainTextBody = plainTextBody;
        Template = template;
        Category = category;
        Status = EmailOutboxStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = DateTime.UtcNow;
    }

    public string ToEmail { get; private set; } = default!;
    public string ToName { get; private set; } = default!;
    public string Subject { get; private set; } = default!;
    public string HtmlBody { get; private set; } = default!;
    public string? PlainTextBody { get; private set; }
    public string? Template { get; private set; }
    public string? Category { get; private set; }

    public EmailOutboxStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? SentAtUtc { get; private set; }

    public static EmailOutbox Enqueue(string toEmail, string toName, string subject, string htmlBody,
        string? plainTextBody, string? template, string? category)
        => new(toEmail, toName, subject, htmlBody, plainTextBody, template, category);

    /// <summary>Records a dispatch failure and schedules the retry with exponential backoff.</summary>
    public void RecordFailure(string error, DateTime now, TimeSpan baseDelay)
    {
        Attempts++;
        LastError = error.Length > 1000 ? error[..999] + "…" : error;
        // Cap: after MaxAttempts failures the row parks as Failed (visible, not retried).
        if (Attempts >= MaxAttempts)
        {
            Status = EmailOutboxStatus.Failed;
            return;
        }
        var delay = TimeSpan.FromTicks(baseDelay.Ticks * (long)Math.Pow(2, Math.Min(Attempts - 1, 6)));
        NextAttemptAtUtc = now + delay;
    }

    public void MarkSent(DateTime now)
    {
        Status = EmailOutboxStatus.Sent;
        SentAtUtc = now;
        LastError = null;
    }

    /// <summary>Parks a permanently failing row (also used to cap retries).</summary>
    public const int MaxAttempts = 6;
}
