using DeChimex.Host.DevPortal;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>Pure classification rules found during the sandbox E2E.</summary>
public class DevClassificationTests
{
    private static HealthReportEntry Entry(HealthStatus s, Exception? ex, params string[] tags)
        => new(s, "probe", TimeSpan.FromMilliseconds(3), ex, null, tags);

    [Fact]
    public void Unreachable_external_provider_is_Unavailable_for_the_developer()
    {
        DevStatus.From(Entry(HealthStatus.Degraded, new HttpRequestException("Connection refused"), "external", "paystack"))
            .Should().Be(DevStatus.Unavailable);
        DevStatus.From(Entry(HealthStatus.Degraded, new TaskCanceledException("timeout"), "external"))
            .Should().Be(DevStatus.Unavailable);
    }

    [Fact]
    public void Other_statuses_map_unchanged()
    {
        DevStatus.From(Entry(HealthStatus.Healthy, null, "external")).Should().Be(DevStatus.Healthy);
        DevStatus.From(Entry(HealthStatus.Degraded, null, "external")).Should().Be(DevStatus.Degraded, "reachable but degraded");
        DevStatus.From(Entry(HealthStatus.Degraded, new HttpRequestException("x"), "dev")).Should().Be(DevStatus.Degraded, "only external checks are re-mapped");
        DevStatus.From(Entry(HealthStatus.Unhealthy, null, "ready")).Should().Be(DevStatus.Unavailable);
    }

    [Fact]
    public void Expected_business_exceptions_are_not_technical_errors()
    {
        TechnicalEventSink.IsExpectedBusinessException(new DeChimex.Application.Common.Exceptions.ConflictException("cart", "Custom products cannot be added to the cart."))
            .Should().BeTrue();
        TechnicalEventSink.IsExpectedBusinessException(new DeChimex.Application.Common.Exceptions.UnauthorizedAppException("Invalid credentials."))
            .Should().BeTrue();
        TechnicalEventSink.IsExpectedBusinessException(new DeChimex.Domain.Exceptions.BusinessRuleValidationException("x", "y"))
            .Should().BeTrue();
        TechnicalEventSink.IsExpectedBusinessException(new OperationCanceledException()).Should().BeTrue("the client aborted the request");
        TechnicalEventSink.IsExpectedBusinessException(new TaskCanceledException()).Should().BeTrue();
        TechnicalEventSink.IsExpectedBusinessException(new InvalidOperationException("bug")).Should().BeFalse();
        TechnicalEventSink.IsExpectedBusinessException(new NullReferenceException()).Should().BeFalse();
        TechnicalEventSink.IsExpectedBusinessException(null).Should().BeFalse();
    }
}
