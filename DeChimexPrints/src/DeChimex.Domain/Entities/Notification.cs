using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;

namespace DeChimex.Domain.Entities;

/// <summary>An in-app notification for a user (staff or customer).</summary>
public sealed class Notification : AggregateRoot
{
    private Notification() { } // EF
    private Notification(Guid? recipientUserId, NotificationType type, string title, string message, string? link, string? requiredPermission)
    {
        RecipientUserId = recipientUserId;
        Type = type;
        Title = title;
        Message = message;
        Link = link;
        RequiredPermission = requiredPermission;
        IsRead = false;
    }

    public Guid? RecipientUserId { get; private set; } // null = staff audience (see RequiredPermission)
    public NotificationType Type { get; private set; }
    public string Title { get; private set; } = default!;
    public string Message { get; private set; } = default!;
    /// <summary>Permission required to see this staff notification (e.g. "orders.view").
    /// Null rows are legacy staff broadcasts shown to all staff.</summary>
    public string? RequiredPermission { get; private set; }
    public string? Link { get; private set; }
    public bool IsRead { get; private set; }
    public DateTime? ReadAtUtc { get; private set; }

    public static Notification Create(NotificationType type, string title, string message, Guid? recipientUserId = null, string? link = null, string? requiredPermission = null)
        => new(recipientUserId, type, title, message, link, requiredPermission);

    public void MarkRead()
    {
        IsRead = true;
        ReadAtUtc = DateTime.UtcNow;
    }
}
