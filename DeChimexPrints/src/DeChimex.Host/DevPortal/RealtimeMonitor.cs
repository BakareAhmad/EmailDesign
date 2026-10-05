using System.Collections.Concurrent;
using DeChimex.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace DeChimex.Host.DevPortal;

/// <summary>
/// Live SignalR statistics (transient, since process start): open connections per hub,
/// connects, abnormal disconnects and hub-method failures. Failures are also recorded as
/// technical events (category Realtime).
/// </summary>
public sealed class RealtimeMonitor
{
    private readonly ConcurrentDictionary<string, HubStats> _hubs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<DateTime> _recentFailures = new();

    public HubStats For(string hub) => _hubs.GetOrAdd(hub, _ => new HubStats());
    public IReadOnlyDictionary<string, HubStats> Hubs => _hubs;

    public void Failure()
    {
        _recentFailures.Enqueue(DateTime.UtcNow);
        while (_recentFailures.Count > 500 && _recentFailures.TryDequeue(out _)) { }
    }

    public int FailuresSince(DateTime utc) => _recentFailures.Count(t => t >= utc);

    public sealed class HubStats
    {
        public long Open;
        public long Connects;
        public long Disconnects;
        public long AbnormalDisconnects;
        public long Invocations;
        public long InvocationFailures;
        public DateTime? LastFailureUtc;
        public string? LastFailure;
    }
}

/// <summary>Global hub filter (registered via AddSignalR(o =&gt; o.AddFilter(...))): counts
/// connections and records hub failures. Pass-through: never changes hub behaviour.</summary>
public sealed class RealtimeMonitorHubFilter(RealtimeMonitor monitor, ITechnicalEventRecorder recorder) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext ctx, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var stats = monitor.For(ctx.Hub.GetType().Name);
        Interlocked.Increment(ref stats.Invocations);
        try
        {
            return await next(ctx);
        }
        catch (Exception ex) when (ex is not HubException)
        {
            Interlocked.Increment(ref stats.InvocationFailures);
            stats.LastFailureUtc = DateTime.UtcNow;
            stats.LastFailure = $"{ctx.HubMethodName}: {ex.GetType().Name}";
            monitor.Failure();
            recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Error, TechnicalCategory.Realtime,
                TechnicalEventType.RealtimeFailure, $"Hub method {ctx.Hub.GetType().Name}.{ctx.HubMethodName} failed: {ex.Message}",
                ExceptionType: ex.GetType().FullName, StackTrace: ex.ToString(), Source: ctx.Hub.GetType().FullName,
                MessageTemplate: $"Hub method {ctx.Hub.GetType().Name}.{ctx.HubMethodName} failed"));
            throw;
        }
    }

    public async Task OnConnectedAsync(HubLifetimeContext ctx, Func<HubLifetimeContext, Task> next)
    {
        var stats = monitor.For(ctx.Hub.GetType().Name);
        await next(ctx);
        Interlocked.Increment(ref stats.Connects);
        Interlocked.Increment(ref stats.Open);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext ctx, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        var stats = monitor.For(ctx.Hub.GetType().Name);
        Interlocked.Decrement(ref stats.Open);
        Interlocked.Increment(ref stats.Disconnects);
        if (exception is not null) Interlocked.Increment(ref stats.AbnormalDisconnects);
        await next(ctx, exception);
    }
}
