using System.Text.RegularExpressions;
using DeChimex.Infrastructure.Authentication;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

/// <summary>
/// The email-verification code is a 6-digit numeric OTP (owner decision 2026-09-07);
/// the password-reset token stays a long opaque string because it is delivered as a
/// CLICKED LINK, not typed. These tests pin both formats.
/// </summary>
public class SecureTokenGeneratorTests
{
    private readonly SecureTokenGenerator _sut = new();

    [Fact]
    public void NumericToken_IsAlwaysExactlySixDigits()
    {
        for (var i = 0; i < 2_000; i++)
        {
            var token = _sut.GenerateNumericToken();
            Regex.IsMatch(token, "^[0-9]{6}$").Should().BeTrue($"token was '{token}'");
        }
    }

    [Fact]
    public void NumericToken_RespectsRequestedDigitCount_AndAllowsLeadingZeros()
    {
        var seenLeadingZero = false;
        for (var i = 0; i < 2_000; i++)
        {
            var four = _sut.GenerateNumericToken(4);
            Regex.IsMatch(four, "^[0-9]{4}$").Should().BeTrue($"token was '{four}'");
            if (four.StartsWith('0')) seenLeadingZero = true;
        }
        seenLeadingZero.Should().BeTrue("a uniform numeric code must be able to start with 0");
    }

    [Fact]
    public void NumericToken_IsSufficientlyRandom()
    {
        // 2000 uniform draws from 10^6 codes expect ~2 collisions; anything far below
        // ~1990 distinct values would indicate a broken (biased) generator.
        var distinct = Enumerable.Range(0, 2_000).Select(_ => _sut.GenerateNumericToken()).Distinct().Count();
        distinct.Should().BeGreaterThan(1_980);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void NumericToken_RejectsUnsupportedDigitCounts(int digits)
    {
        var act = () => _sut.GenerateNumericToken(digits);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void OpaqueToken_RemainsLongAndUrlSafe()
    {
        var token = _sut.GenerateToken();
        token.Length.Should().BeGreaterThanOrEqualTo(48);
        Regex.IsMatch(token, "^[A-Za-z0-9_-]+$").Should().BeTrue($"token was '{token}'");
    }
}
