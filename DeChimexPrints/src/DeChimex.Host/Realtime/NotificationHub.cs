using System.Security.Claims;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DeChimex.Host.Realtime;

/// <summary>Real-time staff notifications.
/// Clients receive the "notification" event when a new notification is raised for
/// an audience they belong to:
///   - "user:{userId}" group: direct-recipient notifications (RecipientUserId set) — everyone
///   - "notifications:staff" group: legacy all-staff broadcasts (RequiredPermission null) — staff only
///   - "perm:{permission}" group: scoped staff notifications — staff only
/// Staff = a business staff role (RoleNames.IsStaff). Customers and developer-only accounts
/// never join the staff audience, matching the notification list/badge queries.
/// The endpoint is mapped with RequireAuthorization, so anonymous connects fail at the transport.</summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    public const string StaffGroup = "notifications:staff";
    public const string EventName = "notification";

    public static string PermissionGroup(string permission) => $"perm:{permission}";
    public static string UserGroup(Guid userId) => $"user:{userId}";

    public override Task OnConnectedAsync()
    {
        var user = Context.User;
        var groups = new List<string>();

        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(userId, out var uid))
            groups.Add(UserGroup(uid));

        if (user is not null && IsStaffAudience(user))
        {
            groups.Add(StaffGroup);
            groups.AddRange(user.FindAll(Permissions.ClaimType).Select(c => PermissionGroup(c.Value)));
        }

        var addTasks = groups.Distinct().Select(g => Groups.AddToGroupAsync(Context.ConnectionId, g));
        return Task.WhenAll(addTasks);
    }

    /// <summary>True when the principal belongs to the staff notification audience (a business staff role).</summary>
    public static bool IsStaffAudience(ClaimsPrincipal user)
        => RoleNames.IsStaff(user.FindAll(ClaimTypes.Role).Select(c => c.Value));
}
