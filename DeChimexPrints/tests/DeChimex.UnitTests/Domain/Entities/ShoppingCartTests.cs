using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

public class ShoppingCartTests
{
    [Fact]
    public void AddOrUpdate_NewLine_AddsItemWithQuantity()
    {
        var cart = ShoppingCart.Create(Guid.NewGuid());

        cart.AddOrUpdate(OrderLineType.Product, Guid.NewGuid(), 3);

        cart.Items.Should().HaveCount(1);
        cart.Items.Single().Quantity.Should().Be(3);
    }

    [Fact]
    public void AddOrUpdate_ExistingLine_IncrementsQuantity()
    {
        // "Add to cart" should top up an existing line, mirroring the guest localStorage cart
        // (which increments). Not replace it. Explicit edits use UpdateQuantity.
        var cart = ShoppingCart.Create(Guid.NewGuid());
        var productId = Guid.NewGuid();

        cart.AddOrUpdate(OrderLineType.Product, productId, 2);
        cart.AddOrUpdate(OrderLineType.Product, productId, 3);

        cart.Items.Should().HaveCount(1);
        cart.Items.Single().Quantity.Should().Be(5);
    }

    [Fact]
    public void AddOrUpdate_DistinguishesByLineTypeAndReference()
    {
        var cart = ShoppingCart.Create(Guid.NewGuid());
        var refId = Guid.NewGuid();

        cart.AddOrUpdate(OrderLineType.Product, refId, 1);
        cart.AddOrUpdate(OrderLineType.Service, refId, 1);

        cart.Items.Should().HaveCount(2);
    }

    [Fact]
    public void UpdateQuantity_SetsAbsoluteQuantity()
    {
        var cart = ShoppingCart.Create(Guid.NewGuid());
        var productId = Guid.NewGuid();
        cart.AddOrUpdate(OrderLineType.Product, productId, 2);
        var lineId = cart.Items.Single().Id;

        cart.UpdateQuantity(lineId, 7);

        cart.Items.Single().Quantity.Should().Be(7);
    }

    [Fact]
    public void Remove_DeletesTheLine()
    {
        var cart = ShoppingCart.Create(Guid.NewGuid());
        cart.AddOrUpdate(OrderLineType.Product, Guid.NewGuid(), 1);
        var lineId = cart.Items.Single().Id;

        cart.Remove(lineId);

        cart.Items.Should().BeEmpty();
    }

    [Fact]
    public void Clear_RemovesAllLines()
    {
        var cart = ShoppingCart.Create(Guid.NewGuid());
        cart.AddOrUpdate(OrderLineType.Product, Guid.NewGuid(), 1);
        cart.AddOrUpdate(OrderLineType.Service, Guid.NewGuid(), 2);

        cart.Clear();

        cart.Items.Should().BeEmpty();
    }
}
