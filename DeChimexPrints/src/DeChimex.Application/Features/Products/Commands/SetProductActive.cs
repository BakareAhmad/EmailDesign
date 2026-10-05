using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Repositories;
using MediatR;

namespace DeChimex.Application.Features.Products.Commands;

public sealed record SetProductActiveCommand(Guid Id, bool IsActive) : ICommand<Unit>;

public sealed class SetProductActiveHandler(IProductRepository products, IUnitOfWork uow)
    : ICommandHandler<SetProductActiveCommand, Unit>
{
    public async Task<Unit> Handle(SetProductActiveCommand command, CancellationToken ct)
    {
        var product = await products.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Product", command.Id);
        if (command.IsActive) product.Activate(); else product.Deactivate();
        products.Update(product);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
