using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Services;

/// <summary>
/// Processes QUEUED marketing broadcasts in bounded batches: for each pending recipient
/// it re-checks consent (they may have unsubscribed since queue time), renders the
/// marketing email with their personal unsubscribe link and sends it via IEmailService.
///
/// Idempotency / at-most-once guarantees:
///  - recipient rows only ever move Pending -> Sent/Failed (Sent rows are never re-picked),
///    so worker restarts and retries never duplicate an already-sent email;
///  - progress is committed after every batch, so a crash resumes where it left off;
///  - a per-recipient failure marks only THAT recipient failed - the broadcast still
///    completes (failed rows can be retried explicitly by an admin).
///
/// The admin's Send action only flips the broadcast to Queued; all sending happens here,
/// off the web request.
/// </summary>
public sealed class MarketingBroadcastProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<MarketingOptions> options,
    ILogger<MarketingBroadcastProcessor> logger)
{
    private readonly MarketingOptions _options = options.Value;

    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        if (!_options.AllowBroadcasts)
        {
            // Development safeguard: broadcasts stay Queued until explicitly armed.
            logger.LogWarning("Marketing broadcasts are disabled (Marketing:AllowBroadcasts=false); {Count} queued broadcast(s) waiting.",
                await CountQueuedAsync(ct));
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.Context.DeChimexDbContext>();
        var emails = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var renderer = scope.ServiceProvider.GetRequiredService<IEmailTemplateRenderer>();
        var urls = scope.ServiceProvider.GetRequiredService<IAppUrlProvider>();

        var broadcastIds = await db.MarketingBroadcasts
            .Where(b => b.Status == MarketingBroadcastStatus.Queued || b.Status == MarketingBroadcastStatus.Sending)
            .Select(b => b.Id)
            .ToListAsync(ct);

        foreach (var broadcastId in broadcastIds)
            await ProcessOneAsync(db, emails, renderer, urls, broadcastId, ct);
    }

    private async Task<int> CountQueuedAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.Context.DeChimexDbContext>();
        return await db.MarketingBroadcasts.CountAsync(
            b => b.Status == MarketingBroadcastStatus.Queued || b.Status == MarketingBroadcastStatus.Sending, ct);
    }

    private async Task ProcessOneAsync(
        Persistence.Context.DeChimexDbContext db,
        IEmailService emails,
        IEmailTemplateRenderer renderer,
        IAppUrlProvider urls,
        Guid broadcastId,
        CancellationToken ct)
    {
        // Re-fetch WITH tracking; recipients are tracked rows we mutate per delivery.
        var broadcast = await db.MarketingBroadcasts
            .Include(b => b.Recipients)
            .FirstOrDefaultAsync(b => b.Id == broadcastId, ct);
        if (broadcast is null) return;

        if (broadcast.Status == MarketingBroadcastStatus.Queued)
        {
            broadcast.MarkSending();
            await db.SaveChangesAsync(ct);
        }

        var customers = await db.Customers
            .AsNoTracking()
            .Where(c => !c.IsGuest && c.MarketingSubscribed && c.Email != null)
            .ToDictionaryAsync(c => c.Id, c => c.UnsubscribeToken, ct);

        while (!ct.IsCancellationRequested)
        {
            var batch = broadcast.Recipients
                .Where(r => r.Status == MarketingRecipientStatus.Pending)
                .Take(_options.BatchSize)
                .ToList();
            if (batch.Count == 0) break;

            foreach (var recipient in batch)
            {
                try
                {
                    // Consent re-check at send time: unsubscribes between queue and send are honoured.
                    if (!customers.TryGetValue(recipient.CustomerId, out var unsubscribeToken))
                    {
                        recipient.MarkFailed("Customer unsubscribed or no longer eligible before send.");
                        continue;
                    }

                    var content = renderer.RenderMarketing(broadcast.Subject, broadcast.HtmlContent,
                        new Dictionary<string, string>
                        {
                            ["FirstName"] = recipient.FirstName,
                            ["UnsubscribeLink"] = urls.BuildMarketingUnsubscribeLink(unsubscribeToken)
                        });

                    var delivered = await emails.TrySendAsync(
                        new EmailMessage(recipient.Email, recipient.FirstName,
                            content.Subject, content.HtmlBody, content.PlainTextBody, Template: "Marketing", Category: "marketing"), ct);

                    if (delivered) recipient.MarkSent();
                    else recipient.MarkFailed("Email provider rejected the send.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Marketing broadcast {BroadcastId}: send to {Email} failed.", broadcastId, recipient.Email);
                    recipient.MarkFailed(ex.Message);
                }
            }

            // Commit progress after every batch: a crash/restart resumes with no duplicates.
            await db.SaveChangesAsync(ct);
        }

        if (broadcast.PendingCount == 0)
        {
            broadcast.MarkCompleted();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Marketing broadcast '{Subject}' completed: {Sent} sent, {Failed} failed.",
                broadcast.Subject, broadcast.SentCount, broadcast.FailedCount);
        }
    }
}

/// <summary>
/// Polls for queued marketing broadcasts and hands them to the processor. A failure in
/// one poll cycle is logged and retried on the next tick; it never crashes the host.
/// </summary>
public sealed class MarketingBroadcastBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<Infrastructure.Options.MarketingOptions> options,
    ILogger<MarketingBroadcastBackgroundService> logger,
    DeChimex.Application.Common.Interfaces.IJobMonitor jobs) : BackgroundService
{
    public const string JobKey = "marketing-broadcast";

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.Register(JobKey, "Marketing broadcast worker",
            $"Sends queued marketing broadcasts in batches (sending {(options.Value.AllowBroadcasts ? "ARMED" : "DISABLED")}).",
            TimeSpan.FromSeconds(Math.Max(5, options.Value.PollIntervalSeconds)));
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.PollIntervalSeconds));
        logger.LogInformation("Marketing broadcast worker started (poll every {Seconds}s, sending {State}).",
            interval.TotalSeconds, options.Value.AllowBroadcasts ? "ARMED" : "DISABLED");

        while (!stoppingToken.IsCancellationRequested)
        {
            using var run = jobs.Begin(JobKey);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<MarketingBroadcastProcessor>();
                await processor.ProcessPendingAsync(stoppingToken);
                run.Succeeded();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                run.Failed(ex, "Marketing broadcast poll cycle failed", recordEvent: false);
                logger.LogError(ex, "Marketing broadcast poll cycle failed; retrying next tick.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
