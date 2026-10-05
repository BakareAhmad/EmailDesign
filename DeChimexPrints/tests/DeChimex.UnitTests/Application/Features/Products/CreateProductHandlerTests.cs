using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Products.Commands;
using DeChimex.Contracts.Products;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Products;

/// <summary>
/// Tests that creating a product auto-generates a valid 9-char SKU when none is supplied,
/// auto-resolves collisions with a fresh unique suffix, and keeps a user-supplied unique
/// SKU. Dependencies (repositories/UoW/cache) are mocked.
/// </summary>
public class CreateProductHandlerTests
{
    private readonly IProductRepository _products = Substitute.For<IProductRepository>();
    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly IInventoryRepository _inventories = Substitute.For<IInventoryRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly CreateProductHandler _sut;

    public CreateProductHandlerTests()
    {
        _sut = new CreateProductHandler(_products, _categories, _inventories, _uow, _cache);
        var category = Category.Create("Print", "test", 1);
        _categories.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(category);
    }

    [Fact]
    public async Task Handle_WithBlankSku_GeneratesAValidUniqueSku()
    {
        var result = await _sut.Handle(new CreateProductCommand(new CreateProductRequest(
            "Premium Business Card", "", "desc", Guid.NewGuid(), 1500m, false, false, 0, 5)), CancellationToken.None);

        result.Sku.Should().MatchRegex(ProductSkuGenerator.Pattern);
        result.Sku.Should().HaveLength(ProductSkuGenerator.Length);
        result.Sku.Should().StartWith("PBC-");
    }

    [Fact]
    public async Task Handle_WithCollidingSku_AutoResolvesToAUniqueSuffix()
    {
        // The user's (or generated) value already exists -> the handler must pick a new
        // suffix, keeping a valid 9-char format, without throwing a conflict.
        _products.SkuExistsAsync("PBC-7K4M2", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.Handle(new CreateProductCommand(new CreateProductRequest(
            "Premium Business Card", "PBC-7K4M2", "desc", Guid.NewGuid(), 1500m, false, false, 0, 5)), CancellationToken.None);

        result.Sku.Should().MatchRegex(ProductSkuGenerator.Pattern);
        result.Sku.Should().StartWith("PBC-");
        result.Sku.Should().NotBe("PBC-7K4M2");
        // The colliding value was checked, and the final SKU was verified unique.
        await _products.Received(1).SkuExistsAsync("PBC-7K4M2", Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithValidUniqueSku_KeepsIt()
    {
        var result = await _sut.Handle(new CreateProductCommand(new CreateProductRequest(
            "Premium Business Card", "PBC-7K4M2", "desc", Guid.NewGuid(), 1500m, false, false, 0, 5)), CancellationToken.None);

        result.Sku.Should().Be("PBC-7K4M2");
        // If the requested SKU is unique, the generator must not be invoked.
        await _products.DidNotReceive().SkuExistsAsync(Arg.Is<string>(s => s != "PBC-7K4M2"),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }
}
