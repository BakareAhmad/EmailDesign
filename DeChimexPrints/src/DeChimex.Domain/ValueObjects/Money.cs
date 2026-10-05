using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.ValueObjects;

/// <summary>
/// Monetary amount with currency. The business operates primarily in NGN.
/// Enforces non-negativity and currency consistency for arithmetic.
/// </summary>
public sealed class Money : ValueObject
{
    public const string DefaultCurrency = "NGN";

    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Zero(string currency = DefaultCurrency) => new(0m, currency);

    /// <summary>A distinct instance with the same value. EF Core maps Money as an owned type, and an
    /// owned instance must not be shared between two owners (e.g. Order.TaxAmount and Invoice.Tax):
    /// sharing makes EF track one object as two entity types and warn on every save.</summary>
    public Money Copy() => new(Amount, Currency);

    public static Money Of(decimal amount, string currency = DefaultCurrency)
    {
        if (amount < 0)
            throw new BusinessRuleValidationException("money.negative", "Monetary amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new BusinessRuleValidationException("money.currency", "Currency must be a 3-letter ISO code.");
        return new Money(decimal.Round(amount, 2, MidpointRounding.AwayFromZero), currency.ToUpperInvariant());
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        var result = Amount - other.Amount;
        if (result < 0)
            throw new BusinessRuleValidationException("money.negative", "Resulting amount cannot be negative.");
        return new Money(result, Currency);
    }

    public Money Multiply(int quantity)
    {
        if (quantity < 0)
            throw new BusinessRuleValidationException("money.quantity", "Quantity cannot be negative.");
        return new Money(Amount * quantity, Currency);
    }

    public Money Multiply(decimal factor)
    {
        if (factor < 0)
            throw new BusinessRuleValidationException("money.factor", "Factor cannot be negative.");
        return Of(Amount * factor, Currency);
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new BusinessRuleValidationException("money.currency.mismatch",
                $"Cannot operate on different currencies ({Currency} vs {other.Currency}).");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => $"{Currency} {Amount:N2}";
}
