using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A file attached to an order (customer design/reference uploads, staff proofs or
/// clarified artwork). Files are never silently deleted — an explicit delete is the
/// only removal path — so the order keeps a full file history.
/// </summary>
public sealed class OrderFile : Entity
{
    private OrderFile() { } // EF

    private OrderFile(Guid orderId, string storedFileName, string originalFileName, string url,
        string contentType, long sizeBytes, string? purpose, Guid? uploadedByUserId, string uploadedByRole)
    {
        OrderId = orderId;
        StoredFileName = storedFileName;
        OriginalFileName = originalFileName;
        Url = url;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Purpose = purpose;
        UploadedByUserId = uploadedByUserId;
        UploadedByRole = uploadedByRole;
        CreatedDateUtc = DateTime.UtcNow;
    }

    public Guid OrderId { get; private set; }
    public string StoredFileName { get; private set; } = default!;
    public string OriginalFileName { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }
    public string? Purpose { get; private set; }
    public Guid? UploadedByUserId { get; private set; }

    /// <summary>"customer" or "staff" — who attached this file.</summary>
    public string UploadedByRole { get; private set; } = default!;
    public DateTime CreatedDateUtc { get; private set; }

    public static OrderFile Create(Guid orderId, string storedFileName, string originalFileName,
        string url, string contentType, long sizeBytes, string? purpose, Guid? uploadedByUserId, string uploadedByRole)
    {
        if (orderId == Guid.Empty)
            throw new BusinessRuleValidationException("order.file.order", "An order is required.");
        if (string.IsNullOrWhiteSpace(storedFileName) || string.IsNullOrWhiteSpace(url))
            throw new BusinessRuleValidationException("order.file", "The stored file is required.");
        if (uploadedByRole is not ("customer" or "staff"))
            throw new BusinessRuleValidationException("order.file.role", "Unknown uploader role.");
        return new OrderFile(orderId, storedFileName, originalFileName ?? string.Empty, url,
            contentType ?? string.Empty, sizeBytes, purpose, uploadedByUserId, uploadedByRole);
    }
}
