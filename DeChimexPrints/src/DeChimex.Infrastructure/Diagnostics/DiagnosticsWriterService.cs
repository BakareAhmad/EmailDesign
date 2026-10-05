using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities.Diagnostics;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeChimex.Infrastructure.Diagnostics;

/// <summary>
/// Drains the <see cref="DiagnosticsQueue"/> and batch-inserts technical events and
/// integration calls (up to 500 rows per SaveChanges, roughly every 2 seconds). Runs with
/// capture suppressed so a failing diagnostics write can never recurse into itself; a failed
/// batch is counted and dropped (diagnostics are best-effort, the app is not).
/// </summary>
public sealed class DiagnosticsWriterService(
    DiagnosticsQueue queue,
    IServiceScopeFactory scopeFactory,
    IJobMonitor jobs,
    ILogger<DiagnosticsWriterService> logger) : BackgroundService
{
    public const string JobKey = "diagnostics-writer";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.Register(JobKey, "Diagnostics writer",
            "Persists queued technical events and integration calls in batches.", Interval);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TechnicalContext.CaptureSuppressed = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
            var written = await DrainAsync(CancellationToken.None);
            if (written == 0)
            {
                // Idle tick: still record a (cheap, in-memory) run so a healthy-but-idle writer
                // is never reported as Stale on the Jobs page.
                using var idle = jobs.Begin(JobKey);
                idle.Succeeded("idle: queue empty");
            }
        }
        // Final flush on shutdown (e.g. the SystemStop event).
        await DrainAsync(CancellationToken.None);
    }

    /// <summary>Writes everything currently queued. Public so tests / maintenance can force a flush.</summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        TechnicalContext.CaptureSuppressed = true;
        var total = 0;
        while (true)
        {
            var batch = new List<object>(500);
            while (batch.Count < 500 && queue.Reader.TryRead(out var item)) batch.Add(item);
            if (batch.Count == 0) return total;

            using var run = jobs.Begin(JobKey);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
                db.TechnicalEvents.AddRange(batch.OfType<TechnicalEvent>());
                db.IntegrationCalls.AddRange(batch.OfType<IntegrationCall>());
                await db.SaveChangesAsync(ct);
                queue.MarkWritten(batch.Count);
                total += batch.Count;
                run.Succeeded($"{batch.Count} row(s) written");
            }
            catch (Exception ex)
            {
                queue.MarkWriteFailure(batch.Count);
                // Mark failed without re-recording a technical event (it would be queued into the
                // same failing path). The job page shows the failure; the log file gets it too.
                run.Failed(null, "Batch write failed: " + ex.GetType().Name, recordEvent: false);
                logger.LogWarning(ex, "Diagnostics batch of {Count} rows could not be written; dropped.", batch.Count);
                return total;
            }
        }
    }
}
