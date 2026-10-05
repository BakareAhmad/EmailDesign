using System.Text.RegularExpressions;
using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.ValueObjects;

/// <summary>Validated staff username (normalised to lower-case). Usernames are the
/// ONLY staff identity shown inside customer-facing chat: message bubbles, quotes,
/// typing signals and conversation notifications carry the username, never the
/// staff member's name, email or internal id.</summary>
public sealed partial class Username : ValueObject
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    /// <summary>Allowed characters once normalised: letters, digits, dot, underscore, hyphen.</summary>
    public const string AllowedPattern = @"^[a-z0-9]+[a-z0-9._-]*[a-z0-9]+$";

    /// <summary>Names that would be confused with the generic chat labels ("Staff",
    /// "Customer") or imply elevated trust. Compared after normalisation.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "staff", "customer", "system", "support", "dechimex"
    };

    public string Value { get; }

    private Username(string value) => Value = value;

    public static Username Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new BusinessRuleValidationException("username.required", "Username is required.");
        value = value.Trim().ToLowerInvariant();
        if (value.Length < MinLength || value.Length > MaxLength)
            throw new BusinessRuleValidationException("username.length",
                $"Username must be between {MinLength} and {MaxLength} characters.");
        if (!UsernameRegex().IsMatch(value))
            throw new BusinessRuleValidationException("username.invalid",
                "Username may only contain letters, digits, dots, underscores and hyphens, and must start and end with a letter or digit.");
        if (IsReserved(value))
            throw new BusinessRuleValidationException("username.reserved",
                $"The username '{value}' is reserved. Choose a different username.");
        return new Username(value);
    }

    public static bool IsReserved(string normalized) => Reserved.Contains(normalized);

    /// <summary>Best-effort base username derived from an email address (used when
    /// backfilling staff who predate usernames, and by test seeds). The caller is
    /// responsible for de-duplication; the result always passes <see cref="Create"/>
    /// except when it collides with an existing username.</summary>
    public static string SuggestFromEmail(string email)
    {
        var local = (email ?? string.Empty).Split('@')[0].Trim().ToLowerInvariant();
        static bool Allowed(char c) =>
            (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is '.' or '_' or '-';
        var cleaned = new string(local.Select(c => Allowed(c) ? c : '.').ToArray()).Trim('.');
        if (cleaned.Length < MinLength)
            cleaned = (cleaned + "user")[..Math.Max(MinLength, cleaned.Length + 4)];
        if (cleaned.Length > MaxLength - 3)
            cleaned = cleaned[..(MaxLength - 3)].Trim('.');
        if (cleaned.Length < MinLength)
            cleaned = "staffuser";
        if (IsReserved(cleaned))
            cleaned += "01";
        return cleaned;
    }

    protected override IEnumerable<object?> GetEqualityComponents() { yield return Value; }
    public override string ToString() => Value;

    [GeneratedRegex(AllowedPattern, RegexOptions.Compiled)]
    private static partial Regex UsernameRegex();
}
