using System.Collections.Concurrent;
using System.Diagnostics;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities.Diagnostics;
using DeChimex.Infrastructure.Diagnostics;
using DeChimex.Persistence.Context;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.DevPortal;

/// <summary>
/// In-memory request counters, aggregated per (5-minute bucket, method, route template) and
/// flushed to RequestMetricBuckets by <see cref="MetricsFlushService"/>. Only REAL requests
/// are counted (no sampling, no estimates). Static files, SignalR (/hubs) and rate-limited
/// requests rejected before routing are not counted.
/// </summary>
public sealed class RequestMetricsCollector(IOptions<DevPortalOptions> options)
{
    public static readonly TimeSpan BucketSize = TimeSpan.FromMinutes(5);
    private ConcurrentDictionary<(DateTime, string, string), Counter> _pending = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private long _total, _serverErrors;

    public int SlowThresholdMs => Math.Max(50, options.Value.SlowRequestMs);
    public DateTime StartedUtc => _startedUtc;
    public long TotalSinceStart => Interlocked.Read(ref _total);
    public long ServerErrorsSinceStart => Interlocked.Read(ref _serverErrors);

    public static DateTime BucketOf(DateTime utc)
        => new(utc.Ticks - utc.Ticks % BucketSize.Ticks, DateTimeKind.Utc);

    public void Record(string method, string route, int status, long ms)
    {
        var key = (BucketOf(DateTime.UtcNow), method, route);
        var c = _pending.GetOrAdd(key, _ => new Counter());
        lock (c)
        {
            c.Requests++;
            if (status >= 500) c.ServerErrors++;
            else if (status >= 400) c.ClientErrors++;
            if (ms >= SlowThresholdMs) c.Slow++;
            c.TotalMs += ms;
            if (ms > c.MaxMs) c.MaxMs = ms;
        }
        Interlocked.Increment(ref _total);
        if (status >= 500) Interlocked.Increment(ref _serverErrors);
    }

    /// <summary>Atomically takes everything recorded so far.</summary>
    public IReadOnlyList<PendingBucket> Drain()
    {
        var taken = Interlocked.Exchange(ref _pending, new ConcurrentDictionary<(DateTime, string, string), Counter>());
        return taken.Select(kv =>
        {
            lock (kv.Value)
                return new PendingBucket(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.Requests, kv.Value.ServerErrors,
                    kv.Value.ClientErrors, kv.Value.Slow, kv.Value.TotalMs, kv.Value.MaxMs);
        }).ToList();
    }

    /// <summary>Not-yet-flushed counters (so the statistics page is exact up to "now").</summary>
    public IReadOnlyList<PendingBucket> Peek() => _pending.Select(kv =>
    {
        lock (kv.Value)
            return new PendingBucket(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.Requests, kv.Value.ServerErrors,
                kv.Value.ClientErrors, kv.Value.Slow, kv.Value.TotalMs, kv.Value.MaxMs);
    }).ToList();

    /// <summary>Re-queues buckets whose flush failed (no data loss on a transient DB error).</summary>
    public void Restore(IEnumerable<PendingBucket> buckets)
    {
        foreach (var b in buckets)
        {
            var c = _pending.GetOrAdd((b.BucketStartUtc, b.Method, b.Route), _ => new Counter());
            lock (c)
            {
                c.Requests += b.Requests; c.ServerErrors += b.ServerErrors; c.ClientErrors += b.ClientErrors;
                c.Slow += b.Slow; c.TotalMs += b.TotalMs; if (b.MaxMs > c.MaxMs) c.MaxMs = b.MaxMs;
            }
        }
    }

    private sealed class Counter
    {
        public int Requests, ServerErrors, ClientErrors, Slow;
        public long TotalMs, MaxMs;
    }
}

public sealed record PendingBucket(DateTime BucketStartUtc, string Method, string Route, int Requests, int ServerErrors,
    int ClientErrors, int Slow, long TotalMs, long MaxMs);

