using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Products.Commands;

/// <summary>
/// Re-applies a product's image order. The first image in the submitted order becomes
/// the primary/thumbnail image used on product cards and lists.
/// </summary>
public sealed record ReorderProductImagesCommand(Guid ProductId, IReadOnlyList<Guid> OrderedImageIds) : ICommand<Unit>;

public sealed class ReorderProductImagesValidator : AbstractValidator<ReorderProductImagesCommand>
{
    public ReorderProductImagesValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.OrderedImageIds).NotEmpty().WithMessage("A product needs at least one image.");
        RuleForEach(x => x.OrderedImageIds).NotEmpty();
    }
}

public sealed class ReorderProductImagesHandler(IProductRepository products, IUnitOfWork uow)
    : ICommandHandler<ReorderProductImagesCommand, Unit>
{
    public async Task<Unit> Handle(ReorderProductImagesCommand command, CancellationToken ct)
    {
        var product = await products.GetWithImagesAsync(command.ProductId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Product", command.ProductId);
        product.ReorderImages(command.OrderedImageIds);
        products.Update(product);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
