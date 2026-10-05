using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;
using Microsoft.AspNetCore.SignalR;

namespace DeChimex.Host.Realtime;

/// <summary>Wire-format payload sent to connected clients on the "notification" event.</summary>
public sealed record NotificationPushDto(
    Guid Id,
    string Type,
    string Title,
    string Message,
    string? Link,
    string? RequiredPermission,
    Guid? RecipientUserId,
    DateTime CreatedUtc);

/// <summary>Delivers persisted notifications to connected clients via the notification hub.
/// Registered as singleton: IHubContext is application-scoped.</summary>
public sealed class NotificationPusher(IHubContext<NotificationHub> hub) : INotificationPusher
{
    public Task PushAsync(Guid notificationId, NotificationType type, string title, string message, string? link, string? requiredPermission, Guid? recipientUserId, CancellationToken ct = default)
    {
        var payload = new NotificationPushDto(notificationId, type.ToString(), title, message, link, requiredPermission, recipientUserId, DateTime.UtcNow);

        if (recipientUserId is not null)
            return hub.Clients.Group(NotificationHub.UserGroup(recipientUserId.Value)).SendAsync(NotificationHub.EventName, payload, ct);

        return requiredPermission is null
            ? hub.Clients.Group(NotificationHub.StaffGroup).SendAsync(NotificationHub.EventName, payload, ct)
            : hub.Clients.Group(NotificationHub.PermissionGroup(requiredPermission)).SendAsync(NotificationHub.EventName, payload, ct);
    }
}
