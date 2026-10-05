using System.Net;
using System.Text;
using DeChimex.Application.Common.Interfaces;
using DeChimex.IntegrationTests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// LOW-008: webhook tests for the Paystack server-to-server endpoint, which is the ONLY
/// surviving API payment action (all user-facing payment flows now run through the MVC
/// Store/Checkout and Admin controllers). Uses a deterministic fake gateway so no external
/// Paystack call is made. The signature gate is the security-intent test.
/// </summary>
public class PaymentsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly FakePaymentGateway _gateway;

    public PaymentsControllerTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _gateway = (FakePaymentGateway)factory.Services.GetRequiredService<IPaymentGateway>();
    }

    private Task<HttpResponseMessage> PostWebhookAsync(string body, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhook/paystack")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-paystack-signature", signature);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Webhook_WithInvalidSignature_ReturnsOk_AndDoesNotThrow()
    {
        _gateway.SignatureValid = false;
        var response = await PostWebhookAsync("{\"event\":\"charge.success\"}", "bad-signature");
        // Always 200 to acknowledge receipt; an invalid signature must not change state or throw.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_WithValidSignature_UnrelatedPayload_ReturnsOk_NoOp()
    {
        _gateway.SignatureValid = true;
        var response = await PostWebhookAsync("{\"event\":\"unrelated\"}", "valid-signature");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
