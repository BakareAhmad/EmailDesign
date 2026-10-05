using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A design review submission attached to an order. Unlike design proofs (which are
/// staff-uploaded and supersede previous versions), design reviews:
/// - Can be uploaded by both customers and staff
/// - Maintain independent history (multiple submissions coexist)
/// - Have their own approval/rejection workflow
/// - Preserve the original file exactly without compression/resizing
/// - Support both previewable (PNG/JPG/PDF) and non-previewable (PSD/AI) formats
/// </summary>
public sealed class OrderDesignReview : Entity
{
    private OrderDesignReview() { } // EF

    private OrderDesignReview(
        Guid orderId,
        string storedFileName,
        string originalFileName,
        string url,
        string contentType,
        long sizeBytes,
        Guid uploadedByUserId,
        string uploadedByRole,
        string? notes,
        string? previewUrl,
        string? previewStoredFileName)
    {
        OrderId = orderId;
        StoredFileName = storedFileName;
        OriginalFileName = originalFileName;
        Url = url;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        UploadedByUserId = uploadedByUserId;
        UploadedByRole = uploadedByRole;
        Notes = notes;
        PreviewUrl = previewUrl;
        PreviewStoredFileName = previewStoredFileName;
        Status = DesignReviewStatus.Pending;
        CreatedDateUtc = DateTime.UtcNow;
    }

    public Guid OrderId { get; private set; }
    public string StoredFileName { get; private set; } = default!;
    public string OriginalFileName { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }
    
    /// <summary>
    /// Preview URL for previewable file types (PNG, JPG, WEBP, PDF, SVG, TIFF).
    /// For non-previewable types (PSD, AI, EPS, ZIP), this will be null.
    /// The preview is a separate file and never replaces the original.
    /// </summary>
    public string? PreviewUrl { get; private set; }
    public string? PreviewStoredFileName { get; private set; }

    public Guid UploadedByUserId { get; private set; }
    public string UploadedByRole { get; private set; } = default!;
    public string? Notes { get; private set; }

    public DesignReviewStatus Status { get; private set; }
    public string? RejectionReason { get; private set; }
    public Guid? ApprovedByUserId { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public Guid? RejectedByUserId { get; private set; }
    public DateTime? RejectedAtUtc { get; private set; }
    public DateTime CreatedDateUtc { get; private set; }

    /// <summary>
    /// Creates a new design review submission. Validates that the file metadata is complete.
    /// </summary>
    public static OrderDesignReview Create(
        Guid orderId,
        string storedFileName,
        string originalFileName,
        string url,
        string contentType,
        long sizeBytes,
        Guid uploadedByUserId,
        string uploadedByRole,
        string? notes = null,
        string? previewUrl = null,
        string? previewStoredFileName = null)
    {
        if (orderId == Guid.Empty)
            throw new BusinessRuleValidationException("designreview.order", "An order is required.");
        if (string.IsNullOrWhiteSpace(storedFileName) || string.IsNullOrWhiteSpace(url))
            throw new BusinessRuleValidationException("designreview.file", "The file and its storage reference are required.");
        if (sizeBytes <= 0)
            throw new BusinessRuleValidationException("designreview.size", "File size must be positive.");
        if (string.IsNullOrWhiteSpace(originalFileName))
            throw new BusinessRuleValidationException("designreview.filename", "The original filename is required.");
        if (string.IsNullOrWhiteSpace(contentType))
            throw new BusinessRuleValidationException("designreview.contenttype", "The content type is required.");
        if (string.IsNullOrWhiteSpace(uploadedByRole) || (uploadedByRole != "customer" && uploadedByRole != "staff"))
            throw new BusinessRuleValidationException("designreview.role", "Upload role must be customer or staff.");
        
        return new OrderDesignReview(
            orderId, storedFileName, originalFileName, url, contentType, sizeBytes,
            uploadedByUserId, uploadedByRole, notes, previewUrl, previewStoredFileName);
    }

    /// <summary>
    /// Approves this design review submission. Only pending reviews can be approved.
    /// </summary>
    public void Approve(Guid approvedByUserId, string? approvalNotes = null)
    {
        if (Status != DesignReviewStatus.Pending)
            throw new BusinessRuleValidationException("designreview.status", 
                "Only pending design reviews can be approved.");
        
        Status = DesignReviewStatus.Approved;
        ApprovedByUserId = approvedByUserId;
        ApprovedAtUtc = DateTime.UtcNow;
        // Preserve any existing notes; approval notes are separate
    }

    /// <summary>
    /// Rejects this design review submission with a reason. Only pending reviews can be rejected.
    /// </summary>
    public void Reject(Guid rejectedByUserId, string reason)
    {
        if (Status != DesignReviewStatus.Pending)
            throw new BusinessRuleValidationException("designreview.status", 
                "Only pending design reviews can be rejected.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessRuleValidationException("designreview.reason", 
                "A rejection reason is required.");
        
        Status = DesignReviewStatus.Rejected;
        RejectionReason = reason.Trim();
        RejectedByUserId = rejectedByUserId;
        RejectedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Whether this design review has a preview available (for previewable file types).
    /// </summary>
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewUrl);

    /// <summary>
    /// Whether this design review can be approved (is pending).
    /// </summary>
    public bool CanApprove => Status == DesignReviewStatus.Pending;

    /// <summary>
    /// Whether this design review can be rejected (is pending).
    /// </summary>
    public bool CanReject => Status == DesignReviewStatus.Pending;
}
