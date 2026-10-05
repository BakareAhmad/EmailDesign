using DeChimex.Application.Common.Interfaces;
using DeChimex.Shared.Constants;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using MediatR;

namespace DeChimex.Application.EventHandlers;

public sealed class LowStockDetectedEventHandler(INotificationService notifications)
    : INotificationHandler<LowStockDetectedEvent>
{
    public Task Handle(LowStockDetectedEvent e, CancellationToken ct)
        => notifications.NotifyAsync(NotificationType.LowStock, "Low stock alert",
            $"{e.ProductName} is low on stock ({e.Available} available, threshold {e.Threshold}).",
            link: $"/admin/inventory", requiredPermission: Permissions.Inventory.View, ct: ct);
}

public sealed class InventoryShortageDetectedEventHandler(INotificationService notifications)
    : INotificationHandler<InventoryShortageDetectedEvent>
{
    public Task Handle(InventoryShortageDetectedEvent e, CancellationToken ct)
        => notifications.NotifyAsync(NotificationType.ProcurementRequired, "Stock shortage",
            $"Shortage for {e.ProductName}: requested more than available ({e.Available}). Procurement needed.",
            link: $"/admin/procurement", requiredPermission: Permissions.Procurement.View, ct: ct);
}