/// <summary>
/// Outermost middleware: times every request and records it against the route TEMPLATE that
/// routing selected (captured by <see cref="CaptureRoute"/> right after UseRouting, before any
/// status-code re-execution), so /admin/orders/5 and /admin/orders/6 aggregate together and
/// no ids or query strings are stored.
/// </summary>
public sealed class RequestMetricsMiddleware(RequestDelegate next, RequestMetricsCollector collector)
{
    public const string RouteItemKey = "__devportal.route";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/hubs"))
        {
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        var status = 500;
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        finally
        {
            sw.Stop();
            if (context.Items.TryGetValue(RouteItemKey, out var r) && r is string route)
                collector.Record(context.Request.Method, route, status, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Runs after UseRouting: remembers the FIRST endpoint selected for this request.</summary>
    public static Task CaptureRoute(HttpContext context, Func<Task> nextStep)
    {
        if (!context.Items.ContainsKey(RouteItemKey))
            context.Items[RouteItemKey] = DescribeEndpoint(context.GetEndpoint());
        return nextStep();
    }

    public static string DescribeEndpoint(Endpoint? endpoint)
    {
        if (endpoint is null) return "(unmatched)";
        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action is not null)
        {
            var template = action.AttributeRouteInfo?.Template;
            var text = !string.IsNullOrEmpty(template) ? "/" + template : $"/{action.ControllerName}/{action.ActionName}";
            return text.Length > 256 ? text[..256] : text;
        }
        if (endpoint is RouteEndpoint re && re.RoutePattern.RawText is { } raw)
            return "/" + raw.TrimStart('/');
        return endpoint.DisplayName ?? "(endpoint)";
    }
}

/// <summary>Flushes the in-memory request counters to RequestMetricBuckets every 15 seconds.</summary>
public sealed class MetricsFlushService(RequestMetricsCollector collector, IServiceScopeFactory scopeFactory, IJobMonitor jobs)
    : BackgroundService
{
    public const string JobKey = "metrics-flush";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.Register(JobKey, "Request metrics flush", "Persists aggregated request counters (5-minute buckets).", Interval);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
            await FlushAsync(CancellationToken.None);
        }
        await FlushAsync(CancellationToken.None);
    }

    public async Task<int> FlushAsync(CancellationToken ct)
    {
        var drained = collector.Drain();
        using var run = jobs.Begin(JobKey);
        if (drained.Count == 0) { run.Succeeded("Nothing to flush"); return 0; }
        try
        {
            Shared.Diagnostics.TechnicalContext.CaptureSuppressed = true;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var starts = drained.Select(d => d.BucketStartUtc).Distinct().ToList();
            var existing = await db.RequestMetricBuckets.Where(b => starts.Contains(b.BucketStartUtc)).ToListAsync(ct);
            var map = existing.ToDictionary(b => (b.BucketStartUtc, b.Method, b.Route));
            foreach (var d in drained)
            {
                if (!map.TryGetValue((d.BucketStartUtc, d.Method, d.Route), out var bucket))
                {
                    bucket = RequestMetricBucket.Create(d.BucketStartUtc, d.Method, d.Route);
                    db.RequestMetricBuckets.Add(bucket);
                    map[(d.BucketStartUtc, d.Method, d.Route)] = bucket;
                }
                bucket.Add(d.Requests, d.ServerErrors, d.ClientErrors, d.Slow, d.TotalMs, d.MaxMs);
            }
            await db.SaveChangesAsync(ct);
            run.Succeeded($"{drained.Count} bucket(s) flushed");
            return drained.Count;
        }
        catch (Exception ex)
        {
            Shared.Diagnostics.TechnicalContext.CaptureSuppressed = false;
            collector.Restore(drained);
            run.Failed(ex, "Metrics flush failed; counters re-queued.");
            return 0;
        }
        finally
        {
            Shared.Diagnostics.TechnicalContext.CaptureSuppressed = false;
        }
    }
}
