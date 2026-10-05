using System.Globalization;
using System.Security.Cryptography;
using DeChimex.Application.Common.Interfaces;

namespace DeChimex.Infrastructure.Authentication;

/// <summary>Generates cryptographically strong opaque tokens.</summary>
public sealed class SecureTokenGenerator : ISecureTokenGenerator
{
    public string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");

    public string GenerateNumericToken(int digits = 6)
    {
        if (digits < 1 || digits > 9)
            throw new ArgumentOutOfRangeException(nameof(digits), "Numeric tokens support 1-9 digits.");
        // GetInt32 is cryptographically random and rejection-sampled => uniform over
        // [0, 10^digits) with no modulo bias. "D{n}" keeps leading zeros ("012345").
        return RandomNumberGenerator.GetInt32(0, (int)Math.Pow(10, digits))
            .ToString($"D{digits}", CultureInfo.InvariantCulture);
    }
}
