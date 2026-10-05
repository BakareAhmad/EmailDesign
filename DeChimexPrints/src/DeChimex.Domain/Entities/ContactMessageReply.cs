using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A staff email reply sent against a contact-form submission. Child entity of
/// <see cref="ContactMessage"/> (created through the aggregate so the thread stays
/// in one transactional boundary).
/// </summary>
public sealed class ContactMessageReply : Entity
{
    private ContactMessageReply() { } // EF

    private ContactMessageReply(Guid contactMessageId, string subject, string body, Guid? sentByUserId, string? sentByName)
    {
        ContactMessageId = contactMessageId;
        Subject = subject;
        Body = body;
        SentByUserId = sentByUserId;
        SentByName = sentByName;
        SentAtUtc = DateTime.UtcNow;
    }

    public Guid ContactMessageId { get; private set; }
    public string Subject { get; private set; } = default!;
    public string Body { get; private set; } = default!;
    public DateTime SentAtUtc { get; private set; }
    public Guid? SentByUserId { get; private set; }
    public string? SentByName { get; private set; }

    public static ContactMessageReply Create(Guid contactMessageId, string subject, string body, Guid? sentByUserId, string? sentByName)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new BusinessRuleValidationException("reply.subject", "Subject is required.");
        if (string.IsNullOrWhiteSpace(body))
            throw new BusinessRuleValidationException("reply.body", "Reply message is required.");

        return new(contactMessageId, subject.Trim(), body.Trim(), sentByUserId,
            string.IsNullOrWhiteSpace(sentByName) ? null : sentByName.Trim());
    }
}
