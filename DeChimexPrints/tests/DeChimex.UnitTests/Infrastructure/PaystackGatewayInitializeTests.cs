using System.Net;
using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using DeChimex.Infrastructure.Payments;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

/// <summary>
/// LOW-009: Paystack initialize payload must charge kobo (minor unit) and persist the
/// order association as Paystack custom_fields metadata (the only shape Paystack keeps).
/// </summary>
public class PaystackGatewayInitializeTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":true,\"data\":{\"authorization_url\":\"https://checkout.paystack.com/x\",\"reference\":\"R1\"}}")
            };
        }
    }

    private static (PaystackGateway gateway, CapturingHandler handler) Create()
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new PaystackOptions
        {
            SecretKey = "sk_test_x",
            PublicKey = "pk_test_x",
            ApiBaseUrl = "https://api.paystack.co",
            Currency = "NGN"
        });
        return (new PaystackGateway(new HttpClient(handler), options, NullLogger<PaystackGateway>.Instance), handler);
    }

    [Fact]
    public async Task Initialize_sends_kobo_amount_and_custom_fields_metadata()
    {
        var (gateway, handler) = Create();

        var result = await gateway.InitializeAsync(new PaymentInitializationRequest(
            "REF-1", "cust@example.com", 50862.95m, "NGN", "https://app/checkout/return",
            new Dictionary<string, string> { ["orderId"] = "order-123" }));

        result.Success.Should().BeTrue();
        result.AuthorizationUrl.Should().Be("https://checkout.paystack.com/x");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        root.GetProperty("amount").GetInt64().Should().Be(5086295, "amounts go to Paystack in kobo");
        root.GetProperty("reference").GetString().Should().Be("REF-1");
        var field = root.GetProperty("metadata").GetProperty("custom_fields")[0];
        field.GetProperty("display_name").GetString().Should().Be("orderId");
        field.GetProperty("value").GetString().Should().Be("order-123");
        field.GetProperty("variable_name").GetString().Should().Be("orderid");
    }

    [Theory]
    [InlineData(0.01, 1)]
    [InlineData(10.005, 1001)]   // midpoint rounds away from zero
    [InlineData(50862.95, 5086295)]
    public async Task Initialize_rounds_naira_to_kobo_away_from_zero(decimal naira, long expectedKobo)
    {
        var (gateway, handler) = Create();
        await gateway.InitializeAsync(new PaymentInitializationRequest("R", "a@b.c", naira, "NGN", "https://cb", null));
        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("amount").GetInt64().Should().Be(expectedKobo);
    }

    private static (PaystackGateway gateway, CapturingHandler handler) CreateWithChannels(string[] channels)
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new PaystackOptions
        {
            SecretKey = "sk_test_x",
            PublicKey = "pk_test_x",
            ApiBaseUrl = "https://api.paystack.co",
            Currency = "NGN",
            Channels = channels
        });
        return (new PaystackGateway(new HttpClient(handler), options, NullLogger<PaystackGateway>.Instance), handler);
    }

    [Fact]
    public async Task Initialize_omits_channels_when_not_configured()
    {
        var (gateway, handler) = Create();
        await gateway.InitializeAsync(new PaymentInitializationRequest("R", "a@b.c", 100m, "NGN", "https://cb", null));
        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.TryGetProperty("channels", out _).Should().BeFalse(
            "with no Paystack:Channels configured, Paystack offers every channel enabled on the integration");
    }

    [Fact]
    public async Task Initialize_sends_configured_channels()
    {
        var (gateway, handler) = CreateWithChannels(new[] { "card", "bank_transfer", "ussd", "mobile_money" });
        await gateway.InitializeAsync(new PaymentInitializationRequest("R", "a@b.c", 100m, "NGN", "https://cb", null));
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var channels = doc.RootElement.GetProperty("channels").EnumerateArray().Select(e => e.GetString()).ToArray();
        channels.Should().Equal("card", "bank_transfer", "ussd", "mobile_money");
    }
}
