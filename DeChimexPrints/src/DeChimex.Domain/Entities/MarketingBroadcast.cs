using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// An admin-composed marketing email broadcast (promotion, announcement, newsletter)
/// sent to subscribed, registered customers. Deliberately SEPARATE from the
/// transactional email templates: broadcasts carry free-form (sanitized) HTML and are
/// processed asynchronously by the background broadcast worker, never inline with a
/// web request.
/// </summary>
public sealed class MarketingBroadcast : AggregateRoot
{
    private readonly List<MarketingBroadcastRecipient> _recipients = new();

    private MarketingBroadcast() { } // EF

    private MarketingBroadcast(string subject, string htmlContent, string plainTextContent, string createdBy)
    {
        Subject = subject;
        HtmlContent = htmlContent;
        PlainTextContent = plainTextContent;
        Status = MarketingBroadcastStatus.Draft;
        CreatedBy = createdBy;
    }

    public string Subject { get; private set; } = default!;
    public string HtmlContent { get; private set; } = default!;
    public string PlainTextContent { get; private set; } = default!;

    public MarketingBroadcastStatus Status { get; private set; } = MarketingBroadcastStatus.Draft;

    public DateTime? QueuedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    public int RecipientCount => _recipients.Count;
    public int SentCount => _recipients.Count(r => r.Status == MarketingRecipientStatus.Sent);
    public int FailedCount => _recipients.Count(r => r.Status == MarketingRecipientStatus.Failed);
    public int PendingCount => _recipients.Count(r => r.Status == MarketingRecipientStatus.Pending);

    public IReadOnlyCollection<MarketingBroadcastRecipient> Recipients => _recipients.AsReadOnly();

    public static MarketingBroadcast Create(string subject, string htmlContent, string plainTextContent, string createdBy)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new BusinessRuleValidationException("marketing.subject", "A subject is required.");
        if (string.IsNullOrWhiteSpace(htmlContent))
            throw new BusinessRuleValidationException("marketing.content", "Email content is required.");

        return new MarketingBroadcast(subject.Trim(), htmlContent, plainTextContent ?? string.Empty,
            string.IsNullOrWhiteSpace(createdBy) ? "admin" : createdBy.Trim());
    }

    /// <summary>
    /// Queues the broadcast for background processing. Only a DRAFT can be queued:
    /// this guard is what prevents double-clicks / page-refreshes from double-sending,
    /// because the second Send request finds a non-draft status and is rejected.
    /// Recipients are snapshotted once, here.
    /// </summary>
    public void Queue(IReadOnlyList<(Guid CustomerId, string Email, string FirstName)> recipients)
    {
        if (Status != MarketingBroadcastStatus.Draft)
            throw new BusinessRuleValidationException("marketing.alreadyqueued",
                "This broadcast has already been queued or sent.");
        if (recipients.Count == 0)
            throw new BusinessRuleValidationException("marketing.norecipients",
                "There are no eligible recipients for this broadcast.");

        Status = MarketingBroadcastStatus.Queued;
        QueuedAtUtc = DateTime.UtcNow;
        foreach (var (customerId, email, firstName) in recipients)
            _recipients.Add(new MarketingBroadcastRecipient(Id, customerId, email, firstName));
    }

    /// <summary>Called by the background worker when it starts processing.</summary>
    public void MarkSending()
    {
        if (Status == MarketingBroadcastStatus.Queued)
            Status = MarketingBroadcastStatus.Sending;
    }

    /// <summary>
    /// Called by the worker when no pending recipients remain. The broadcast is
    /// COMPLETED even when some individual recipients failed - per-recipient failures
    /// are tracked on the recipient rows and can be retried.
    /// </summary>
    public void MarkCompleted()
    {
        if (Status is MarketingBroadcastStatus.Queued or MarketingBroadcastStatus.Sending)
        {
            Status = MarketingBroadcastStatus.Completed;
            CompletedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Requeues the broadcast for a retry pass over FAILED recipients only.</summary>
    public void RetryFailed()
    {
        if (Status != MarketingBroadcastStatus.Completed)
            throw new BusinessRuleValidationException("marketing.notretryable",
                "Only a completed broadcast can retry its failed recipients.");
        if (FailedCount == 0)
            throw new BusinessRuleValidationException("marketing.nofailures",
                "There are no failed recipients to retry.");

        Status = MarketingBroadcastStatus.Queued;
        foreach (var recipient in _recipients.Where(r => r.Status == MarketingRecipientStatus.Failed))
            recipient.Requeue();
    }
}
