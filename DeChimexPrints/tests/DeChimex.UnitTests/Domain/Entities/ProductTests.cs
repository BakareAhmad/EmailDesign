using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

public class ProductTests
{
    private readonly Guid _categoryId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidData_ShouldCreateProduct()
    {
        // Arrange & Act
        var product = Product.Create(
            "Test Product",
            "SKU-001",
            "A test product description",
            _categoryId,
            99.99m,
            true,
            false);

        // Assert
        product.Name.Should().Be("Test Product");
        product.Sku.Should().Be("SKU-001");
        product.Description.Should().Be("A test product description");
        product.CategoryId.Should().Be(_categoryId);
        product.SellingPrice.Amount.Should().Be(99.99m);
        product.IsCustomizable.Should().BeTrue();
        product.IsFeatured.Should().BeFalse();
        product.IsActive.Should().BeTrue();
        product.Slug.Value.Should().Be("test-product");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithEmptyName_ShouldThrowException(string? name)
    {
        // Act
        var act = () => Product.Create(name!, "SKU-001", null, _categoryId, 99.99m);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Product name is required*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithEmptySku_ShouldThrowException(string? sku)
    {
        // Act
        var act = () => Product.Create("Test Product", sku!, null, _categoryId, 99.99m);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*SKU is required*");
    }

    [Fact]
    public void Create_WithEmptyCategoryId_ShouldThrowException()
    {
        // Act
        var act = () => Product.Create("Test Product", "SKU-001", null, Guid.Empty, 99.99m);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*category is required*");
    }

    [Fact]
    public void Create_ShouldNormalizeSkuToUppercase()
    {
        // Act
        var product = Product.Create("Test Product", "sku-001", null, _categoryId, 99.99m);

        // Assert
        product.Sku.Should().Be("SKU-001");
    }

    [Fact]
    public void Update_WithValidData_ShouldUpdateProduct()
    {
        // Arrange
        var product = Product.Create("Old Name", "SKU-001", "Old description", _categoryId, 99.99m);
        var newCategoryId = Guid.NewGuid();

        // Act
        product.Update("New Name", "Updated description", newCategoryId, 149.99m, false, true);

        // Assert
        product.Name.Should().Be("New Name");
        product.Description.Should().Be("Updated description");
        product.CategoryId.Should().Be(newCategoryId);
        product.SellingPrice.Amount.Should().Be(149.99m);
        product.IsCustomizable.Should().BeFalse();
        product.IsFeatured.Should().BeTrue();
        product.Slug.Value.Should().Be("new-name");
    }

    [Fact]
    public void Update_WithUnchangedName_KeepsExistingSlug()
    {
        // An ordinary edit (e.g. a price change) must never churn the product URL — and
        // must never even re-derive the slug, so no unique-index surprise is possible.
        var product = Product.Create("Steady Name", "SKU-001", null, _categoryId, 99.99m);

        product.Update("Steady Name", "new description", _categoryId, 250m, false, false);

        product.Slug.Value.Should().Be("steady-name");
        product.SellingPrice.Amount.Should().Be(250m);
    }

    [Fact]
    public void Update_WithNameChange_UsesProvidedResolvedSlug_WhenGiven()
    {
        var product = Product.Create("Original Name", "SKU-001", null, _categoryId, 99.99m);

        product.Update("Renamed Product", null, _categoryId, 150m, false, false,
            DeChimex.Domain.ValueObjects.Slug.FromText("renamed-product-2"));

        product.Slug.Value.Should().Be("renamed-product-2", "the handler resolves collisions and passes the final slug");
        product.Name.Should().Be("Renamed Product");
    }

    [Fact]
    public void Create_WithProvidedSlug_UsesItInsteadOfDeriving()
    {
        var product = Product.Create("Some Name", "SKU-001", null, _categoryId, 50m,
            slug: DeChimex.Domain.ValueObjects.Slug.FromText("some-name-2"));

        product.Slug.Value.Should().Be("some-name-2");
    }

    [Fact]
    public void Update_WithEmptyName_ShouldThrowException()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", "Description", _categoryId, 99.99m);

        // Act
        var act = () => product.Update("", "Description", _categoryId, 99.99m, false, false);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Product name is required*");
    }

    [Fact]
    public void Activate_ShouldSetIsActiveTrue()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);
        product.Deactivate();

        // Act
        product.Activate();

        // Assert
        product.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_ShouldSetIsActiveFalse()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);

        // Act
        product.Deactivate();

        // Assert
        product.IsActive.Should().BeFalse();
    }

    [Fact]
    public void AddImage_WithValidUrl_ShouldAddImage()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);

        // Act
        var image = product.AddImage("https://example.com/image.jpg", "Test image", true);

        // Assert
        product.Images.Should().HaveCount(1);
        image.Url.Should().Be("https://example.com/image.jpg");
        image.AltText.Should().Be("Test image");
        image.IsPrimary.Should().BeTrue();
        image.DisplayOrder.Should().Be(0);
    }

    [Fact]
    public void AddImage_WithEmptyUrl_ShouldThrowException()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);

        // Act
        var act = () => product.AddImage("", "Alt text");

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Image URL is required*");
    }

    [Fact]
    public void AddImage_WhenNoImagesExist_ShouldMakePrimary()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);

        // Act
        var image = product.AddImage("https://example.com/image.jpg");

        // Assert
        image.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void AddImage_WhenImagesExist_ShouldNotMakePrimary()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);
        product.AddImage("https://example.com/image1.jpg", isPrimary: true);

        // Act
        var image2 = product.AddImage("https://example.com/image2.jpg");

        // Assert
        image2.IsPrimary.Should().BeFalse();
        product.Images.Should().HaveCount(2);
    }

    [Fact]
    public void AddImage_WithIsPrimaryTrue_ShouldUnsetOtherPrimary()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);
        var image1 = product.AddImage("https://example.com/image1.jpg", isPrimary: true);

        // Act
        var image2 = product.AddImage("https://example.com/image2.jpg", isPrimary: true);

        // Assert
        image1.IsPrimary.Should().BeFalse();
        image2.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void RemoveImage_ExistingImage_ShouldRemove()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);
        var image = product.AddImage("https://example.com/image.jpg");
        var second = product.AddImage("https://example.com/image2.jpg");

        // Act
        product.RemoveImage(image.Id);

        // Assert
        product.Images.Should().ContainSingle().Which.Id.Should().Be(second.Id);
    }

    [Fact]
    public void RemoveImage_LastImage_ShouldThrow()
    {
        // A product must never end up without an image (owner rule 2026-09-09):
        // the storefront has no placeholder fallback, so the last image is protected.
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);
        var image = product.AddImage("https://example.com/image.jpg");

        var act = () => product.RemoveImage(image.Id);

        act.Should().Throw<DeChimex.Domain.Exceptions.BusinessRuleValidationException>();
        product.Images.Should().ContainSingle();
    }

    [Fact]
    public void RemoveImage_PrimaryImage_ShouldMakeNextImagePrimary()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);
        var image1 = product.AddImage("https://example.com/image1.jpg", isPrimary: true);
        var image2 = product.AddImage("https://example.com/image2.jpg");

        // Act
        product.RemoveImage(image1.Id);

        // Assert
        product.Images.Should().HaveCount(1);
        image2.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void RemoveImage_NonExistentId_ShouldDoNothing()
    {
        // Arrange
        var product = Product.Create("Test Product", "SKU-001", null, _categoryId, 99.99m);
        product.AddImage("https://example.com/image.jpg");

        // Act
        product.RemoveImage(Guid.NewGuid());

        // Assert
        product.Images.Should().HaveCount(1);
    }
}
