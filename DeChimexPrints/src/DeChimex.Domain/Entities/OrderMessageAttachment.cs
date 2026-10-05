using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// One attachment on a conversation message (chat media). Unlike <see cref="OrderFile"/>
/// (order-level references/proofs with an explicit delete path), message attachments are
/// part of the conversation record: they render inline in the chat bubble and live and
/// die with the message. Only the public <see cref="Url"/> ever leaves the server —
/// raw storage paths are never exposed to clients.
/// </summary>
public sealed class OrderMessageAttachment : Entity
{
    private OrderMessageAttachment() { } // EF

    private OrderMessageAttachment(Guid messageId, string kind, string originalFileName,
        string storedFileName, string url, string contentType, long sizeBytes, int? durationSeconds)
    {
        MessageId = messageId;
        Kind = kind;
        OriginalFileName = originalFileName;
        StoredFileName = storedFileName;
        Url = url;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        DurationSeconds = durationSeconds;
    }

    public Guid MessageId { get; private set; }

    /// <summary>"image" | "video" | "audio" | "voice" | "file". Voice notes are audio
    /// recorded in the composer; plain audio is an uploaded sound file.</summary>
    public string Kind { get; private set; } = default!;
    public string OriginalFileName { get; private set; } = default!;
    public string StoredFileName { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }

    /// <summary>Playback length for voice/audio attachments; null otherwise.</summary>
    public int? DurationSeconds { get; private set; }

    public static OrderMessageAttachment Create(Guid messageId, string kind, string originalFileName,
        string storedFileName, string url, string contentType, long sizeBytes, int? durationSeconds)
    {
        if (messageId == Guid.Empty)
            throw new BusinessRuleValidationException("order.chat.attachment.message", "A message is required.");
        if (kind is not ("image" or "video" or "audio" or "voice" or "file"))
            throw new BusinessRuleValidationException("order.chat.attachment.kind", "Unknown attachment kind.");
        if (string.IsNullOrWhiteSpace(storedFileName) || string.IsNullOrWhiteSpace(url))
            throw new BusinessRuleValidationException("order.chat.attachment", "The stored file is required.");
        if (sizeBytes <= 0)
            throw new BusinessRuleValidationException("order.chat.attachment.size", "The attachment is empty.");
        if (durationSeconds is < 1 or > 3600)
            throw new BusinessRuleValidationException("order.chat.attachment.duration", "Invalid attachment duration.");
        if (durationSeconds is not null && kind is not ("audio" or "voice"))
            throw new BusinessRuleValidationException("order.chat.attachment.duration", "Only audio attachments carry a duration.");
        return new OrderMessageAttachment(messageId, kind, originalFileName ?? string.Empty,
            storedFileName, url, contentType ?? string.Empty, sizeBytes, durationSeconds);
    }
}
