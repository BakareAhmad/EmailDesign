using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace DeChimex.Infrastructure.Services;

/// <summary>Persists in-app notifications and pushes them to connected staff in real time (SignalR).</summary>
public sealed class NotificationService(INotificationRepository notifications, IUnitOfWork uow, INotificationPusher pusher,
    IWebPushDispatcher webPush, ILogger<NotificationService> logger)
    : INotificationService
{
    public async Task NotifyAsync(NotificationType type, string title, string message,
        Guid? recipientUserId = null, string? link = null, string? requiredPermission = null, CancellationToken ct = default)
    {
        var notification = Notification.Create(type, title, message, recipientUserId, link, requiredPermission);
        await notifications.AddAsync(notification, ct);
        await uow.SaveChangesAsync(ct);

        // Real-time push is best effort: a failed WebSocket delivery must never
        // roll back or block the persisted notification.
        try
        {
            await pusher.PushAsync(notification.Id, type, title, message, link, requiredPermission, recipientUserId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Real-time notification push failed for {Id}: {Message}", notification.Id, ex.Message);
        }

        // PWA Web Push: only DIRECT recipient notifications reach a user's devices (best
        // effort, non-blocking). Staff-audience broadcasts are deliberately never pushed.
        if (recipientUserId is Guid recipient)
        {
            try
            {
                await webPush.DispatchAsync(recipient, title, message, link, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Web push dispatch failed for {Id}: {Message}", notification.Id, ex.Message);
            }
        }
    }
}
