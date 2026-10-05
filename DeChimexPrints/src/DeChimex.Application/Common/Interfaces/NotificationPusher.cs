using DeChimex.Domain.Enums;

namespace DeChimex.Application.Common.Interfaces;

/// <summary>Pushes a notification to connected staff in real time (SignalR).
/// Implemented in the host layer; failures are swallowed by the caller, so
/// notification persistence is never blocked by a real-time delivery error.</summary>
public interface INotificationPusher
{
    Task PushAsync(Guid notificationId, NotificationType type, string title, string message, string? link, string? requiredPermission, Guid? recipientUserId, CancellationToken ct = default);
}
