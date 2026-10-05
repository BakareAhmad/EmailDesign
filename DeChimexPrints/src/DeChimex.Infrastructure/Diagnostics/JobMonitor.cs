using System.Collections.Concurrent;
using System.Diagnostics;
using DeChimex.Application.Common.Interfaces;

namespace DeChimex.Infrastructure.Diagnostics;

/// <summary>
/// In-process monitor for background work. Live state (running, last run, counters) is
/// transient since process start, which is exactly what "is it working right now" needs;
/// every failure is additionally recorded as a persisted technical event (category
/// BackgroundJob) so failure history survives restarts.
/// State: Idle (never ran) | Running | Healthy | Failing | Stale (a recurring job has not
/// completed within 3x its expected interval).
/// </summary>
public sealed class JobMonitor(ITechnicalEventRecorder recorder) : IJobMonitor
{
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);

    public void Register(string key, string name, string description, TimeSpan? expectedInterval = null, bool recurring = true)
        => _jobs.AddOrUpdate(key,
            _ => new JobState(key, name, description, expectedInterval, recurring),
            (_, existing) => { existing.Name = name; existing.Description = description; existing.Interval = expectedInterval; existing.Recurring = recurring; return existing; });

    public IJobRun Begin(string key)
    {
        var state = _jobs.GetOrAdd(key, k => new JobState(k, k, string.Empty, null, true));
        lock (state)
        {
            state.RunningCount++;
            state.LastStartedUtc = DateTime.UtcNow;
        }
        return new Run(this, state);
    }

    public IReadOnlyList<JobStatusSnapshot> Snapshot()
    {
        var now = DateTime.UtcNow;
        return _jobs.Values.Select(s =>
        {
            lock (s)
            {
                DateTime? next = s.Recurring && s.Interval is { } iv && (s.LastCompletedUtc ?? s.LastStartedUtc) is { } last
                    ? last + iv : null;
                string state;
                if (s.RunningCount > 0 && s.LastCompletedUtc is null) state = "Running";
                else if (s.ConsecutiveFailures > 0) state = "Failing";
                else if (s.Recurring && s.Interval is { } interval && s.RegisteredUtc < now - interval * 3
                         && (s.LastCompletedUtc is null || s.LastCompletedUtc < now - interval * 3)) state = "Stale";
                else if (s.LastCompletedUtc is null && s.RunningCount == 0) state = "Idle";
                else state = s.RunningCount > 0 ? "Running" : "Healthy";

                return new JobStatusSnapshot(s.Key, s.Name, s.Description, s.Recurring, s.Interval, s.RunningCount > 0,
                    s.LastStartedUtc, s.LastCompletedUtc, s.LastSuccessUtc, s.LastFailureUtc, s.LastFailureMessage,
                    s.SuccessCount, s.FailureCount, s.ConsecutiveFailures, s.LastDurationMs, s.MaxDurationMs, s.LastNote, next, state);
            }
        }).OrderBy(s => s.Name).ToList();
    }

    private void Complete(JobState s, double ms, bool ok, Exception? ex, string? message, string? note, bool recordEvent = true)
    {
        lock (s)
        {
            s.RunningCount = Math.Max(0, s.RunningCount - 1);
            s.LastCompletedUtc = DateTime.UtcNow;
            s.LastDurationMs = ms;
            if (s.MaxDurationMs is null || ms > s.MaxDurationMs) s.MaxDurationMs = ms;
            if (note is not null) s.LastNote = note;
            if (ok)
            {
                s.SuccessCount++;
                s.ConsecutiveFailures = 0;
                s.LastSuccessUtc = s.LastCompletedUtc;
            }
            else
            {
                s.FailureCount++;
                s.ConsecutiveFailures++;
                s.LastFailureUtc = s.LastCompletedUtc;
                s.LastFailureMessage = Shared.Diagnostics.SensitiveDataRedactor.Redact(message ?? ex?.Message, 500);
            }
        }

        if (!ok && recordEvent && !Shared.Diagnostics.TechnicalContext.CaptureSuppressed)
        {
            recorder.Record(new TechnicalEventEntry(
                TechnicalSeverity.Error, TechnicalCategory.BackgroundJob, TechnicalEventType.JobFailure,
                $"Background job '{s.Name}' failed: {message ?? ex?.Message ?? "unknown error"}",
                ExceptionType: ex?.GetType().FullName,
                StackTrace: ex?.ToString(),
                Source: s.Key,
                MessageTemplate: $"Background job '{s.Name}' failed",
                Properties: new Dictionary<string, string?> { ["job"] = s.Key, ["durationMs"] = ((long)ms).ToString() }));
        }
    }

    private sealed class JobState(string key, string name, string description, TimeSpan? interval, bool recurring)
    {
        public string Key { get; } = key;
        public string Name { get; set; } = name;
        public string Description { get; set; } = description;
        public TimeSpan? Interval { get; set; } = interval;
        public bool Recurring { get; set; } = recurring;
        public DateTime RegisteredUtc { get; } = DateTime.UtcNow;
        public int RunningCount;
        public DateTime? LastStartedUtc;
        public DateTime? LastCompletedUtc;
        public DateTime? LastSuccessUtc;
        public DateTime? LastFailureUtc;
        public string? LastFailureMessage;
        public long SuccessCount;
        public long FailureCount;
        public int ConsecutiveFailures;
        public double? LastDurationMs;
        public double? MaxDurationMs;
        public string? LastNote;
    }

    private sealed class Run(JobMonitor monitor, JobState state) : IJobRun
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private int _done;

        public void Succeeded(string? note = null)
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                monitor.Complete(state, _sw.Elapsed.TotalMilliseconds, true, null, null, note);
        }

        public void Failed(Exception? exception, string? message = null, bool recordEvent = true)
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                monitor.Complete(state, _sw.Elapsed.TotalMilliseconds, false, exception, message, null, recordEvent);
        }

        public void Dispose() => Succeeded();
    }
}
