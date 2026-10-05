using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities.Diagnostics;
using DeChimex.Infrastructure.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Diagnostics;

public class TechnicalEventRecorderTests
{
    private static (TechnicalEventRecorder Recorder, DiagnosticsQueue Queue) Create(HttpContext? http)
    {
        var queue = new DiagnosticsQueue(Options.Create(new DevPortalOptions()));
        var accessor = new HttpContextAccessor { HttpContext = http };
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return (new TechnicalEventRecorder(queue, accessor, env, Array.Empty<ITechnicalEventObserver>()), queue);
    }

    private static TechnicalEvent Single(DiagnosticsQueue q)
    {
        q.Reader.TryRead(out var item).Should().BeTrue("the event must be queued");
        return item.Should().BeOfType<TechnicalEvent>().Subject;
    }

    [Fact]
    public void Event_raised_after_the_request_completed_is_still_recorded()
    {
        // Fire-and-forget work (the email queue's Task.Run) still sees the originating request's
        // HttpContext through the async-local accessor. Once the response has completed, Kestrel
        // recycles that context and touching Request/User throws. The event must survive that.
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/Account/ForgotPassword";
        http.Uninitialize(); // what Kestrel does when the request finishes

        var (recorder, queue) = Create(http);
        recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Error, TechnicalCategory.Email, TechnicalEventType.Log,
            "Brevo send failed (InternalServerError)"));

        var e = Single(queue);
        e.Message.Should().Contain("Brevo send failed");
        e.Route.Should().BeNull("the recycled request context is ignored");
        queue.CaptureFailures.Should().Be(0);
    }

    [Fact]
    public void Live_request_context_enriches_route()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/dev/logs";
        var (recorder, queue) = Create(http);
        recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Warning, TechnicalCategory.Security, TechnicalEventType.Log, "x"));
        Single(queue).Route.Should().Be("GET /dev/logs");
    }
}
