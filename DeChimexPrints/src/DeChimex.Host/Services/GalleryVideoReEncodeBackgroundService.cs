using Microsoft.Extensions.Options;

namespace DeChimex.Host.Services;

/// <summary>
/// Runs the gallery-video re-encode backfill once shortly after startup when
/// <c>Gallery:ReEncodeExistingVideos</c> is true (the default). This fixes legacy video items that
/// were uploaded before the upload-time H.264 transcode existed. Idempotent (already fast-start H.264
/// files are skipped), so it is safe to leave enabled; it re-encodes only what still needs it.
/// </summary>
public sealed class GalleryVideoReEncodeBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<GalleryBackfillOptions> options,
    ILogger<GalleryVideoReEncodeBackgroundService> logger,
    DeChimex.Application.Common.Interfaces.IJobMonitor jobs) : BackgroundService
{
    public const string JobKey = "gallery-video-backfill";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        jobs.Register(JobKey, "Gallery video re-encode backfill",
            options.Value.ReEncodeExistingVideos ? "One-shot at startup: re-encodes legacy gallery videos." : "Disabled (Gallery:ReEncodeExistingVideos=false).",
            expectedInterval: null, recurring: false);
        if (!options.Value.ReEncodeExistingVideos)
        {
            logger.LogInformation("Gallery video re-encode backfill is disabled (Gallery:ReEncodeExistingVideos=false).");
            return;
        }

        // Let the host finish starting + seeding before touching the gallery.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var run = jobs.Begin(JobKey);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<GalleryVideoReEncodeService>();
            var result = await service.RunAsync(stoppingToken);
            run.Succeeded($"{result.TotalVideos} videos, {result.ReEncoded} re-encoded, {result.Failed} failed");
            logger.LogInformation(
                "Gallery video backfill finished: {Total} videos, {ReEncoded} re-encoded, {Already} already playable, {Missing} missing, {Failed} failed.",
                result.TotalVideos, result.ReEncoded, result.AlreadyPlayable, result.MissingFiles, result.Failed);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        catch (Exception ex)
        {
            run.Failed(ex, "Gallery video re-encode backfill failed", recordEvent: false);
            logger.LogError(ex, "Gallery video re-encode backfill failed.");
        }
    }
}

/// <summary>Configuration for the gallery-video backfill background task.</summary>
public sealed class GalleryBackfillOptions
{
    public const string SectionName = "Gallery";
    public bool ReEncodeExistingVideos { get; init; } = true;
}
