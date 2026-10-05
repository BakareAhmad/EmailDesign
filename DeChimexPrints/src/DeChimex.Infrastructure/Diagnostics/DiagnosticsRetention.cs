using DeChimex.Application.Common.Interfaces;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Diagnostics;

public sealed record RetentionResult(int TechnicalEvents, int IntegrationCalls, int MetricBuckets);

/// <summary>
/// Deletes diagnostics rows older than the configured retention (technical events and
/// integration calls: DevPortal:RetentionDays; request metrics: DevPortal:MetricsRetentionDays).
/// Only the three diagnostics tables are ever touched. Idempotent: running it twice deletes
/// nothing the second time. Uses set-based deletes on relational providers.
/// </summary>
public sealed class DiagnosticsRetention(DeChimexDbContext db, IOptions<DevPortalOptions> options)
{
    public async Task<RetentionResult> ApplyAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var eventCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, o.RetentionDays));
        var metricCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, o.MetricsRetentionDays));

        if (db.Database.IsRelational())
        {
            var e = await db.TechnicalEvents.Where(x => x.TimestampUtc < eventCutoff).ExecuteDeleteAsync(ct);
            var c = await db.IntegrationCalls.Where(x => x.TimestampUtc < eventCutoff).ExecuteDeleteAsync(ct);
            var m = await db.RequestMetricBuckets.Where(x => x.BucketStartUtc < metricCutoff).ExecuteDeleteAsync(ct);
            return new RetentionResult(e, c, m);
        }

        // InMemory provider (preview/tests) has no set-based delete.
        var events = await db.TechnicalEvents.Where(x => x.TimestampUtc < eventCutoff).ToListAsync(ct);
        var calls = await db.IntegrationCalls.Where(x => x.TimestampUtc < eventCutoff).ToListAsync(ct);
        var buckets = await db.RequestMetricBuckets.Where(x => x.BucketStartUtc < metricCutoff).ToListAsync(ct);
        db.TechnicalEvents.RemoveRange(events);
        db.IntegrationCalls.RemoveRange(calls);
        db.RequestMetricBuckets.RemoveRange(buckets);
        await db.SaveChangesAsync(ct);
        return new RetentionResult(events.Count, calls.Count, buckets.Count);
    }
}

/// <summary>Applies diagnostics retention shortly after start and then hourly.</summary>
public sealed class DiagnosticsRetentionService(IServiceScopeFactory scopeFactory, IJobMonitor jobs) : BackgroundService
{
    public const string JobKey = "diagnostics-retention";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.Register(JobKey, "Diagnostics retention",
            "Deletes technical events, integration calls and request metrics older than the retention window.", Interval);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var run = jobs.Begin(JobKey))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var r = await scope.ServiceProvider.GetRequiredService<DiagnosticsRetention>().ApplyAsync(stoppingToken);
                    run.Succeeded($"Deleted {r.TechnicalEvents} events, {r.IntegrationCalls} calls, {r.MetricBuckets} buckets");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { run.Failed(ex); }
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
