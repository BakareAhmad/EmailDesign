using System.Text.RegularExpressions;
using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.ValueObjects;

/// <summary>Validated e-mail address value object (normalised to lower-case).</summary>
public sealed partial class Email : ValueObject
{
    public string Value { get; }

    private Email(string value) => Value = value;

    public static Email Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new BusinessRuleValidationException("email.required", "Email is required.");
        value = value.Trim().ToLowerInvariant();
        if (value.Length > 256 || !EmailRegex().IsMatch(value))
            throw new BusinessRuleValidationException("email.invalid", "Email is not a valid address.");
        return new Email(value);
    }

    protected override IEnumerable<object?> GetEqualityComponents() { yield return Value; }
    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();
}
