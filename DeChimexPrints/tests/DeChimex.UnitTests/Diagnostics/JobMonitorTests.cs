using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Diagnostics;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Diagnostics;

public class JobMonitorTests
{
    private sealed class Recorder : ITechnicalEventRecorder
    {
        public List<TechnicalEventEntry> Events { get; } = new();
        public void Record(TechnicalEventEntry entry) => Events.Add(entry);
    }

    [Fact]
    public void Registered_job_is_idle_until_it_runs_then_healthy()
    {
        var m = new JobMonitor(new Recorder());
        m.Register("j", "Job", "desc", TimeSpan.FromMinutes(5));
        m.Snapshot().Single().State.Should().Be("Idle");
        using (var run = m.Begin("j")) run.Succeeded("3 rows");
        var s = m.Snapshot().Single();
        s.State.Should().Be("Healthy");
        s.SuccessCount.Should().Be(1);
        s.LastNote.Should().Be("3 rows");
        s.NextExpectedUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Failures_mark_the_job_failing_record_an_event_and_recover_on_success()
    {
        var rec = new Recorder();
        var m = new JobMonitor(rec);
        m.Register("j", "Job", "desc", TimeSpan.FromMinutes(1));
        using (var run = m.Begin("j")) run.Failed(new InvalidOperationException("boom"), "Run failed");
        using (var run = m.Begin("j")) run.Failed(null, "Quiet failure", recordEvent: false);
        var s = m.Snapshot().Single();
        s.State.Should().Be("Failing");
        s.FailureCount.Should().Be(2);
        s.ConsecutiveFailures.Should().Be(2);
        s.LastFailureMessage.Should().Contain("Quiet failure");
        rec.Events.Should().ContainSingle(e => e.Category == TechnicalCategory.BackgroundJob && e.Severity == TechnicalSeverity.Error);

        using (var run = m.Begin("j")) run.Succeeded();
        m.Snapshot().Single().State.Should().Be("Healthy");
        m.Snapshot().Single().ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void A_run_disposed_without_an_outcome_counts_as_completed()
    {
        var m = new JobMonitor(new Recorder());
        m.Register("once", "One-shot", "desc", null, recurring: false);
        using (m.Begin("once")) { }
        m.Snapshot().Single().State.Should().NotBe("Idle");
        m.Snapshot().Single().NextExpectedUtc.Should().BeNull("one-shot jobs have no next run");
    }
}
