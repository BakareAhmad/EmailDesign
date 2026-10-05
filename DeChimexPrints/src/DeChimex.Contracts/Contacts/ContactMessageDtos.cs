namespace DeChimex.Contracts.Contacts;

/// <summary>Read model for a staff email reply on a contact-form submission.</summary>
public sealed record ContactMessageReplyDto(
    Guid Id, string Subject, string Body, DateTime SentAtUtc, string? SentByName);

/// <summary>Read model for a persisted contact-form submission.</summary>
public sealed record ContactMessageDto(
    Guid Id, string Name, string Email, string? Phone, string? Service,
    string Details, DateTime CreatedUtc, bool IsResolved, DateTime? ResolvedAtUtc,
    IReadOnlyList<ContactMessageReplyDto> Replies);
