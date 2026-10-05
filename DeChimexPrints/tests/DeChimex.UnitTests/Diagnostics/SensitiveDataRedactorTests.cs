using DeChimex.Shared.Diagnostics;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Diagnostics;

public class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("key sk_live_abcdef123456 used", "sk_live_abcdef123456")]
    [InlineData("pk_test_36f00853210aca2652 used", "pk_test_36f00853210aca2652")]
    [InlineData("brevo xkeysib-634f6054130401b4a8fda620 failed", "xkeysib-634f6054130401b4a8fda620")]
    [InlineData("Authorization: Bearer eyJhbGciOi.abc.def", "eyJhbGciOi.abc.def")]
    [InlineData("password=Hunter22&x=1", "Hunter22")]
    [InlineData("{\"token\":\"abc123secret\"}", "abc123secret")]
    [InlineData("Server=db;User Id=sa;Password=S3cret!;", "S3cret!")]
    [InlineData("card 4111111111111111 declined", "4111111111111111")]
    [InlineData("otp: 482913 sent", "482913")]
    [InlineData("card 4111 1111 1111 1111 declined", "4111 1111 1111 1111")]
    [InlineData("card 5500-0000-0000-0004 declined", "5500-0000-0000-0004")]
    [InlineData("amex 378282246310005", "378282246310005")]
    public void Secrets_are_removed(string input, string secret)
        => SensitiveDataRedactor.Redact(input)!.Should().NotContain(secret);

    [Fact]
    public void Emails_are_masked_and_plain_text_is_kept()
    {
        SensitiveDataRedactor.Redact("sent to ada.lovelace@example.com ok")!.Should().Be("sent to a***@example.com ok");
        SensitiveDataRedactor.MaskEmail("ada@example.com").Should().Be("a***@example.com");
        SensitiveDataRedactor.Redact("Order DCX-1042 moved to Processing").Should().Be("Order DCX-1042 moved to Processing");
    }

    [Theory]
    [InlineData("Webhook amount 1 does not match payment \"PAY-20260925235026-4EA8C448A5\"")]
    [InlineData("order DCX-2026-000006 ref PAY-20261231235959-ABCDEF1234")]
    [InlineData("not a card: 4111111111111112")]
    public void Payment_references_timestamps_and_non_luhn_numbers_are_kept(string input)
        => SensitiveDataRedactor.Redact(input).Should().Be(input);

    [Fact]
    public void Long_values_are_truncated()
        => SensitiveDataRedactor.Redact(new string('a', 50), 10)!.Should().Be("aaaaaaaaaa…[truncated]");

    [Theory]
    [InlineData("password", true)] [InlineData("NewPassword", true)] [InlineData("api_key", true)] [InlineData("X-Api-Key", true)]
    [InlineData("authorization", true)] [InlineData("refresh_token", true)] [InlineData("Cookie", true)] [InlineData("cvv", true)]
    [InlineData("card_number", true)] [InlineData("step", false)] [InlineData("orderId", false)] [InlineData("reference", false)]
    public void Sensitive_property_names_are_detected(string key, bool sensitive)
        => SensitiveDataRedactor.IsSensitiveKey(key).Should().Be(sensitive);
}
