using DeChimex.Application.Features.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Current user's notifications, scoped to their permission claims.
/// Reuses notification queries/commands directly.</summary>
[Authorize]
[Route("admin/notifications")]
public sealed class AdminNotificationsController : MvcControllerBase
{
    private bool IsStaff => Shared.Constants.RoleNames.IsStaff(CurrentUser.Roles);

    [HttpGet]
    public async Task<IActionResult> Index(bool unreadOnly = false)
    {
        ViewData["Title"] = "Notifications";
        ViewData["Heading"] = "Notifications";
        ViewData["ActiveNav"] = "notifications";

        var items = CurrentUser.UserId is Guid userId
            ? await Sender.Send(new GetNotificationsQuery(userId, unreadOnly, CurrentUser.Permissions, IsStaff))
            : new List<DeChimex.Contracts.Notifications.NotificationDto>();

        ViewBag.UnreadOnly = unreadOnly;
        return View(items);
    }

    /// <summary>Unread count for the sidebar badge, scoped to the current user.</summary>
    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount()
    {
        var count = CurrentUser.UserId is Guid userId
            ? await Sender.Send(new GetUnreadNotificationCountQuery(userId, CurrentUser.Permissions, IsStaff))
            : 0;
        return Json(new { count });
    }

    [HttpPost("{id:guid}/read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        await Sender.Send(new MarkNotificationReadCommand(id, CurrentUser.UserId, CurrentUser.Permissions, IsStaff));
        return RedirectToAction(nameof(Index));
    }
}
