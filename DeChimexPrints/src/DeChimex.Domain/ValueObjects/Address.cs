using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.ValueObjects;

/// <summary>Postal address value object used for shipping and billing.</summary>
public sealed class Address : ValueObject
{
    public string Street { get; }
    public string City { get; }
    public string State { get; }
    public string Country { get; }
    public string? PostalCode { get; }

    private Address(string street, string city, string state, string country, string? postalCode)
    {
        Street = street;
        City = city;
        State = state;
        Country = country;
        PostalCode = postalCode;
    }

    public static Address Create(string street, string city, string state, string country = "Nigeria", string? postalCode = null)
    {
        if (string.IsNullOrWhiteSpace(street))
            throw new BusinessRuleValidationException("address.street", "Street is required.");
        if (string.IsNullOrWhiteSpace(city))
            throw new BusinessRuleValidationException("address.city", "City is required.");
        if (string.IsNullOrWhiteSpace(state))
            throw new BusinessRuleValidationException("address.state", "State is required.");
        if (string.IsNullOrWhiteSpace(country))
            throw new BusinessRuleValidationException("address.country", "Country is required.");

        return new Address(street.Trim(), city.Trim(), state.Trim(), country.Trim(), postalCode?.Trim());
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Street;
        yield return City;
        yield return State;
        yield return Country;
        yield return PostalCode;
    }

    public override string ToString() =>
        string.Join(", ", new[] { Street, City, State, PostalCode, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
