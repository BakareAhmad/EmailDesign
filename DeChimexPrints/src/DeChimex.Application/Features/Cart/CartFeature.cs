using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Features.Orders;
using DeChimex.Contracts.Cart;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Cart;

public sealed record AddCartItemCommand(Guid CustomerId, AddCartItemRequest Request) : ICommand<CartDto>;

public sealed class AddCartItemValidator : AbstractValidator<AddCartItemCommand>
{
    public AddCartItemValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Request.ReferenceId).NotEmpty();
        RuleFor(x => x.Request.Quantity).GreaterThan(0);
        RuleFor(x => x.Request.LineType).Must(t => Enum.TryParse<OrderLineType>(t, true, out _)).WithMessage("Invalid line type.");
    }
}

public sealed class AddCartItemHandler(
    IShoppingCartRepository carts,
    ICartQueries queries,
    IProductRepository products,
    DeChimex.Application.Common.Interfaces.ICategoryOptionsQueries categoryOptions,
    IUnitOfWork uow)
    : ICommandHandler<AddCartItemCommand, CartDto>
{
    public async Task<CartDto> Handle(AddCartItemCommand c, CancellationToken ct)
    {
        var lineType = Enum.Parse<OrderLineType>(c.Request.LineType, true);

        // The direct-purchase path only accepts eligible products (owner's auto-detect
        // rule): custom-only products must go through "Order / Customize" so the
        // requirements/pricing workflow is never bypassed. Services keep their existing
        // cart behaviour. The gate is repeated authoritatively at order creation.
        if (lineType == OrderLineType.Product)
        {
            var product = await products.GetByIdAsync(c.Request.ReferenceId, ct)
                ?? throw new NotFoundException("Product", c.Request.ReferenceId);
            var hasOptions = product.CategoryId != Guid.Empty
                && await categoryOptions.CategoryHasActiveOptionsAsync(product.CategoryId, ct);
            DirectCheckout.RequireEligible(product.Name, product.IsCustomizable, hasOptions);
        }

        var cart = await carts.GetByCustomerIdAsync(c.CustomerId, ct);
        if (cart is null)
        {
            cart = ShoppingCart.Create(c.CustomerId);
            await carts.AddAsync(cart, ct);
        }
        cart.AddOrUpdate(lineType, c.Request.ReferenceId, c.Request.Quantity, c.Request.Notes);
        carts.Update(cart);
        await uow.SaveChangesAsync(ct);
        return await queries.GetForCustomerAsync(c.CustomerId, ct) ?? throw new NotFoundException("Cart", c.CustomerId);
    }
}

public sealed record UpdateCartItemCommand(Guid CustomerId, Guid ItemId, UpdateCartItemRequest Request) : ICommand<Unit>;
public sealed class UpdateCartItemHandler(IShoppingCartRepository carts, IUnitOfWork uow)
    : ICommandHandler<UpdateCartItemCommand, Unit>
{
    public async Task<Unit> Handle(UpdateCartItemCommand c, CancellationToken ct)
    {
        var cart = await carts.GetByCustomerIdAsync(c.CustomerId, ct) ?? throw new NotFoundException("Cart", c.CustomerId);
        cart.UpdateQuantity(c.ItemId, c.Request.Quantity);
        carts.Update(cart);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record RemoveCartItemCommand(Guid CustomerId, Guid ItemId) : ICommand<Unit>;
public sealed class RemoveCartItemHandler(IShoppingCartRepository carts, IUnitOfWork uow)
    : ICommandHandler<RemoveCartItemCommand, Unit>
{
    public async Task<Unit> Handle(RemoveCartItemCommand c, CancellationToken ct)
    {
        var cart = await carts.GetByCustomerIdAsync(c.CustomerId, ct) ?? throw new NotFoundException("Cart", c.CustomerId);
        cart.Remove(c.ItemId);
        carts.Update(cart);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record ClearCartCommand(Guid CustomerId) : ICommand<Unit>;
public sealed class ClearCartHandler(IShoppingCartRepository carts, IUnitOfWork uow)
    : ICommandHandler<ClearCartCommand, Unit>
{
    public async Task<Unit> Handle(ClearCartCommand c, CancellationToken ct)
    {
        var cart = await carts.GetByCustomerIdAsync(c.CustomerId, ct);
        if (cart is null) return Unit.Value;
        cart.Clear();
        carts.Update(cart);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record GetCartQuery(Guid CustomerId) : IQuery<CartDto?>;
public sealed class GetCartHandler(ICartQueries queries) : IQueryHandler<GetCartQuery, CartDto?>
{
    public Task<CartDto?> Handle(GetCartQuery q, CancellationToken ct) => queries.GetForCustomerAsync(q.CustomerId, ct);
}
