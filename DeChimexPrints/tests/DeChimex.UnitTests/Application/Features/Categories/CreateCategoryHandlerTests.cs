using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Contracts.Categories;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Categories;

/// <summary>LOW-007: unit tests for the CreateCategory application handler (dependencies mocked).</summary>
public class CreateCategoryHandlerTests
{
    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly CreateCategoryHandler _sut;

    public CreateCategoryHandlerTests() => _sut = new CreateCategoryHandler(_categories, _uow, _cache, Substitute.For<IMajorCategoryRepository>());

    [Fact]
    public async Task Handle_WithNewName_CreatesCategory_InvalidatesCache_ReturnsDto()
    {
        _categories.NameExistsAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var command = new CreateCategoryCommand(new CreateCategoryRequest("Mugs", "Custom printed mugs", 1));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Name.Should().Be("Mugs");
        result.Slug.Should().NotBeNullOrEmpty();
        await _categories.Received(1).AddAsync(Arg.Any<Category>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _cache.Received(2).RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()); // both list-cache keys
    }

    [Fact]
    public async Task Handle_WithoutExplicitOrder_AppendsAtEnd()
    {
        // Owner rule (2026-09-09): no manual ordering - a new category takes the last spot.
        _categories.NameExistsAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _categories.GetMaxDisplayOrderAsync(Arg.Any<CancellationToken>()).Returns(7);
        var command = new CreateCategoryCommand(new CreateCategoryRequest("Mugs", null));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.DisplayOrder.Should().Be(8);
    }

    [Fact]
    public async Task Handle_WithExistingName_ThrowsConflict_AndDoesNotAdd()
    {
        _categories.NameExistsAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var command = new CreateCategoryCommand(new CreateCategoryRequest("Mugs", "x", 1));

        var act = () => _sut.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
        await _categories.DidNotReceive().AddAsync(Arg.Any<Category>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
