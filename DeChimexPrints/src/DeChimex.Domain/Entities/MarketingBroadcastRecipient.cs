using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;

namespace DeChimex.Domain.Entities;

/// <summary>
/// One row per (broadcast, customer): the snapshot of who should receive a marketing
/// broadcast plus their individual delivery state. The unique (BroadcastId, CustomerId)
/// index plus the status machine (only Pending rows are ever picked up by the worker)
/// together guarantee at-most-once delivery even across worker restarts and retries.
/// </summary>
public class MarketingBroadcastRecipient : Entity
{
    private MarketingBroadcastRecipient() { } // EF

    internal MarketingBroadcastRecipient(Guid broadcastId, Guid customerId, string email, string firstName)
    {
        BroadcastId = broadcastId;
        CustomerId = customerId;
        Email = email;
        FirstName = firstName;
        Status = MarketingRecipientStatus.Pending;
    }

    public Guid BroadcastId { get; private set; }
    public Guid CustomerId { get; private set; }

    /// <summary>Snapshot of the address at queue time (customers can change email later).</summary>
    public string Email { get; private set; } = default!;
    public string FirstName { get; private set; } = default!;

    public MarketingRecipientStatus Status { get; private set; } = MarketingRecipientStatus.Pending;
    public DateTime? SentAtUtc { get; private set; }
    public string? FailureReason { get; private set; }

    public void MarkSent()
    {
        Status = MarketingRecipientStatus.Sent;
        SentAtUtc = DateTime.UtcNow;
        FailureReason = null;
    }

    public void MarkFailed(string reason)
    {
        Status = MarketingRecipientStatus.Failed;
        FailureReason = reason is { Length: > 500 } ? reason[..500] : reason;
    }

    /// <summary>Returns a FAILED recipient to Pending (retry pass). Sent rows never move.</summary>
    internal void Requeue()
    {
        if (Status == MarketingRecipientStatus.Failed)
            Status = MarketingRecipientStatus.Pending;
    }
}
