namespace DeChimex.Contracts.Marketing;

/// <summary>Statuses as strings: Contracts must not depend on Domain enums.</summary>
public sealed record MarketingBroadcastDto(
    Guid Id,
    string Subject,
    string Status,
    int RecipientCount,
    int SentCount,
    int FailedCount,
    DateTime CreatedDateUtc,
    string? CreatedBy,
    DateTime? QueuedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record MarketingBroadcastDetailsDto(
    MarketingBroadcastDto Broadcast,
    string HtmlContent,
    IReadOnlyList<MarketingRecipientDto> Recipients);

public sealed record MarketingRecipientDto(
    string Email,
    string FirstName,
    string Status,
    DateTime? SentAtUtc,
    string? FailureReason);
