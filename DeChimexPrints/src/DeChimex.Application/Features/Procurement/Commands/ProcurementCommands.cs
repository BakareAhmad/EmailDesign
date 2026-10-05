using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Procurement;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Procurement.Commands;

public sealed record CreateProcurementCommand(CreateProcurementRequest Request) : ICommand<ProcurementDto>;

public sealed class CreateProcurementValidator : AbstractValidator<CreateProcurementCommand>
{
    public CreateProcurementValidator()
    {
        RuleFor(x => x.Request.Items).NotEmpty();
        RuleForEach(x => x.Request.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.ProductId).NotEmpty();
            i.RuleFor(x => x.Quantity).GreaterThan(0);
        });
    }
}

public sealed class CreateProcurementHandler(
    IProcurementRepository procurements, IProductRepository products,
    IReferenceGenerator refs, IProcurementQueries queries, IUnitOfWork uow)
    : ICommandHandler<CreateProcurementCommand, ProcurementDto>
{
    public async Task<ProcurementDto> Handle(CreateProcurementCommand c, CancellationToken ct)
    {
        var reference = await refs.NextProcurementReferenceAsync(ct);
        var procurement = DeChimex.Domain.Entities.Procurement.Request(reference, c.Request.SourceOrderId);
        foreach (var i in c.Request.Items)
        {
            var product = await products.GetByIdAsync(i.ProductId, ct) ?? throw new NotFoundException("Product", i.ProductId);
            procurement.AddItem(product.Id, product.Name, i.Quantity, i.UnitCost);
        }
        await procurements.AddAsync(procurement, ct);
        await uow.SaveChangesAsync(ct);
        return await queries.GetByIdAsync(procurement.Id, ct) ?? throw new NotFoundException("Procurement", procurement.Id);
    }
}

public sealed record ApproveProcurementCommand(Guid Id) : ICommand<Unit>;
public sealed class ApproveProcurementHandler(IProcurementRepository procurements, ICurrentUser user, IUnitOfWork uow)
    : ICommandHandler<ApproveProcurementCommand, Unit>
{
    public async Task<Unit> Handle(ApproveProcurementCommand c, CancellationToken ct)
    {
        var p = await procurements.GetWithItemsAsync(c.Id, ct) ?? throw new NotFoundException("Procurement", c.Id);
        p.Approve(user.Email ?? "system");
        procurements.Update(p);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record MarkProcurementPurchasedCommand(Guid Id, MarkPurchasedRequest Request) : ICommand<Unit>;
public sealed class MarkProcurementPurchasedHandler(IProcurementRepository procurements, IUnitOfWork uow)
    : ICommandHandler<MarkProcurementPurchasedCommand, Unit>
{
    public async Task<Unit> Handle(MarkProcurementPurchasedCommand c, CancellationToken ct)
    {
        var p = await procurements.GetWithItemsAsync(c.Id, ct) ?? throw new NotFoundException("Procurement", c.Id);
        p.MarkPurchased(c.Request.SupplierName);
        procurements.Update(p);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Receiving procurement increases stock via the Inventory aggregate (StockIn transactions).</summary>
public sealed record ReceiveProcurementCommand(Guid Id) : ICommand<Unit>;
public sealed class ReceiveProcurementHandler(
    IProcurementRepository procurements, IInventoryRepository inventories, ICurrentUser user, IUnitOfWork uow)
    : ICommandHandler<ReceiveProcurementCommand, Unit>
{
    public async Task<Unit> Handle(ReceiveProcurementCommand c, CancellationToken ct)
    {
        var p = await procurements.GetWithItemsAsync(c.Id, ct) ?? throw new NotFoundException("Procurement", c.Id);
        p.MarkReceived();
        foreach (var item in p.Items)
        {
            var inventory = await inventories.GetByProductIdAsync(item.ProductId, ct);
            if (inventory is null) continue;
            inventory.StockIn(item.Quantity, p.Reference, "Procurement received", user.Email ?? "system");
            inventories.Update(inventory);
        }
        procurements.Update(p);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record DeleteProcurementCommand(Guid Id) : ICommand<Unit>;
public sealed class DeleteProcurementValidator : AbstractValidator<DeleteProcurementCommand>
{
    public DeleteProcurementValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
public sealed class DeleteProcurementHandler(IProcurementRepository procurements, IUnitOfWork uow)
    : ICommandHandler<DeleteProcurementCommand, Unit>
{
    public async Task<Unit> Handle(DeleteProcurementCommand c, CancellationToken ct)
    {
        var p = await procurements.GetByIdAsync(c.Id, ct)
            ?? throw new NotFoundException("Procurement", c.Id);

        if (p.Status is ProcurementStatus.Purchased or ProcurementStatus.Received)
            throw new BusinessRuleValidationException("procurement.locked", "Purchased or received procurements cannot be deleted because they affect stock.");

        procurements.Remove(p); // soft delete via interceptor
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
