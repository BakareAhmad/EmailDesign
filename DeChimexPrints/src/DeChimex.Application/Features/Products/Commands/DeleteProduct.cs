using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Repositories;
using MediatR;

namespace DeChimex.Application.Features.Products.Commands;

public sealed record DeleteProductCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteProductHandler(IProductRepository products, IInventoryRepository inventory, IUnitOfWork uow,
    DeChimex.Application.Common.Interfaces.ICacheService cache)
    : ICommandHandler<DeleteProductCommand, Unit>
{
    public async Task<Unit> Handle(DeleteProductCommand command, CancellationToken ct)
    {
        var product = await products.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Product", command.Id);

        // Cascade the soft delete to the stock record so the deleted product leaves the
        // inventory reports (the product name in past orders stays denormalized in order details).
        var stock = await inventory.GetByProductIdAsync(product.Id, ct);
        if (stock is not null) inventory.Remove(stock);

        products.Remove(product);
        await uow.SaveChangesAsync(ct);

        // MED-004: removing a product lowers its category's product count in the cached lists.
        foreach (var key in DeChimex.Application.Common.CacheKeys.CategoryLists) await cache.RemoveAsync(key, ct);

        return Unit.Value;
    }
}
