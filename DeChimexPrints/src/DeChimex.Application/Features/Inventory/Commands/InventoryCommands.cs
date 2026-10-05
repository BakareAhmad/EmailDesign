using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Inventory;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Inventory.Commands;

// ---- Stock In ----
public sealed record StockInCommand(Guid ProductId, StockInRequest Request) : ICommand<Unit>;

public sealed class StockInValidator : AbstractValidator<StockInCommand>
{
    public StockInValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Request.Quantity).GreaterThan(0);
    }
}

public sealed class StockInHandler(IInventoryRepository inventories, ICurrentUser currentUser, IUnitOfWork uow)
    : ICommandHandler<StockInCommand, Unit>
{
    public async Task<Unit> Handle(StockInCommand command, CancellationToken ct)
    {
        var inventory = await inventories.GetByProductIdAsync(command.ProductId, ct)
            ?? throw new NotFoundException("Inventory", command.ProductId);
        inventory.StockIn(command.Request.Quantity, command.Request.Reference, command.Request.Notes, currentUser.Email);
        inventories.Update(inventory);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Adjustment ----
public sealed record StockAdjustmentCommand(Guid ProductId, StockAdjustmentRequest Request) : ICommand<Unit>;

public sealed class StockAdjustmentValidator : AbstractValidator<StockAdjustmentCommand>
{
    public StockAdjustmentValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Request.Delta).NotEqual(0);
        RuleFor(x => x.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class StockAdjustmentHandler(IInventoryRepository inventories, ICurrentUser currentUser, IUnitOfWork uow)
    : ICommandHandler<StockAdjustmentCommand, Unit>
{
    public async Task<Unit> Handle(StockAdjustmentCommand command, CancellationToken ct)
    {
        var inventory = await inventories.GetByProductIdAsync(command.ProductId, ct)
            ?? throw new NotFoundException("Inventory", command.ProductId);
        inventory.Adjust(command.Request.Delta, command.Request.Reason, currentUser.Email);
        inventories.Update(inventory);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Set threshold ----
public sealed record SetThresholdCommand(Guid ProductId, SetThresholdRequest Request) : ICommand<Unit>;

public sealed class SetThresholdValidator : AbstractValidator<SetThresholdCommand>
{
    public SetThresholdValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Request.Threshold).GreaterThanOrEqualTo(0);
    }
}

public sealed class SetThresholdHandler(IInventoryRepository inventories, IUnitOfWork uow)
    : ICommandHandler<SetThresholdCommand, Unit>
{
    public async Task<Unit> Handle(SetThresholdCommand command, CancellationToken ct)
    {
        var inventory = await inventories.GetByProductIdAsync(command.ProductId, ct)
            ?? throw new NotFoundException("Inventory", command.ProductId);
        inventory.SetThreshold(command.Request.Threshold);
        inventories.Update(inventory);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
