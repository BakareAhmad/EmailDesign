using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A versioned design proof/preview on an order. Uploading a new proof supersedes the
/// previous pending/approved one; the row with <see cref="Status"/> Approved is the
/// single version authorized for production (approval records who and when).
/// </summary>
public sealed class OrderDesignProof : Entity
{
    private OrderDesignProof() { } // EF

    private OrderDesignProof(Guid orderId, string storedFileName, string originalFileName,
        string url, string contentType, int version, string? staffNotes)
    {
        OrderId = orderId;
        StoredFileName = storedFileName;
        OriginalFileName = originalFileName;
        Url = url;
        ContentType = contentType;
        Version = version;
        StaffNotes = staffNotes;
        Status = DesignProofStatus.PendingApproval;
        CreatedDateUtc = DateTime.UtcNow;
    }

    public Guid OrderId { get; private set; }
    public string StoredFileName { get; private set; } = default!;
    public string OriginalFileName { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public int Version { get; private set; }
    public string? StaffNotes { get; private set; }
    public DesignProofStatus Status { get; private set; }
    public string? CustomerFeedback { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public Guid? ApprovedByUserId { get; private set; }
    public DateTime CreatedDateUtc { get; private set; }

    public static OrderDesignProof Create(Guid orderId, string storedFileName, string originalFileName,
        string url, string contentType, int version, string? staffNotes)
    {
        if (orderId == Guid.Empty)
            throw new BusinessRuleValidationException("order.proof.order", "An order is required.");
        if (string.IsNullOrWhiteSpace(storedFileName) || string.IsNullOrWhiteSpace(url))
            throw new BusinessRuleValidationException("order.proof", "The proof file is required.");
        if (version < 1)
            throw new BusinessRuleValidationException("order.proof.version", "Proof version must be positive.");
        return new OrderDesignProof(orderId, storedFileName, originalFileName ?? string.Empty,
            url, contentType ?? string.Empty, version,
            string.IsNullOrWhiteSpace(staffNotes) ? null : staffNotes.Trim());
    }

    /// <summary>
    /// Customer accepts this version for production. Earlier change-request feedback is
    /// kept: it is the audit trail of what was asked before approval, not clutter.
    /// </summary>
    public void Approve(Guid userId)
    {
        if (Status == DesignProofStatus.Superseded)
            throw new BusinessRuleValidationException("order.proof.superseded",
                "A superseded proof can no longer be approved.");
        Status = DesignProofStatus.Approved;
        ApprovedAtUtc = DateTime.UtcNow;
        ApprovedByUserId = userId;
    }

    /// <summary>Customer rejects this version and asks for changes.</summary>
    public void RequestChanges(string feedback)
    {
        if (Status == DesignProofStatus.Superseded)
            throw new BusinessRuleValidationException("order.proof.superseded",
                "A superseded proof can no longer be changed.");
        Status = DesignProofStatus.ChangesRequested;
        CustomerFeedback = string.IsNullOrWhiteSpace(feedback) ? null : feedback.Trim();
    }

    /// <summary>A newer version was uploaded; this one is no longer the candidate for approval.</summary>
    public void Supersede()
    {
        if (Status is not (DesignProofStatus.PendingApproval or DesignProofStatus.Approved))
            return;
        Status = DesignProofStatus.Superseded;
    }
}
