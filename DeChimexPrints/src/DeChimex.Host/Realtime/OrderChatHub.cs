using System.Security.Claims;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DeChimex.Host.Realtime;

/// <summary>
/// Real-time, order-scoped conversation for the custom-order workspace. Clients join the
/// group for their order after connecting; the server verifies that the caller is either
/// the order's customer or a staff member holding the orders.workspace permission, so a
/// connection can never eavesdrop on another order's chat. Every broadcast below goes
/// to the order's group only — presence, typing and message events never leak across
/// orders or to unauthorized callers.
/// Realtime policy for chat (deliberately narrow):
///   SignalR (true realtime): message/presence/typing/read-delivery events.
///   Normal request/response: sending, uploads, read beacons, history/state loads.
///   Never broadcast: upload progress, per-keystroke typing, historical data.
/// </summary>
[Authorize]
public sealed class OrderChatHub(
    IOrderWorkspaceQueries workspace,
    ICustomerRepository customers,
    IUserRepository users,
    IOrderPresenceTracker presence) : Hub
{
    public const string EventName = "orderMessage";

    public static string OrderGroup(Guid orderId) => $"order:{orderId}";

    /// <summary>Adds the caller to the given order's conversation group after authorization.</summary>
    public async Task JoinOrder(Guid orderId)
    {
        if (orderId == Guid.Empty || !await IsAuthorizedAsync(orderId))
            throw new HubException("You are not authorized to view this order's conversation.");

        await Groups.AddToGroupAsync(Context.ConnectionId, OrderGroup(orderId));
        presence.Join(orderId, Context.ConnectionId, CallerRole());
        await BroadcastPresenceAsync(orderId);
    }

    /// <summary>Transient typing signal (never persisted): forwarded to the order's
    /// group except the sender. Callers must have joined the order first.</summary>
    public async Task Typing(Guid orderId, bool isTyping)
    {
        if (orderId == Guid.Empty || !presence.IsMember(orderId, Context.ConnectionId))
            throw new HubException("Join the order's conversation before sending typing signals.");

        var role = presence.RoleOf(Context.ConnectionId) ?? CallerRole();
        await Clients.OthersInGroup(OrderGroup(orderId)).SendAsync("orderTyping", new
        {
            orderId,
            role,
            isTyping,
            // The typing party's chat identity: staff are identified by username
            // ONLY (never name/email); customers by the name staff already see.
            name = await CallerChatNameAsync(role)
        });
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var orderId in presence.Leave(Context.ConnectionId))
            await BroadcastPresenceAsync(orderId);
        await base.OnDisconnectedAsync(exception);
    }

    private Task BroadcastPresenceAsync(Guid orderId)
    {
        var snapshot = presence.GetSnapshot(orderId);
        return Clients.Group(OrderGroup(orderId)).SendAsync("orderPresence", new
        {
            orderId,
            customerOnline = snapshot.CustomerOnline,
            staffOnline = snapshot.StaffOnline,
            customerLastSeenUtc = snapshot.CustomerLastSeenUtc,
            staffLastSeenUtc = snapshot.StaffLastSeenUtc
        });
    }

    private async Task<string> CallerChatNameAsync(string role)
    {
        var userIdClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
            return role == "staff" ? "Staff" : "Customer";
        if (role == "staff")
            return (await users.GetByIdAsync(userId))?.StaffDisplayName ?? "Staff";
        return (await customers.GetByUserIdAsync(userId))?.FullName ?? "Customer";
    }

    private string CallerRole()
    {
        var user = Context.User;
        return user is not null && user.FindAll(Permissions.ClaimType)
            .Any(c => c.Value is Permissions.Orders.View or Permissions.Orders.UpdateStatus)
            ? "staff" : "customer";
    }

    private async Task<bool> IsAuthorizedAsync(Guid orderId)
    {
        var userIdClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
            return false;

        // Staff: anyone who can view orders can join any order's chat.
        if (CallerRole() == "staff")
            return true;

        // Customer: only the owner of the order.
        var customer = await customers.GetByUserIdAsync(userId);
        if (customer is null) return false;
        var order = await workspace.GetAsync(orderId, CancellationToken.None);
        return order is not null && order.CustomerId == customer.Id;
    }
}
