using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Application;

/// <summary>
/// Service-fee configuration parsing/validation (brief §2): a percentage of Paystack's fee,
/// range 0–100 with at most 2 decimal places; NaN/infinite/negative/garbage rejected.
/// </summary>
public class ServiceFeeSettingsTests
{
    [Theory]
    [InlineData(null, 100)]
    [InlineData("", 100)]
    [InlineData("   ", 100)]
    [InlineData("garbage", 100)]
    [InlineData("NaN", 100)]
    public void Read_side_parse_falls_back_to_default_on_missing_or_invalid(string? raw, decimal expected)
        => DeChimex.Application.Common.ServiceFeeSettings.Parse(raw).Should().Be(expected);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("25", 25)]
    [InlineData("50", 50)]
    [InlineData("12.5", 12.5)]
    [InlineData("100", 100)]
    [InlineData(" 50 ", 50)]
    [InlineData("50%", 50)]
    [InlineData("0.25", 0.25)]
    public void Valid_percentages_parse(string raw, decimal expected)
    {
        DeChimex.Application.Common.ServiceFeeSettings.TryParse(raw, out var percent).Should().BeTrue();
        percent.Should().Be(expected);
        DeChimex.Application.Common.ServiceFeeSettings.Parse(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    [InlineData("150")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("abc")]
    [InlineData("5.001")]
    [InlineData("1e2")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_percentages_are_rejected(string? raw)
        => DeChimex.Application.Common.ServiceFeeSettings.TryParse(raw, out _).Should().BeFalse();

    [Theory]
    [InlineData(50, "50")]
    [InlineData(12.5, "12.5")]
    [InlineData(100, "100")]
    [InlineData(0, "0")]
    public void Format_is_canonical_and_round_trips(decimal percent, string expected)
    {
        DeChimex.Application.Common.ServiceFeeSettings.Format(percent).Should().Be(expected);
        DeChimex.Application.Common.ServiceFeeSettings.TryParse(
            DeChimex.Application.Common.ServiceFeeSettings.Format(percent), out var roundTripped).Should().BeTrue();
        roundTripped.Should().Be(percent);
    }
}
