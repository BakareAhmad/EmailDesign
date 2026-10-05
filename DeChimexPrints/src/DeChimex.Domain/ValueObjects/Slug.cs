using System.Text.RegularExpressions;
using DeChimex.Domain.Common;

namespace DeChimex.Domain.ValueObjects;

/// <summary>URL-friendly slug generated from a display name.</summary>
public sealed partial class Slug : ValueObject
{
    public string Value { get; }
    private Slug(string value) => Value = value;

    public static Slug FromText(string text)
    {
        text = (text ?? string.Empty).Trim().ToLowerInvariant();
        text = NonAlnum().Replace(text, "-");
        text = MultiDash().Replace(text, "-").Trim('-');
        if (string.IsNullOrEmpty(text)) text = Guid.NewGuid().ToString("n")[..8];
        return new Slug(text);
    }

    protected override IEnumerable<object?> GetEqualityComponents() { yield return Value; }
    public override string ToString() => Value;

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonAlnum();
    [GeneratedRegex(@"-{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiDash();
}
