using DeChimex.Domain.ValueObjects;
using DeChimex.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.ValueObjects;

public class MoneyTests
{
    [Fact]
    public void Of_WithValidAmount_ShouldCreateMoney()
    {
        // Arrange & Act
        var money = Money.Of(100.50m, "USD");

        // Assert
        money.Amount.Should().Be(100.50m);
        money.Currency.Should().Be("USD");
    }

    [Fact]
    public void Of_WithNegativeAmount_ShouldThrowException()
    {
        // Arrange & Act
        var act = () => Money.Of(-10m, "USD");

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*cannot be negative*");
    }

    [Fact]
    public void Add_TwoMoneyObjects_ShouldReturnSum()
    {
        // Arrange
        var money1 = Money.Of(100m, "USD");
        var money2 = Money.Of(50m, "USD");

        // Act
        var result = money1.Add(money2);

        // Assert
        result.Amount.Should().Be(150m);
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public void Add_DifferentCurrencies_ShouldThrowException()
    {
        // Arrange
        var money1 = Money.Of(100m, "USD");
        var money2 = Money.Of(50m, "EUR");

        // Act
        var act = () => money1.Add(money2);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Cannot operate on different currencies*");
    }

    [Fact]
    public void Subtract_ValidAmount_ShouldReturnDifference()
    {
        // Arrange
        var money1 = Money.Of(100m, "USD");
        var money2 = Money.Of(30m, "USD");

        // Act
        var result = money1.Subtract(money2);

        // Assert
        result.Amount.Should().Be(70m);
    }

    [Fact]
    public void Subtract_ResultingNegative_ShouldThrowException()
    {
        // Arrange
        var money1 = Money.Of(30m, "USD");
        var money2 = Money.Of(50m, "USD");

        // Act
        var act = () => money1.Subtract(money2);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*cannot be negative*");
    }

    [Fact]
    public void Multiply_ByPositiveFactor_ShouldReturnProduct()
    {
        // Arrange
        var money = Money.Of(100m, "USD");

        // Act
        var result = money.Multiply(2.5m);

        // Assert
        result.Amount.Should().Be(250m);
    }

    [Fact]
    public void Equals_SameAmountAndCurrency_ShouldBeTrue()
    {
        // Arrange
        var money1 = Money.Of(100m, "USD");
        var money2 = Money.Of(100m, "USD");

        // Assert
        money1.Should().Be(money2);
    }

    [Fact]
    public void Equals_DifferentAmount_ShouldBeFalse()
    {
        // Arrange
        var money1 = Money.Of(100m, "USD");
        var money2 = Money.Of(200m, "USD");

        // Assert
        money1.Should().NotBe(money2);
    }

    [Fact]
    public void Zero_ShouldCreateZeroAmount()
    {
        // Act
        var money = Money.Zero();

        // Assert
        money.Amount.Should().Be(0m);
        money.Currency.Should().Be("NGN");
    }

    [Fact]
    public void Zero_WithCurrency_ShouldCreateZeroAmountWithCurrency()
    {
        // Act
        var money = Money.Zero("USD");

        // Assert
        money.Amount.Should().Be(0m);
        money.Currency.Should().Be("USD");
    }
}
