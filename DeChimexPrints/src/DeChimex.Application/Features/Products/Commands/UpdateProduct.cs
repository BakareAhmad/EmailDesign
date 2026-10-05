using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Products;
using DeChimex.Domain.Repositories;
using DeChimex.Domain.ValueObjects;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Products.Commands;

public sealed record UpdateProductCommand(Guid Id, UpdateProductRequest Request) : ICommand<Unit>;

public sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.CategoryId).NotEmpty();
        RuleFor(x => x.Request.SellingPrice).GreaterThan(0)
            .WithMessage("Selling price must be greater than zero.");
    }
}

public sealed class UpdateProductHandler(IProductRepository products, ICategoryRepository categories, IUnitOfWork uow,
    DeChimex.Application.Common.Interfaces.ICacheService cache)
    : ICommandHandler<UpdateProductCommand, Unit>
{
    public async Task<Unit> Handle(UpdateProductCommand command, CancellationToken ct)
    {
        var product = await products.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Product", command.Id);
        _ = await categories.GetByIdAsync(command.Request.CategoryId, ct)
            ?? throw new NotFoundException("Category", command.Request.CategoryId);

        // Capture the category before the change so we only pay to invalidate when it actually moved
        // (a re-categorisation shifts counts on both the old and new category list entries).
        var previousCategoryId = product.CategoryId;

        // Slug resolution (see SlugExistsAsync): the unique index on Products.Slug is
        // enforced by SQL Server on EVERY write — an unresolved collision aborts the
        // WHOLE edit in a DbUpdateException, so a valid price/name edit would silently
        // never persist. An unchanged name keeps the current slug; a changed name gets
        // a collision-free slug resolved up front (-2, -3, ... after the desired value).
        Slug? resolvedSlug;
        var trimmedName = command.Request.Name.Trim();
        if (trimmedName == product.Name)
        {
            resolvedSlug = null; // keeps the existing slug inside Product.Update
        }
        else
        {
            var desired = Slug.FromText(trimmedName);
            var candidate = desired;
            var n = 2;
            while (await products.SlugExistsAsync(candidate.Value, product.Id, ct))
                candidate = Slug.FromText($"{desired.Value}-{n++}");
            resolvedSlug = candidate;
        }

        product.Update(command.Request.Name, command.Request.Description, command.Request.CategoryId,
            command.Request.SellingPrice, command.Request.IsCustomizable, command.Request.IsFeatured,
            resolvedSlug);
        products.Update(product);
        await uow.SaveChangesAsync(ct);

        // MED-004: cached category lists carry a per-category product count. A re-categorisation
        // (or any rename that affects display) would otherwise leave those counts stale until TTL.
        if (previousCategoryId != command.Request.CategoryId)
            foreach (var key in DeChimex.Application.Common.CacheKeys.CategoryLists) await cache.RemoveAsync(key, ct);

        return Unit.Value;
    }
}
