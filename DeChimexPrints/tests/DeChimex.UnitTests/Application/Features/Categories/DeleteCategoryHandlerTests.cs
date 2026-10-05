using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Categories;

/// <summary>LOW-007: unit tests for the DeleteCategory application handler (dependencies mocked).</summary>
public class DeleteCategoryHandlerTests
{
    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly IProductRepository _products = Substitute.For<IProductRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly DeleteCategoryHandler _sut;

    public DeleteCategoryHandlerTests() => _sut = new DeleteCategoryHandler(_categories, _products, _uow, _cache);

    [Fact]
    public async Task Handle_WhenCategoryMissing_ThrowsNotFound_AndDoesNotRemove()
    {
        _categories.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Category?)null);
        var command = new DeleteCategoryCommand(Guid.NewGuid());

        var act = () => _sut.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        _categories.DidNotReceive().Remove(Arg.Any<Category>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCategoryExists_Removes_InvalidatesCache_AndSaves()
    {
        var category = Category.Create("Mugs", "Custom printed mugs", 1);
        _categories.GetByIdAsync(category.Id, Arg.Any<CancellationToken>()).Returns(category);
        _products.ExistsAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Product, bool>>>(), Arg.Any<CancellationToken>()).Returns(false);
        var command = new DeleteCategoryCommand(category.Id);

        await _sut.Handle(command, CancellationToken.None);

        _categories.Received(1).Remove(category);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _cache.Received(2).RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()); // both list-cache keys
    }

    [Fact]
    public async Task Handle_WhenCategoryStillHasProducts_ThrowsAndDoesNotRemove()
    {
        var category = Category.Create("Mugs", "Custom printed mugs", 1);
        _categories.GetByIdAsync(category.Id, Arg.Any<CancellationToken>()).Returns(category);
        _products.ExistsAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Product, bool>>>(), Arg.Any<CancellationToken>()).Returns(true);
        var command = new DeleteCategoryCommand(category.Id);

        var act = () => _sut.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleValidationException>().WithMessage("*products*");
        _categories.DidNotReceive().Remove(Arg.Any<Category>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
