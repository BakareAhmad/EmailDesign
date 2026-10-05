using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// One message in an order's contextual conversation (customer ↔ staff). The thread
/// belongs to the order, so staff always see the structured request alongside the chat.
/// A message carries text, attachments (chat media / voice notes), or both. It can
/// quote one earlier message (<see cref="ReplyToMessageId"/>) and tracks honest
/// delivery/read state: sent = persisted; delivered = pushed to (or fetched by) a
/// recipient client; read = the recipient's client reported it actually viewed.
/// Typing and presence are transient transport concerns and are never stored here.
/// </summary>
public sealed class OrderMessage : Entity
{
    private readonly List<OrderMessageAttachment> _attachments = new();

    private OrderMessage() { } // EF

    private OrderMessage(Guid orderId, Guid? authorUserId, string authorRole, string authorName,
        string body, Guid? replyToMessageId)
    {
        OrderId = orderId;
        AuthorUserId = authorUserId;
        AuthorRole = authorRole;
        AuthorName = authorName;
        Body = body;
        ReplyToMessageId = replyToMessageId;
        CreatedDateUtc = DateTime.UtcNow;
    }

    public Guid OrderId { get; private set; }
    public Guid? AuthorUserId { get; private set; }

    /// <summary>"customer" or "staff".</summary>
    public string AuthorRole { get; private set; } = default!;
    public string AuthorName { get; private set; } = default!;
    public string Body { get; private set; } = default!;

    /// <summary>Id of the quoted message for replies; null for top-level messages.
/// A scalar FK only (no navigation): the quoted message may be redacted or
    /// unavailable at render time, which the UI handles gracefully.</summary>
    public Guid? ReplyToMessageId { get; private set; }

    /// <summary>When a recipient client received this message (live push while
    /// connected, or a later state fetch). Null until then.</summary>
    public DateTime? DeliveredAtUtc { get; private set; }

    /// <summary>When a recipient client reported this message actually viewed.
/// First read wins; the staff side reads as a team, so per-user read rows
    /// would add writes without meaningful value.</summary>
    public DateTime? ReadAtUtc { get; private set; }
    public Guid? ReadByUserId { get; private set; }

    public DateTime CreatedDateUtc { get; private set; }

    public IReadOnlyCollection<OrderMessageAttachment> Attachments => _attachments.AsReadOnly();

    public static OrderMessage Create(Guid orderId, Guid? authorUserId, string authorRole,
        string authorName, string body, Guid? replyToMessageId = null)
    {
        if (orderId == Guid.Empty)
            throw new BusinessRuleValidationException("order.message.order", "An order is required.");
        if (authorRole is not ("customer" or "staff"))
            throw new BusinessRuleValidationException("order.message.role", "Unknown author role.");
        // Body may be empty for media-only messages ("body or ≥1 attachment" is
        // enforced by the send handler, which sees both halves); length still capped.
        if (body is not null && body.Length > 4000)
            throw new BusinessRuleValidationException("order.message.body", "The message is too long.");
        return new OrderMessage(orderId, authorUserId, authorRole,
            string.IsNullOrWhiteSpace(authorName) ? authorRole : authorName.Trim(),
            (body ?? string.Empty).Trim(), replyToMessageId);
    }

    public OrderMessageAttachment Attach(string kind, string originalFileName, string storedFileName,
        string url, string contentType, long sizeBytes, int? durationSeconds)
    {
        var attachment = OrderMessageAttachment.Create(Id, kind, originalFileName,
            storedFileName, url, contentType, sizeBytes, durationSeconds);
        _attachments.Add(attachment);
        return attachment;
    }

    /// <summary>Re-labels the author (staff username changes / historical backfill).
    /// Quotes resolve the parent's name live, so they follow automatically.</summary>
    public void RenameAuthor(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new BusinessRuleValidationException("order.message.author", "Author name is required.");
        newName = newName.Trim();
        if (newName.Length > 160)
            throw new BusinessRuleValidationException("order.message.author", "Author name is too long.");
        AuthorName = newName;
    }

    /// <summary>Marks delivered; returns false when already delivered (idempotent).</summary>
    public bool MarkDelivered()
    {
        if (DeliveredAtUtc is not null) return false;
        DeliveredAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>Marks read (implies delivered); first read wins, later ones are no-ops.</summary>
    public bool MarkRead(Guid? readerUserId)
    {
        if (ReadAtUtc is not null) return false;
        DeliveredAtUtc ??= DateTime.UtcNow;
        ReadAtUtc = DateTime.UtcNow;
        ReadByUserId = readerUserId;
        return true;
    }
}
