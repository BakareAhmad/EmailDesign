namespace DeChimex.Contracts.Notifications;

public sealed record NotificationDto(Guid Id, string Type, string Title, string Message, string? Link, bool IsRead, DateTime CreatedDateUtc);
