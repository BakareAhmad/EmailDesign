using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Email;

/// <summary>Tunable outbox processor cadence. The default suits production; the integration
/// test host lowers the poll interval so email assertions stay fast.</summary>
public sealed class EmailOutboxOptions
{
    public const string SectionName = "Email:Outbox";

    /// <summary>How often the processor looks for due Pending rows.</summary>
    public int PollIntervalMilliseconds { get; init; } = 2_000;

    /// <summary>Base delay for the exponential retry backoff (delay doubles per attempt).</summary>
    public int RetryBaseDelayMilliseconds { get; init; } = 30_000;

    /// <summary>Rows processed per drain cycle.</summary>
    public int BatchSize { get; init; } = 20;
}

/// <summary>
/// Drains the durable email outbox (AUD-003): picks up due Pending rows, sends them via
/// IEmailService and records per-row success/failure with exponential backoff. Rows that
/// exhaust <see cref="EmailOutbox.MaxAttempts"/> park as Failed (visible, not retried), so
/// no transactional email can ever be lost silently again. Modelled on the marketing
/// broadcast worker: progress committed per batch, crash-safe, job-monitor visible.
/// </summary>
public sealed class OutboxEmailProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<EmailOutboxOptions> options,
    IJobMonitor jobs,
    ILogger<OutboxEmailProcessor> logger) : BackgroundService
{
    public const string JobKey = "email-outbox";

    private readonly EmailOutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        jobs.Register(JobKey, "Email outbox",
            "Dispatches transactional emails from the durable outbox with retries (one run per drain cycle).",
            expectedInterval: null, recurring: true);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(200, _options.PollIntervalMilliseconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email outbox drain failed; retrying on the next tick.");
            }
        }
    }

    /// <summary>Sends every due Pending row (public for tests). Returns rows processed.</summary>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        List<Guid> dueIds;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimex.Persistence.Context.DeChimexDbContext>();
            dueIds = await db.EmailOutboxes.AsNoTracking()
                .Where(o => o.Status == EmailOutboxStatus.Pending && o.NextAttemptAtUtc <= now)
                .OrderBy(o => o.NextAttemptAtUtc)
                .Take(_options.BatchSize)
                .Select(o => o.Id)
                .ToListAsync(ct);
        }
        if (dueIds.Count == 0) return 0;

        var processed = 0;
        foreach (var id in dueIds)
        {
            if (ct.IsCancellationRequested) break;
            processed++;
            await SendOneAsync(id, ct);
        }
        return processed;
    }

    private async Task SendOneAsync(Guid id, CancellationToken ct)
    {
        using var run = jobs.Begin(JobKey); // one monitored run per drain attempt (per row keeps the portal truthful)
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeChimex.Persistence.Context.DeChimexDbContext>();
            var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

            // Re-fetch tracked; skip if a concurrent drain already handled it.
            var row = await db.EmailOutboxes.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (row is null || row.Status != EmailOutboxStatus.Pending || row.NextAttemptAtUtc > DateTime.UtcNow)
                return;

            await email.SendAsync(new EmailMessage(
                row.ToEmail, row.ToName, row.Subject, row.HtmlBody, row.PlainTextBody,
                row.Template, row.Category), CancellationToken.None);

            row.MarkSent(DateTime.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            run.Succeeded(row.Template);
        }
        catch (Exception ex)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeChimex.Persistence.Context.DeChimexDbContext>();
            var row = await db.EmailOutboxes.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (row is not null)
            {
                row.RecordFailure(ex.Message, DateTime.UtcNow,
                    TimeSpan.FromMilliseconds(_options.RetryBaseDelayMilliseconds));
                await db.SaveChangesAsync(CancellationToken.None);
            }
            run.Failed(ex, $"Outbox email ({row?.Template ?? "unspecified"}) could not be dispatched: {ex.GetType().Name}", recordEvent: false);
            logger.LogError(ex, "Failed to dispatch outbox email {Id} to {To}.", id, row?.ToEmail);
        }
    }
}
