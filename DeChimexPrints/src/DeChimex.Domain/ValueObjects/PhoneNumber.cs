using System.Text.RegularExpressions;
using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.ValueObjects;

/// <summary>E.164-friendly phone number value object.</summary>
public sealed partial class PhoneNumber : ValueObject
{
    public string Value { get; }
    private PhoneNumber(string value) => Value = value;

    public static PhoneNumber Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new BusinessRuleValidationException("phone.required", "Phone number is required.");
        value = value.Trim();
        if (!PhoneRegex().IsMatch(value))
            throw new BusinessRuleValidationException("phone.invalid", "Phone number format is invalid.");
        return new PhoneNumber(value);
    }

    protected override IEnumerable<object?> GetEqualityComponents() { yield return Value; }
    public override string ToString() => Value;

    [GeneratedRegex(@"^\+?[0-9\s\-()]{7,20}$", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();
}
