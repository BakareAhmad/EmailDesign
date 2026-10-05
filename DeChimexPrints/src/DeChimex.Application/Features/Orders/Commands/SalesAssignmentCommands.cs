using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Features.Orders;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Orders.Commands;

// ------------------------------------------------------------------------------------
// Manual sales (re)assignment by an authorized owner/admin. Validates eligibility,
// updates the order's current owner, records history in the timeline, and notifies
// the newly assigned staff via the SalesAssignedEvent. Idempotent for same-staff.
// ------------------------------------------------------------------------------------

public sealed record ReassignSalesCommand(Guid OrderId, Guid SalesUserId) : ICommand<Unit>;

public sealed class ReassignSalesValidator : AbstractValidator<ReassignSalesCommand>
{
    public ReassignSalesValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.SalesUserId).NotEmpty();
    }
}

public sealed class ReassignSalesHandler(
    IOrderRepository orders,
    ISalesAssignmentService assignment,
    ICurrentUser currentUser,
    IUnitOfWork uow) : ICommandHandler<ReassignSalesCommand, Unit>
{
    public async Task<Unit> Handle(ReassignSalesCommand command, CancellationToken ct)
    {
        if (!currentUser.HasPermission(Permissions.Users.AssignRoles))
            throw new ForbiddenException("You are not allowed to assign sales staff.");

        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        if (order.AssignedSalesUserId == command.SalesUserId)
            return Unit.Value; // already assigned to this employee: nothing to do

        var name = await assignment.RequireEligibleSalesNameAsync(command.SalesUserId, ct);
        var actor = currentUser.Email ?? "Staff";
        var previousName = order.AssignedSalesName;

        order.AssignSalesStaff(command.SalesUserId, name, currentUser.UserId, actor);
        order.AddTimelineEntry(
            previousName is null ? "assigned" : "reassigned",
            previousName is null
                ? $"Assigned to sales ({name}) by {actor}."
                : $"Reassigned from {previousName} to {name} by {actor}.",
            currentUser.UserId, actor);

        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
