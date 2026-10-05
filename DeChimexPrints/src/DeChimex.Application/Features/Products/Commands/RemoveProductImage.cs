using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Repositories;
using MediatR;

namespace DeChimex.Application.Features.Products.Commands;

public sealed record RemoveProductImageCommand(Guid ProductId, Guid ImageId) : ICommand<Unit>;

public sealed class RemoveProductImageHandler(IProductRepository products, IUnitOfWork uow)
    : ICommandHandler<RemoveProductImageCommand, Unit>
{
    public async Task<Unit> Handle(RemoveProductImageCommand command, CancellationToken ct)
    {
        var product = await products.GetWithImagesAsync(command.ProductId, ct)
            ?? throw new NotFoundException("Product", command.ProductId);
        product.RemoveImage(command.ImageId);
        products.Update(product);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
