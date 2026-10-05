using System.Net;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using DeChimex.Infrastructure.Payments;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

/// <summary>
/// Paystack verification must follow the documented transaction status vocabulary
/// (verify-payments): success -> Succeeded; failed / abandoned / reversed -> Failed;
/// pending / processing / queued / ongoing / unknown -> Pending (the payment stays
/// retryable and unconfirmed — async channels such as bank transfer, USSD and mobile
/// money sit in-flight while the customer completes the payment). Transport errors and
/// unparseable responses are Pending too: never paid, never a hard failure.
/// </summary>
public class PaystackGatewayVerifyTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static PaystackGateway Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var options = Options.Create(new PaystackOptions
        {
            SecretKey = "sk_test_x",
            PublicKey = "pk_test_x",
            ApiBaseUrl = "https://api.paystack.co",
            Currency = "NGN"
        });
        return new PaystackGateway(new HttpClient(new StubHandler(respond)), options, NullLogger<PaystackGateway>.Instance);
    }

    private static HttpResponseMessage VerifyBody(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    private static string StatusBody(string status) =>
        $"{{\"status\":true,\"data\":{{\"status\":\"{status}\",\"reference\":\"R1\",\"amount\":500000,\"currency\":\"NGN\",\"gateway_response\":\"Declined by bank\"}}}}";

    [Theory]
    [InlineData("success", PaymentVerificationOutcome.Succeeded)]
    [InlineData("SUCCESS", PaymentVerificationOutcome.Succeeded)]
    [InlineData("failed", PaymentVerificationOutcome.Failed)]
    [InlineData("abandoned", PaymentVerificationOutcome.Failed)]
    [InlineData("reversed", PaymentVerificationOutcome.Failed)]
    [InlineData("pending", PaymentVerificationOutcome.Pending)]
    [InlineData("processing", PaymentVerificationOutcome.Pending)]
    [InlineData("queued", PaymentVerificationOutcome.Pending)]
    [InlineData("ongoing", PaymentVerificationOutcome.Pending)]
    [InlineData("brand-new-status", PaymentVerificationOutcome.Pending)]
    public async Task Verify_maps_documented_provider_statuses(string providerStatus, PaymentVerificationOutcome expected)
    {
        var gateway = Create(_ => VerifyBody(StatusBody(providerStatus)));

        var result = await gateway.VerifyAsync("R1");

        result.Outcome.Should().Be(expected);
        if (expected == PaymentVerificationOutcome.Succeeded)
        {
            result.AmountPaid.Should().Be(5000m);
            result.Currency.Should().Be("NGN");
            result.GatewayReference.Should().Be("R1");
        }
    }

    [Fact]
    public async Task Verify_reports_amount_and_currency_for_server_side_matching()
    {
        var gateway = Create(_ => VerifyBody(
            "{\"status\":true,\"data\":{\"status\":\"success\",\"reference\":\"R1\",\"amount\":40333,\"currency\":\"USD\"}}"));

        var result = await gateway.VerifyAsync("R1");

        result.Outcome.Should().Be(PaymentVerificationOutcome.Succeeded);
        result.AmountPaid.Should().Be(403.33m);
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task Verify_transport_error_is_pending_never_failed()
    {
        var gateway = Create(_ => throw new HttpRequestException("connection reset"));

        var result = await gateway.VerifyAsync("R1");

        result.Outcome.Should().Be(PaymentVerificationOutcome.Pending);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Verify_http_error_is_pending_never_failed(HttpStatusCode status)
    {
        var gateway = Create(_ => VerifyBody("{\"status\":false,\"message\":\"boom\"}", status));

        var result = await gateway.VerifyAsync("R1");

        result.Outcome.Should().Be(PaymentVerificationOutcome.Pending);
    }

    [Fact]
    public async Task Verify_malformed_json_is_pending_and_never_throws()
    {
        var gateway = Create(_ => VerifyBody("this is not json {{{"));

        var result = await gateway.VerifyAsync("R1");

        result.Outcome.Should().Be(PaymentVerificationOutcome.Pending);
    }

    [Fact]
    public async Task Verify_failure_message_prefers_short_gateway_response()
    {
        var gateway = Create(_ => VerifyBody(StatusBody("failed")));

        var result = await gateway.VerifyAsync("R1");

        result.Outcome.Should().Be(PaymentVerificationOutcome.Failed);
        result.Message.Should().Be("Declined by bank");
    }

    [Fact]
    public async Task Verify_abandoned_without_gateway_response_gets_neutral_copy()
    {
        var gateway = Create(_ => VerifyBody(
            "{\"status\":true,\"data\":{\"status\":\"abandoned\",\"reference\":\"R1\",\"amount\":500000,\"currency\":\"NGN\"}}"));

        var result = await gateway.VerifyAsync("R1");

        result.Outcome.Should().Be(PaymentVerificationOutcome.Failed);
        result.Message.Should().Be("The payment was not completed.");
    }
}
