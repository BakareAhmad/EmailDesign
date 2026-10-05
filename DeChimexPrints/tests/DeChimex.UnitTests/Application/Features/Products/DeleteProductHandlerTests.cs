using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Products.Commands;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Products;

/// <summary>Unit tests for DeleteProduct: soft delete plus the stock-record cascade.</summary>
public class DeleteProductHandlerTests
{
    private readonly IProductRepository _products = Substitute.For<IProductRepository>();
    private readonly IInventoryRepository _inventory = Substitute.For<IInventoryRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly DeleteProductHandler _sut;

    public DeleteProductHandlerTests() => _sut = new DeleteProductHandler(_products, _inventory, _uow, _cache);

    [Fact]
    public async Task Handle_WhenProductMissing_ThrowsNotFound()
    {
        _products.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var act = () => _sut.Handle(new DeleteProductCommand(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenProductHasStock_SoftDeletesStockTogether()
    {
        var product = Product.Create("Tote Bag", "TOTE-01", "Canvas tote", Guid.NewGuid(), 3500);
        var stock = Inventory.Create(product.Id, product.Name, 40);
        _products.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        _inventory.GetByProductIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(stock);

        await _sut.Handle(new DeleteProductCommand(product.Id), CancellationToken.None);

        _products.Received(1).Remove(product);
        _inventory.Received(1).Remove(stock);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenProductHasNoStock_OnlyRemovesProduct()
    {
        var product = Product.Create("Tote Bag", "TOTE-01", "Canvas tote", Guid.NewGuid(), 3500);
        _products.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        _inventory.GetByProductIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns((Inventory?)null);

        await _sut.Handle(new DeleteProductCommand(product.Id), CancellationToken.None);

        _products.Received(1).Remove(product);
        _inventory.DidNotReceive().Remove(Arg.Any<Inventory>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
