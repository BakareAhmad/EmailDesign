using System.Security.Cryptography;
using DeChimex.Application.Common.Interfaces;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Authentication;

/// <summary>Tunable PBKDF2 parameters (AUD-025). Defaults follow current OWASP guidance;
/// tests may lower the iteration count for speed via configuration only.</summary>
public sealed class PasswordHasherOptions
{
    public const string SectionName = "Security:PasswordHasher";

    /// <summary>OWASP 2023+ guidance for PBKDF2-HMAC-SHA256: 600,000 iterations.</summary>
    public int Iterations { get; init; } = 600_000;
}

/// <summary>
/// PBKDF2 (SHA-256) password hasher with per-password random salt. Output is a self-describing
/// string encoding iterations, salt and subkey so parameters can evolve without breaking old hashes:
/// <c>iterations:salt:hash</c>. <see cref="Verify"/> re-derives with the STORED iteration count,
/// and <see cref="NeedsRehash"/> reports hashes created under weaker parameters so callers can
/// transparently upgrade accounts on successful login.
/// </summary>
public sealed class PasswordHasher(IOptions<PasswordHasherOptions> options) : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;
    private const char Delimiter = ':';

    private readonly int _iterations = options.Value.Iterations;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, Algorithm, KeySize);
        return string.Join(Delimiter, _iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public bool Verify(string password, string hash)
    {
        if (!TryParse(hash, out var iterations, out var salt, out var expected)) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True when the stored hash used FEWER iterations than the current policy
    /// (i.e. it was created before an increase, or by a weaker configuration) — the caller
    /// re-hashes on successful verification (AUD-025 rehash-on-login).</summary>
    public bool NeedsRehash(string hash)
        => TryParse(hash, out var iterations, out _, out _) && iterations < _iterations;

    private static bool TryParse(string hash, out int iterations, out byte[] salt, out byte[] expected)
    {
        iterations = 0; salt = []; expected = [];
        var parts = hash.Split(Delimiter);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out iterations)) return false;
        if (iterations < 1) return false;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length > 0 && expected.Length > 0;
    }
}
