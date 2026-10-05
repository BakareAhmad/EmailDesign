using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Wishlist;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Wishlist;

public sealed record AddWishlistItemCommand(Guid CustomerId, AddWishlistItemRequest Request) : ICommand<Unit>;
public sealed class AddWishlistItemValidator : AbstractValidator<AddWishlistItemCommand>
{
    public AddWishlistItemValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Request.ProductId).NotEmpty();
    }
}

public sealed class AddWishlistItemHandler(IWishlistRepository wishlist, IUnitOfWork uow, IProductRepository products)
    : ICommandHandler<AddWishlistItemCommand, Unit>
{
    public async Task<Unit> Handle(AddWishlistItemCommand c, CancellationToken ct)
    {
        var product = await products.GetByIdAsync(c.Request.ProductId, ct);
        if (product is null || !product.IsActive)
            throw new DeChimex.Application.Common.Exceptions.NotFoundException("Product", c.Request.ProductId);
        var existing = await wishlist.FindAsync(c.CustomerId, c.Request.ProductId, ct);
        if (existing is null)
        {
            await wishlist.AddAsync(WishlistItem.Create(c.CustomerId, c.Request.ProductId), ct);
            await uow.SaveChangesAsync(ct);
        }
        return Unit.Value;
    }
}

public sealed record RemoveWishlistItemCommand(Guid CustomerId, Guid ProductId) : ICommand<Unit>;
public sealed class RemoveWishlistItemHandler(IWishlistRepository wishlist, IUnitOfWork uow)
    : ICommandHandler<RemoveWishlistItemCommand, Unit>
{
    public async Task<Unit> Handle(RemoveWishlistItemCommand c, CancellationToken ct)
    {
        var existing = await wishlist.FindAsync(c.CustomerId, c.ProductId, ct);
        if (existing is not null)
        {
            wishlist.Remove(existing);
            await uow.SaveChangesAsync(ct);
        }
        return Unit.Value;
    }
}

public sealed record GetWishlistQuery(Guid CustomerId) : IQuery<IReadOnlyList<WishlistItemDto>>;
public sealed class GetWishlistHandler(IWishlistQueries queries) : IQueryHandler<GetWishlistQuery, IReadOnlyList<WishlistItemDto>>
{
    public Task<IReadOnlyList<WishlistItemDto>> Handle(GetWishlistQuery q, CancellationToken ct) => queries.GetForCustomerAsync(q.CustomerId, ct);
}
