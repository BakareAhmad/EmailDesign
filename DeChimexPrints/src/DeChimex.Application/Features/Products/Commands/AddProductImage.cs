using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Products;
using DeChimex.Domain.Repositories;
using FluentValidation;

namespace DeChimex.Application.Features.Products.Commands;

public sealed record AddProductImageCommand(Guid ProductId, string Url, string? AltText, bool IsPrimary) : ICommand<ProductImageDto>;

public sealed class AddProductImageValidator : AbstractValidator<AddProductImageCommand>
{
    public AddProductImageValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Url).NotEmpty().MaximumLength(1000);
    }
}

public sealed class AddProductImageHandler(IProductRepository products, IUnitOfWork uow)
    : ICommandHandler<AddProductImageCommand, ProductImageDto>
{
    public async Task<ProductImageDto> Handle(AddProductImageCommand command, CancellationToken ct)
    {
        var product = await products.GetWithImagesAsync(command.ProductId, ct)
            ?? throw new NotFoundException("Product", command.ProductId);
        var image = product.AddImage(command.Url, command.AltText, command.IsPrimary);
        products.Update(product);
        await uow.SaveChangesAsync(ct);
        return new ProductImageDto(image.Id, image.Url, image.AltText, image.IsPrimary, image.DisplayOrder);
    }
}
