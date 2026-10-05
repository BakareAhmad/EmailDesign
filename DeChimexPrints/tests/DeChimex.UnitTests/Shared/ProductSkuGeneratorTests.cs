using DeChimex.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Shared;

/// <summary>
/// Tests for the product SKU generator/validator. Every generated SKU must be exactly
/// 9 characters, must be derived from the product name prefix, and must stay unique
/// against an existing set — while remaining editable (the validator is what the user
/// manually-typed value must satisfy).
/// </summary>
public class ProductSkuGeneratorTests
{
    [Theory]
    [InlineData("Premium Business Card", "PBC")]
    [InlineData("Custom Mug Printing", "CMP")]
    [InlineData("Large Format Banner", "LFB")]
    [InlineData("Branded T-Shirt", "BTS")]
    [InlineData("Wedding Invitation Card", "WIC")]
    [InlineData("Custom Wedding Card", "CWC")]
    public void BuildPrefix_UsesInitialsOfMeaningfulWords(string name, string expected)
        => ProductSkuGenerator.BuildPrefix(name).Should().Be(expected);

    [Fact]
    public void BuildPrefix_AlwaysThreeUppercaseAlphanumeric()
    {
        foreach (var name in new[] { "", "Mug", "T-Shirt", "A Premium Mug", "Custom", "123 Name", "the and of" })
        {
            var prefix = ProductSkuGenerator.BuildPrefix(name);
            prefix.Should().NotBeNullOrWhiteSpace();
            prefix.Length.Should().Be(ProductSkuGenerator.PrefixLength);
            prefix.Should().MatchRegex("^[A-Z0-9]{3}$");
        }
    }

    [Fact]
    public void BuildPrefix_SingleWordPadsFromTheWord()
        => ProductSkuGenerator.BuildPrefix("Mug").Should().Be("MUG");

    [Fact]
    public void BuildPrefix_TShirt_ProducesThreeChars()
        => ProductSkuGenerator.BuildPrefix("T-Shirt").Should().Be("TSH");

    [Fact]
    public void BuildPrefix_BlankName_FallsBackToProduct()
        => ProductSkuGenerator.BuildPrefix("").Should().Be(ProductSkuGenerator.FallbackPrefix);

    [Fact]
    public void Suggest_GeneratesNineCharValidSkuFromName()
    {
        for (var i = 0; i < 30; i++)
        {
            var sku = ProductSkuGenerator.Suggest("Premium Business Card");
            sku.Should().HaveLength(ProductSkuGenerator.Length);
            sku.Should().MatchRegex(ProductSkuGenerator.Pattern);
            sku.Should().StartWith("PBC-");
        }
    }

    [Fact]
    public void Suggest_AvoidsExistingSkus()
    {
        var existing = new[] { "PBC-AAAA1", "PBC-AAAA2", "PBC-AAAA3" };
        var sku = ProductSkuGenerator.Suggest("Premium Business Card", existing);
        existing.Should().NotContain(sku);
        sku.Should().MatchRegex(ProductSkuGenerator.Pattern);
    }

    [Fact]
    public void SuggestKeepingPrefix_KeepsPrefixAndIsUnique()
    {
        var existing = new[] { "PBC-AAAA1", "PBC-AAAA2" };
        var sku = ProductSkuGenerator.SuggestKeepingPrefix("Premium Business Card", "PBC-AAAA1", existing);
        sku.Should().StartWith("PBC-");
        existing.Should().NotContain(sku);
        ProductSkuGenerator.IsValidFormat(sku).Should().BeTrue();
    }

    [Fact]
    public void SuggestKeepingPrefix_FallsBackToNamePrefix_WhenPrefixInvalid()
    {
        var sku = ProductSkuGenerator.SuggestKeepingPrefix("Premium Business Card", "ZZ", new List<string?>());
        sku.Should().StartWith("PBC-");
        ProductSkuGenerator.IsValidFormat(sku).Should().BeTrue();
    }

    [Theory]
    [InlineData("PBC-7K4M2", true)]
    [InlineData("CMP-4X8Q1", true)]
    [InlineData("LFB-9P2KD", true)]
    [InlineData("PBC-7K4M", false)]     // 8 chars
    [InlineData("PBC-7K4M22", false)]   // 10 chars
    [InlineData("PBC7K4M2", false)]     // no hyphen
    [InlineData("pbc-7k4m2", false)]    // lowercase
    [InlineData("PBC-7K 4", false)]     // space
    [InlineData("", false)]
    [InlineData("ABC-12345", true)]
    public void IsValidFormat_EnforcesExactlyNine(string sku, bool expected)
        => ProductSkuGenerator.IsValidFormat(sku).Should().Be(expected);
}
