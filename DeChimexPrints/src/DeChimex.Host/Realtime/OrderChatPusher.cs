using DeChimex.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DeChimex.Host.Realtime;

/// <summary>
/// Broadcasts a persisted order-chat message to every client joined to the order's
/// group. Registered as singleton: IHubContext is application-scoped. Best-effort —
/// a failed push never rolls back the already-committed message.
/// </summary>
public sealed class OrderChatPusher(IHubContext<OrderChatHub> hub, ILogger<OrderChatPusher> logger) : IOrderChatPusher
{
    public Task PushMessageAsync(Guid orderId, object payload, CancellationToken ct = default)
        => hub.Clients.Group(OrderChatHub.OrderGroup(orderId)).SendAsync(OrderChatHub.EventName, payload, ct);

    public async Task PushOrderEventAsync(Guid orderId, string eventName, object payload, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.Group(OrderChatHub.OrderGroup(orderId)).SendAsync(eventName, payload, ct);
        }
        catch (Exception ex)
        {
            // Best-effort: the change is already committed; a dead socket must never
            // turn the actor's successful POST into a 500.
            logger.LogWarning(ex, "Order {OrderId}: failed to push {EventName} to the workspace group.", orderId, eventName);
        }
    }
}
