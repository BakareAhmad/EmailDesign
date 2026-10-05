using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Shared.Constants;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Orders.Commands;

/// <summary>
/// Advances an order through its production/fulfilment lifecycle. Deducts stock (StockOut) from
/// reserved inventory when production starts, honouring "products only" for mixed orders.
/// </summary>
public sealed record UpdateOrderStatusCommand(Guid OrderId, string Status) : ICommand<Unit>;

public sealed class UpdateOrderStatusValidator : AbstractValidator<UpdateOrderStatusCommand>
{
    public UpdateOrderStatusValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Status).NotEmpty()
            .Must(s => Enum.TryParse<OrderStatus>(s, true, out _))
            .WithMessage("Unknown order status.");
    }
}

public sealed class UpdateOrderStatusHandler(
    IOrderRepository orders,
    IInventoryRepository inventories,
    INotificationService notifications,
    IOrderChatPusher chat,
    ICurrentUser currentUser,
    IUnitOfWork uow) : ICommandHandler<UpdateOrderStatusCommand, Unit>
{
    public async Task<Unit> Handle(UpdateOrderStatusCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithLinesAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        var target = Enum.Parse<OrderStatus>(command.Status, true);

        switch (target)
        {
            case OrderStatus.InProduction:
                order.StartProduction();
                await DeductReservedStockAsync(order, ct);
                break;
            case OrderStatus.Ready:
                order.MarkReady();
                break;
            case OrderStatus.Completed:
                order.MarkCompleted();
                await notifications.NotifyAsync(NotificationType.OrderCompleted, "Order completed",
                    $"Order {order.OrderNumber} has been completed.",
                    link: $"/admin/orders/{order.Id}", requiredPermission: Permissions.Orders.UpdateStatus, ct: ct);
                break;
            case OrderStatus.Delivered:
                order.MarkDelivered();
                break;
            case OrderStatus.AwaitingProcurement:
                order.FlagProcurementRequired();
                break;
            case OrderStatus.QualityCheck:
                order.StartQualityCheck();
                break;
            case OrderStatus.OutForDelivery:
                order.Dispatch();
                break;
            default:
                throw new ConflictException("order.status.transition", $"Cannot transition to {target} via this endpoint.");
        }

        // The timeline is the customer-facing history of the order (CUSTOM_ORDER_FLOW_PLAN
        // §4.7/§17): every status change is recorded alongside the pre-payment events.
        var timelineDescription = $"Order status changed to {target}.";
        order.AddTimelineEntry("status", timelineDescription,
            currentUser.UserId, currentUser.Email ?? "Staff");

        orders.Update(order);
        await uow.SaveChangesAsync(ct);

        // Live: viewers with the workspace open see the new status (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderStatus", new
        {
            status = target.ToString(),
            timelineType = "status",
            timelineDescription
        }, ct);
        return Unit.Value;
    }

    private async Task DeductReservedStockAsync(Order order, CancellationToken ct)
    {
        foreach (var line in order.Items)
        {
            var inventory = await inventories.GetByProductIdAsync(line.ProductId, ct);
            if (inventory is null) continue;

            // AUD-014: the deduction must honour the reservation that payment made. Stock
            // may have moved on since (adjustments, another order producing first), so the
            // reservation can exceed on-hand — deducting min(qty, onHand) alone silently
            // under-deducted and shipped short. Cap by BOTH the reservation and on-hand,
            // and surface any shortfall as an attention row so procurement re-triggers.
            var deductible = Math.Min(line.Quantity, Math.Min(inventory.QuantityReserved, inventory.QuantityOnHand));
            if (deductible > 0)
            {
                inventory.StockOut(deductible, order.OrderNumber, "Fulfilment stock-out", "system");
                inventories.Update(inventory);
            }

            var shortfall = line.Quantity - deductible;
            if (shortfall > 0)
            {
                // Same event the reservation path raises; BusinessActivityCapture turns it
                // into an "inventory shortage" attention row for procurement.
                inventory.RaiseShortage(shortfall, order.OrderNumber);
                inventories.Update(inventory);
            }
        }
    }
}
