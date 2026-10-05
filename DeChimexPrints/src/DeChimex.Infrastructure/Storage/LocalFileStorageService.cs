using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Storage;

/// <summary>
/// Secure local file storage with extension/size validation and unique file names.
/// Implements the storage abstraction so Cloudinary/Azure Blob can replace it later.
/// </summary>
public sealed class LocalFileStorageService(
    IOptions<StorageOptions> options,
    IVideoEncodeService videoEncode) : IFileStorageService
{
    private readonly StorageOptions _options = options.Value;

    private static readonly string[] VideoExtensions = { ".mp4", ".webm" };

    /// <summary>Categories whose files are confidential (AUD-021): stored OUTSIDE wwwroot
    /// under PrivateRootPath and served only through UploadAuthorizationMiddleware.</summary>
    private static readonly HashSet<FileCategory> PrivateCategories =
    [
        FileCategory.OrderAttachment, FileCategory.DesignProof,
        FileCategory.DesignReview, FileCategory.ChatMedia,
        FileCategory.InvoiceDocument, FileCategory.Document
    ];

    private string RootFor(FileCategory category)
        => PrivateCategories.Contains(category) ? _options.PrivateRootPath : _options.LocalRootPath;

    public async Task<FileUploadResult> UploadAsync(FileUploadRequest request, CancellationToken ct = default)
    {
        if (request.Length <= 0 || request.Length > _options.MaxFileSizeBytes)
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["File"] = new[] { $"File size must be between 1 byte and {_options.MaxFileSizeBytes} bytes." }
            });

        var extension = Path.GetExtension(request.OriginalFileName).ToLowerInvariant();
        var allowed = request.Category switch
        {
            FileCategory.ProductImage => _options.AllowedImageExtensions,
            // The gallery showcases both photos and short videos.
            FileCategory.GalleryMedia => _options.AllowedImageExtensions
                .Concat(_options.AllowedVideoExtensions)
                .ToArray(),
            // Order attachments (design references/briefs) accept images and documents.
            FileCategory.OrderAttachment => _options.AllowedImageExtensions
                .Concat(_options.AllowedDocumentExtensions)
                .ToArray(),
            // Design proofs/previews are images the customer approves in the browser.
            FileCategory.DesignProof => _options.AllowedImageExtensions,
            // Design reviews accept design files (images, PDFs, SVG, Adobe formats, ZIP)
            // These are preserved EXACTLY without compression/resizing.
            FileCategory.DesignReview => _options.AllowedDesignReviewExtensions,
            // Chat media: photos, short videos, voice notes and documents shared
            // inside the conversation.
            FileCategory.ChatMedia => _options.AllowedImageExtensions
                .Concat(_options.AllowedVideoExtensions)
                .Concat(_options.AllowedAudioExtensions)
                .Concat(_options.AllowedDocumentExtensions)
                .ToArray(),
            _ => _options.AllowedDocumentExtensions
        };

        if (!allowed.Contains(extension))
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["File"] = new[] { $"File type '{extension}' is not allowed." }
            });

        var subFolder = request.Category.ToString().ToLowerInvariant();
        var root = Path.Combine(RootFor(request.Category), subFolder);
        Directory.CreateDirectory(root);

        var storedName = $"{Guid.NewGuid():n}{extension}";
        var fullPath = Path.Combine(root, storedName);

        await using (var fileStream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await request.Content.CopyToAsync(fileStream, ct);

        // Gallery videos are re-encoded to a browser-decodedable codec (H.264, fast start) so the
        // native play/pause + progress bar in the gallery lightbox actually works. If no encoder
        // (ffmpeg) is available, the original is kept and the player's inline message covers it.
        // DesignReview files are NEVER re-encoded/compressed - preserve original exactly.
        var isVideo = (request.Category == FileCategory.GalleryMedia || request.Category == FileCategory.ChatMedia) &&
                      VideoExtensions.Contains(extension);
        long finalSize = request.Length;
        if (isVideo)
        {
            try
            {
                var played = await videoEncode.MakeBrowserPlayableAsync(fullPath, ct);
                if (played && File.Exists(fullPath))
                    finalSize = new FileInfo(fullPath).Length;
            }
            catch
            {
                // Best-effort only: never fail an upload because transcoding failed.
            }
        }

        // AUD-013: no more placeholder "previews". The previous implementation produced
        // "previews" by File.Copy (PDFs stored twice as preview_*.pdf), doubling disk per
        // design upload and showing the original as its own preview. Non-inline types now
        // render as file-type icons with an authorized download link (HasPreview=false);
        // images are previewed by the browser directly from their authorized URL.

        var url = $"{_options.PublicBaseUrl.TrimEnd('/')}/{subFolder}/{storedName}";

        // AUD-013: no SEPARATE preview artifact is produced (the old File.Copy doubled the
        // disk per design upload and presented a copy as a "preview"). For images the
        // browser previews the file directly from its authorized URL, so PreviewUrl simply
        // points at the SAME stored file; every other type gets no preview — the workspace
        // renders a file-type icon with an authorized download link instead.
        string? previewUrl = IsBrowserViewableImage(request.ContentType) ? url : null;
        string? previewStoredFileName = previewUrl is null ? null : $"{subFolder}/{storedName}";
        return new FileUploadResult(url, $"{subFolder}/{storedName}", finalSize, previewUrl, previewStoredFileName);
    }

    /// <summary>AUD-013: content types the browser can render inline as an image. Kept to
    /// universally safe raster formats — SVG is intentionally absent (scriptable vector;
    /// it is always delivered as a download by the upload guard).</summary>
    private static bool IsBrowserViewableImage(string? contentType) => contentType is not null && (
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase));

    public Task DeleteAsync(string storedFileName, CancellationToken ct = default)
    {
        // Security (HIGH-006): Prevent path traversal attacks. Only bare relative file
        // names stored under the upload root are acceptable here.
        if (string.IsNullOrWhiteSpace(storedFileName) ||
            storedFileName.Contains("..") ||
            storedFileName.Contains(Path.DirectorySeparatorChar) &&
            storedFileName.StartsWith(Path.DirectorySeparatorChar.ToString()) ||
            Path.IsPathRooted(storedFileName))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["File"] = new[] { "Invalid file path." }
            });
        }

        // AUD-021: the file may live in the public (wwwroot) or the private (App_Data)
        // root depending on its category; try both, validating containment in each.
        foreach (var root in new[] { _options.LocalRootPath, _options.PrivateRootPath })
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, storedFileName));
            var rootFullPath = Path.GetFullPath(root);

            // Defense in depth: the resolved path must remain inside the upload root.
            if (!fullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return Task.CompletedTask;
            }
        }

        // Unknown/absent file: deletion stays best-effort (idempotent).
        return Task.CompletedTask;
    }
}
