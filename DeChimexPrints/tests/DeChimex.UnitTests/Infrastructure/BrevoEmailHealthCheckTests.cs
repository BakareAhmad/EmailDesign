using System.Net;
using System.Text;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Email;
using DeChimex.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

/// <summary>
/// Exercises the real BrevoEmailHealthCheck over a stubbed HTTP handler: the free plan's
/// exhausted daily quota is the failure mode the owner actually hit (Brevo returns 2xx but
/// never delivers), so the check must report it instead of claiming a healthy connection.
/// </summary>
public class BrevoEmailHealthCheckTests
{
    private const string AccountWithZeroQuota =
        """{"email":"bakareahmadio@gmail.com","plan":[{"type":"free","credits":0,"creditsType":"sendLimit"}]}""";

    private const string AccountWithQuotaLeft =
        """{"email":"bakareahmadio@gmail.com","plan":[{"type":"free","credits":217,"creditsType":"sendLimit"}]}""";

    private const string IpRejection =
        """{"message":"We have detected you are using an unrecognised IP address 102.89.44.7. Please add the new IP address in this link: https://app.brevo.com/security/authorised_ips","code":"unauthorized"}""";

    private static (BrevoEmailHealthCheck Check, StubHandler Handler, IEmailService Email) Create(
        string accountJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(status, accountJson);
        var options = Options.Create(new BrevoOptions { ApiKey = "test-key", Enabled = true });
        var email = Substitute.For<IEmailService>();
        email.TrySendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(true);
        var renderer = new EmailTemplateRenderer(Options.Create(new DeChimex.Infrastructure.Options.AppOptions()));
        var check = new BrevoEmailHealthCheck(new HttpClient(handler), options, email, renderer,
            NullLogger<BrevoEmailHealthCheck>.Instance);
        return (check, handler, email);
    }

    [Fact]
    public async Task WhenFreeQuotaExhausted_ReportsLimitInsteadOfHealthy()
    {
        var (check, handler, email) = Create(AccountWithZeroQuota);

        var result = await check.CheckAsync(null);

        result.Healthy.Should().BeFalse();
        result.Status.Should().Contain("Daily email limit reached");
        result.Detail.Should().Contain("300 emails per day");
        result.FixUrl.Should().Be("https://www.brevo.com/pricing/");
        handler.RequestedPath.Should().EndWith("/account");
    }

    [Fact]
    public async Task WhenQuotaRemains_ReportsHealthyWithRemainingCount()
    {
        var (check, _, _) = Create(AccountWithQuotaLeft);

        var result = await check.CheckAsync(null);

        result.Healthy.Should().BeTrue();
        result.Status.Should().Be("Connected to Brevo");
        result.Detail.Should().Contain("217 sends remaining today");
    }

    [Fact]
    public async Task WhenQuotaExhausted_DoesNotWasteATestSend()
    {
        // A test send while out of quota would be accepted (2xx) yet never delivered, so
        // the check must refuse to run it and report the quota instead.
        var (check, _, email) = Create(AccountWithZeroQuota);

        var result = await check.CheckAsync("owner@dechimexprinthub.com");

        result.Healthy.Should().BeFalse();
        result.Status.Should().Contain("Daily email limit reached");
        await email.DidNotReceive().TrySendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenQuotaRemains_SendsTestEmailAndReportsAcceptance()
    {
        var (check, _, email) = Create(AccountWithQuotaLeft);

        var result = await check.CheckAsync("owner@dechimexprinthub.com");

        result.Healthy.Should().BeTrue();
        result.Status.Should().Be("Test email accepted for owner@dechimexprinthub.com");
        await email.Received(1).TrySendAsync(
            Arg.Is<EmailMessage>(m => m.ToEmail == "owner@dechimexprinthub.com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenIpRejected_SurfacesBrevoMessageAndDashboardLink()
    {
        var (check, _, _) = Create(IpRejection, HttpStatusCode.Unauthorized);

        var result = await check.CheckAsync(null);

        result.Healthy.Should().BeFalse();
        result.Status.Should().Contain("blocking this server");
        result.Detail.Should().Contain("102.89.44.7");
        result.FixUrl.Should().Be("https://app.brevo.com/security/authorised_ips");
    }

    [Fact]
    public async Task WhenProviderUnreachable_ReportsNetworkErrorWithoutThrowing()
    {
        var handler = new ThrowingHandler();
        var options = Options.Create(new BrevoOptions { ApiKey = "test-key", Enabled = true });
        var check = new BrevoEmailHealthCheck(new HttpClient(handler), options,
            Substitute.For<IEmailService>(),
            new EmailTemplateRenderer(Options.Create(new DeChimex.Infrastructure.Options.AppOptions())),
            NullLogger<BrevoEmailHealthCheck>.Instance);

        var result = await check.CheckAsync("owner@dechimexprinthub.com");

        result.Healthy.Should().BeFalse();
        result.Status.Should().Be("Could not reach Brevo");
    }

    [Fact]
    public async Task WhenEmailDisabled_SaysSoWithoutCallingTheProvider()
    {
        var handler = new StubHandler(HttpStatusCode.OK, AccountWithQuotaLeft);
        var options = Options.Create(new BrevoOptions { ApiKey = "test-key", Enabled = false });
        var check = new BrevoEmailHealthCheck(new HttpClient(handler), options,
            Substitute.For<IEmailService>(),
            new EmailTemplateRenderer(Options.Create(new DeChimex.Infrastructure.Options.AppOptions())),
            NullLogger<BrevoEmailHealthCheck>.Instance);

        var result = await check.CheckAsync(null);

        result.Healthy.Should().BeFalse();
        result.Status.Should().Contain("disabled or not configured");
        handler.Calls.Should().Be(0);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string RequestedPath { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            RequestedPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
