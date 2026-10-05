using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Services;

/// <summary>
/// One-time/on-demand backfill that re-encodes every EXISTING gallery video to a browser-playable
/// codec (H.264 + fast start). Uploaded videos are already handled at upload time; this covers the
/// legacy files created before that fix, which browsers refuse to decode (so the play button never
/// works). Idempotent: already fast-start H.264 files are left untouched.
/// Iterates the gallery repository and resolves each media URL to its physical file under the same
/// storage root the app uses, then calls <see cref="IVideoEncodeService.MakeBrowserPlayableAsync"/>.
/// </summary>
public sealed class GalleryVideoReEncodeService
{
    private readonly IGalleryRepository _gallery;
    private readonly IVideoEncodeService _encoder;
    private readonly IWebHostEnvironment _env;
    private readonly StorageOptions _storage;
    private readonly ILogger<GalleryVideoReEncodeService> _logger;

    public GalleryVideoReEncodeService(
        IGalleryRepository gallery,
        IVideoEncodeService encoder,
        IWebHostEnvironment env,
        IOptions<StorageOptions> storage,
        ILogger<GalleryVideoReEncodeService> logger)
    {
        _gallery = gallery;
        _encoder = encoder;
        _env = env;
        _storage = storage.Value;
        _logger = logger;
    }

    public async Task<GalleryReEncodeResult> RunAsync(CancellationToken ct = default)
    {
        var media = await _gallery.ListAsync(ct);
        var videos = media.Where(m => m.Type == GalleryMediaType.Video).ToList();

        var result = new GalleryReEncodeResult { TotalVideos = videos.Count };

        foreach (var video in videos)
        {
            var path = ResolvePhysicalPath(video.Url);
            if (path is null || !File.Exists(path))
            {
                result.MissingFiles++;
                _logger.LogWarning("Gallery video {Id} references missing file {Url}; skipped.", video.Id, video.Url);
                continue;
            }

            try
            {
                if (await _encoder.MakeBrowserPlayableAsync(path, ct))
                {
                    result.ReEncoded++;
                    _logger.LogInformation("Re-encoded gallery video {Id} ({Url}).", video.Id, video.Url);
                }
                else
                {
                    result.AlreadyPlayable++;
                    _logger.LogDebug("Gallery video {Id} already browser-playable ({Url}).", video.Id, video.Url);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                _logger.LogError(ex, "Failed to re-encode gallery video {Id} ({Url}).", video.Id, video.Url);
            }
        }

        _logger.LogInformation(
            "Gallery video backfill complete: {Total} videos, {ReEncoded} re-encoded, {Already} already playable, {Missing} missing, {Failed} failed.",
            result.TotalVideos, result.ReEncoded, result.AlreadyPlayable, result.MissingFiles, result.Failed);

        return result;
    }

    /// <summary>Maps a public media URL to the physical file path under the storage root.</summary>
    private string? ResolvePhysicalPath(string url)
    {
        var publicBase = _storage.PublicBaseUrl.TrimEnd('/');
        if (string.IsNullOrEmpty(publicBase) || !url.StartsWith(publicBase, StringComparison.OrdinalIgnoreCase))
            return null;

        var storedName = url.Substring(publicBase.Length).TrimStart('/');
        if (string.IsNullOrEmpty(storedName)) return null;

        var root = _storage.LocalRootPath;
        if (!Path.IsPathRooted(root))
            root = Path.Combine(_env.ContentRootPath, root);

        var full = Path.GetFullPath(Path.Combine(root, storedName.Replace('/', Path.DirectorySeparatorChar)));
        // Defense in depth: the resolved path must stay within the upload root.
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return null;
        return full;
    }
}

/// <summary>Summary of a single gallery-video backfill run.</summary>
public sealed record GalleryReEncodeResult
{
    public int TotalVideos { get; init; }
    public int ReEncoded { get; set; }
    public int AlreadyPlayable { get; set; }
    public int MissingFiles { get; set; }
    public int Failed { get; set; }
}
