using DeChimex.Application.Features.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>Customer portal: JSON feed behind the notification bell (badge count,
/// dropdown list, mark-read). Reuses the same scoped queries/commands as the staff
/// notifications page with IsStaff = false, so only the customer's own
/// direct-recipient rows are ever visible (never staff-audience rows).</summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal/notifications")]
public sealed class PortalNotificationsController : PortalControllerBase
{
    /// <summary>Unread count for the portal bell badge, scoped to the current user.</summary>
    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount()
    {
        var count = CurrentUser.UserId is Guid userId
            ? await Sender.Send(new GetUnreadNotificationCountQuery(userId, CurrentUser.Permissions))
            : 0;
        return Json(new { count });
    }

    /// <summary>Most recent notifications for the bell dropdown (newest first).</summary>
    [HttpGet("recent")]
    public async Task<IActionResult> Recent()
    {
        var items = CurrentUser.UserId is Guid userId
            ? await Sender.Send(new GetNotificationsQuery(userId, false, CurrentUser.Permissions))
            : new List<DeChimex.Contracts.Notifications.NotificationDto>();
        return Json(items.Take(10));
    }

    [HttpPost("{id:guid}/read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        await Sender.Send(new MarkNotificationReadCommand(id, CurrentUser.UserId, CurrentUser.Permissions));
        return Json(new { ok = true });
    }

    // ---------------- PWA Web Push (2026-10-04) ----------------

    /// <summary>Public key for pushManager.subscribe (safe to expose by design).</summary>
    [HttpGet("push/vapid-public-key")]
    public async Task<IActionResult> VapidPublicKey()
        => Json(new { key = await Sender.Send(new GetVapidPublicKeyQuery()) });

    public sealed record PushSubscribeRequest(string Endpoint, string KeysP256dh, string KeysAuth, string? UserAgent);

    /// <summary>Stores (or refreshes) the calling browser's push subscription for this user.</summary>
    [HttpPost("push/subscribe")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscribeRequest request)
    {
        if (CurrentUser.UserId is not Guid userId || request is null
            || string.IsNullOrWhiteSpace(request.Endpoint)
            || string.IsNullOrWhiteSpace(request.KeysP256dh)
            || string.IsNullOrWhiteSpace(request.KeysAuth))
            return BadRequest(new { error = "Invalid subscription." });

        await Sender.Send(new SubscribePushCommand(userId, new SubscribePushRequest(
            request.Endpoint, request.KeysP256dh, request.KeysAuth, request.UserAgent)));
        return Json(new { ok = true });
    }

    /// <summary>Removes the calling browser's subscription (idempotent, endpoint-owner checked).</summary>
    [HttpPost("push/unsubscribe")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Endpoint))
            return BadRequest(new { error = "Invalid subscription." });
        await Sender.Send(new UnsubscribePushCommand(CurrentUser.UserId, request.Endpoint));
        return Json(new { ok = true });
    }

    public sealed record PushUnsubscribeRequest(string Endpoint);
}
