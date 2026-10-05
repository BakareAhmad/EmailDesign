using DeChimex.Domain.ValueObjects;
using DeChimex.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.ValueObjects;

public class EmailTests
{
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("test.user@domain.co.uk")]
    [InlineData("user+tag@example.org")]
    public void Create_WithValidEmail_ShouldCreateEmail(string emailValue)
    {
        // Act
        var email = Email.Create(emailValue);

        // Assert
        email.Value.Should().Be(emailValue.ToLowerInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithEmptyEmail_ShouldThrowException(string? emailValue)
    {
        // Act
        var act = () => Email.Create(emailValue!);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Email is required*");
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("invalid@")]
    [InlineData("@domain.com")]
    [InlineData("user@")]
    public void Create_WithInvalidFormat_ShouldThrowException(string emailValue)
    {
        // Act
        var act = () => Email.Create(emailValue);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Email is not a valid address*");
    }

    [Fact]
    public void Create_ShouldNormalizeToLowercase()
    {
        // Arrange
        var emailValue = "User@Example.COM";

        // Act
        var email = Email.Create(emailValue);

        // Assert
        email.Value.Should().Be("user@example.com");
    }

    [Fact]
    public void Equals_SameEmail_ShouldBeTrue()
    {
        // Arrange
        var email1 = Email.Create("user@example.com");
        var email2 = Email.Create("USER@EXAMPLE.COM");

        // Assert
        email1.Should().Be(email2);
    }

    [Fact]
    public void Equals_DifferentEmail_ShouldBeFalse()
    {
        // Arrange
        var email1 = Email.Create("user1@example.com");
        var email2 = Email.Create("user2@example.com");

        // Assert
        email1.Should().NotBe(email2);
    }
}
