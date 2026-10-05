using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Notifications;
using DeChimex.Domain.Repositories;
using MediatR;

namespace DeChimex.Application.Features.Notifications;

/// <param name="UserPermissions">The requesting user's permission claims, used to scope
/// staff notifications by their RequiredPermission.</param>
/// <param name="IsStaff">True only for business staff (RoleNames.IsStaff). Rows without a
/// recipient are staff-audience rows and are never shown to customers/developers.</param>
public sealed record GetNotificationsQuery(Guid UserId, bool UnreadOnly, IReadOnlyCollection<string> UserPermissions, bool IsStaff = false) : IQuery<IReadOnlyList<NotificationDto>>;
public sealed class GetNotificationsHandler(INotificationQueries queries)
    : IQueryHandler<GetNotificationsQuery, IReadOnlyList<NotificationDto>>
{
    public Task<IReadOnlyList<NotificationDto>> Handle(GetNotificationsQuery q, CancellationToken ct)
        => queries.GetForUserAsync(q.UserId, q.UnreadOnly, q.UserPermissions, q.IsStaff, ct);
}

/// <summary>Unread count for the sidebar badge, scoped by the same permission rules.</summary>
public sealed record GetUnreadNotificationCountQuery(Guid UserId, IReadOnlyCollection<string> UserPermissions, bool IsStaff = false) : IQuery<int>;
public sealed class GetUnreadNotificationCountHandler(INotificationQueries queries)
    : IQueryHandler<GetUnreadNotificationCountQuery, int>
{
    public Task<int> Handle(GetUnreadNotificationCountQuery q, CancellationToken ct)
        => queries.CountUnreadAsync(q.UserId, q.UserPermissions, q.IsStaff, ct);
}

/// <param name="UserId">The user attempting to mark the notification read (null = anonymous, rejected).</param>
/// <param name="UserPermissions">The user's permission claims, used to verify the notification is visible to them.</param>
/// <param name="IsStaff">True only for business staff; staff-audience rows are invisible to anyone else.</param>
public sealed record MarkNotificationReadCommand(Guid NotificationId, Guid? UserId, IReadOnlyCollection<string> UserPermissions, bool IsStaff = false) : ICommand<Unit>;
public sealed class MarkNotificationReadHandler(INotificationRepository notifications, IUnitOfWork uow)
    : ICommandHandler<MarkNotificationReadCommand, Unit>
{
    public async Task<Unit> Handle(MarkNotificationReadCommand c, CancellationToken ct)
    {
        if (c.UserId is null)
            throw new NotFoundException("Notification", c.NotificationId);

        var n = await notifications.GetByIdAsync(c.NotificationId, ct) ?? throw new NotFoundException("Notification", c.NotificationId);

        // Visibility guard: a notification can only be marked read by a user who can see it.
        var visible = n.RecipientUserId == c.UserId
            || (c.IsStaff && n.RecipientUserId == null && (n.RequiredPermission == null || c.UserPermissions.Contains(n.RequiredPermission)));
        if (!visible)
            throw new NotFoundException("Notification", c.NotificationId);

        n.MarkRead();
        notifications.Update(n);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
