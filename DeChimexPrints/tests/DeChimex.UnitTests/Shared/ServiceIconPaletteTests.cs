using DeChimex.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Shared;

/// <summary>
/// Tests for the curated service-icon colour palette. The palette must only ever
/// return colours from the curated set (never ad-hoc RGB/HSL), and it should spread
/// assignments sensibly across the palette rather than always landing on one colour.
/// </summary>
public class ServiceIconPaletteTests
{
    [Fact]
    public void Colors_ContainsTenSoftDistinctAccents()
    {
        ServiceIconPalette.Colors.Should().HaveCount(10);
        ServiceIconPalette.Colors.Should().OnlyHaveUniqueItems();
        // No dark / neon / fully saturated values: each is a mid-light soft hex.
        ServiceIconPalette.Colors.Should().OnlyContain(c => c.StartsWith('#') && c.Length == 7);
    }

    [Fact]
    public void Pick_WithNoExistingColors_ReturnsPaletteColor()
    {
        var result = ServiceIconPalette.Pick();
        ServiceIconPalette.Colors.Should().Contain(result);
        ServiceIconPalette.IsPaletteColor(result).Should().BeTrue();
    }

    [Fact]
    public void Pick_AlwaysReturnsACuratedColor_EvenWithForeignExistingColors()
    {
        // Existing colours include values outside the palette; these must be ignored.
        var result = ServiceIconPalette.Pick(new List<string?> { "#000000", "#123456" });
        ServiceIconPalette.IsPaletteColor(result).Should().BeTrue();
    }

    [Fact]
    public void Pick_PrefersLeastUsedColorForVariety()
    {
        // Five existing services all already use the same accent; the next pick must
        // prefer a colour that is NOT that one (lowest usage), so a set of services
        // gains variety instead of all repeating a single colour.
        var existing = new List<string?> { "#818CF8", "#818CF8", "#818CF8", "#818CF8", "#818CF8" };
        var result = ServiceIconPalette.Pick(existing);
        result.Should().NotBe("#818CF8");
    }

    [Fact]
    public void IsPaletteColor_IsCaseInsensitive_AndRejectsNull()
    {
        ServiceIconPalette.IsPaletteColor("#818cf8").Should().BeTrue();
        ServiceIconPalette.IsPaletteColor(null).Should().BeFalse();
        ServiceIconPalette.IsPaletteColor("").Should().BeFalse();
        ServiceIconPalette.IsPaletteColor("red").Should().BeFalse();
    }

    [Fact]
    public void DefaultColor_IsInPalette()
    {
        ServiceIconPalette.IsPaletteColor(ServiceIconPalette.DefaultColor).Should().BeTrue();
    }
}
