using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Security response headers emitted by SecurityHeadersMiddleware (real pipeline).
/// The form-action gateway allowlist is load-bearing: checkout uses a hosted
/// 302 redirect to Paystack, and Chrome/Edge enforce form-action across that
/// redirect chain — without the gateway host the browser refuses to leave for
/// checkout (AI_HANDOFF §78-§79).
/// </summary>
public class SecurityHeadersTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SecurityHeadersTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Csp_AllowsSelf_AndPaystackCheckoutFormAction()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().ContainKey("Content-Security-Policy");

        var csp = string.Join("; ", response.Headers.GetValues("Content-Security-Policy"));
        csp.Should().Contain("form-action 'self' https://checkout.paystack.com");
        csp.Should().Contain("frame-src 'self' https://checkout.paystack.com");
        csp.Should().Contain("object-src 'none'");
    }
}
