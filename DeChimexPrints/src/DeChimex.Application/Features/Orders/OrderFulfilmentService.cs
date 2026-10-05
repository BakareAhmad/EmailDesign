using DeChimex.Application.Common.Interfaces;
using DeChimex.Shared.Constants;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;

namespace DeChimex.Application.Features.Orders;

/// <summary>
/// Encapsulates the "deduct inventory after payment" rule shared by the payment-verification
/// and manual-confirmation flows. Reserves stock for each product line; any shortage flags
/// the order for procurement (backorder) rather than failing.
///
/// Business rules implemented:
///  - Only product lines affect inventory (services are ignored).
///  - Reservation never throws on shortage; it records available stock and reports the deficit.
///  - If any line is short, the order is flagged AwaitingProcurement.
/// </summary>
public interface IOrderFulfilmentService
{
    Task ReserveInventoryForPaidOrderAsync(Order order, CancellationToken ct);
}

public sealed class OrderFulfilmentService(
    IInventoryRepository inventories,
    INotificationService notifications) : IOrderFulfilmentService
{
    public async Task ReserveInventoryForPaidOrderAsync(Order order, CancellationToken ct)
    {
        var totalShortage = 0;

        foreach (var line in order.Items)
        {
            var inventory = await inventories.GetByProductIdAsync(line.ProductId, ct);
            if (inventory is null) continue; // defensive: product without inventory record

            var shortage = inventory.Reserve(line.Quantity, order.OrderNumber, "system");
            inventories.Update(inventory);
            totalShortage += shortage;
        }

        if (totalShortage > 0)
        {
            order.FlagProcurementRequired();
            await notifications.NotifyAsync(
                Domain.Enums.NotificationType.ProcurementRequired,
                "Procurement required",
                $"Order {order.OrderNumber} has {totalShortage} unit(s) of backordered stock awaiting procurement.",
                link: $"/admin/orders/{order.Id}",
                requiredPermission: Permissions.Procurement.View,
                ct: ct);
        }
    }
}
