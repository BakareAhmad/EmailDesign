using DeChimex.Host.Models.Admin;
using DeChimex.Host.Services;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Gallery.Commands;
using DeChimex.Application.Features.Gallery.Queries;
using DeChimex.Contracts.Gallery;
using DeChimex.Infrastructure.Options;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin gallery management. Reuses gallery queries/commands directly.</summary>
[Authorize(Policy = Permissions.Gallery.View)]
[Route("admin/gallery")]
public sealed class AdminGalleryController : MvcControllerBase
{
    private readonly IFileStorageService _storage;
    private readonly StorageOptions _storageOptions;
    private readonly GalleryVideoReEncodeService _reEncode;

    public AdminGalleryController(
        IFileStorageService storage,
        IOptions<StorageOptions> storageOptions,
        GalleryVideoReEncodeService reEncode)
    {
        _storage = storage;
        _storageOptions = storageOptions.Value;
        _reEncode = reEncode;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Gallery";
        ViewData["Heading"] = "Gallery";
        ViewData["ActiveNav"] = "gallery";

        var items = await Sender.Send(new GetGalleryQuery(IncludeArchived: true, Category: null));
        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Gallery.Manage);
        return View(items);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Gallery.Manage)]
    public IActionResult Create()
    {
        ViewData["Title"] = "New media";
        ViewData["Heading"] = "Gallery";
        ViewData["ActiveNav"] = "gallery";
        return View(new GalleryFormViewModel());
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Gallery.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(GalleryFormViewModel model)
    {
        var files = UploadedFiles(model);
        if (files.Count == 0)
            ModelState.AddModelError("MediaFiles", "Upload at least one media file.");
        else if (!ValidateFileMix(files, allowMultiple: true))
            { } // error already registered by the helper
        var (urls, uploadError) = await UploadAllAsync(files);
        if (uploadError is not null)
            ModelState.AddModelError(string.Empty, uploadError);
        if (!ModelState.IsValid)
            return View(model);

        var isVideo = IsVideoFile(files[0].FileName);
        string? thumb = null;
        if (isVideo)
        {
            var (thumbUrl, thumbError) = await UploadThumbnailAsync(model);
            if (thumbError is not null)
            {
                ModelState.AddModelError(string.Empty, thumbError);
                return View(model);
            }
            thumb = thumbUrl;
        }

        // ONE post for the whole selection: first file is the post media (grid thumbnail),
        // the remaining images are stored as ordered children of that same post.
        await Sender.Send(new CreateGalleryMediaCommand(new CreateGalleryMediaRequest(
            model.Title.Trim(), urls![0], isVideo ? "Video" : "Image", model.Category, thumb,
            model.IsFeatured, model.DisplayOrder,
            AdditionalImageUrls: isVideo ? null : urls.Skip(1).ToList())));

        TempData["Success"] = urls.Count > 1 && !isVideo
            ? $"Gallery post added with {urls.Count} images."
            : "Gallery item added.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Gallery.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var items = await Sender.Send(new GetGalleryQuery(IncludeArchived: true, Category: null));
        var item = items.FirstOrDefault(i => i.Id == id);
        if (item is null) return NotFound();

        ViewData["Title"] = "Edit media";
        ViewData["Heading"] = "Gallery";
        ViewData["ActiveNav"] = "gallery";
        return View(new GalleryFormViewModel
        {
            Id = item.Id,
            Title = item.Title,
            ThumbnailUrl = item.ThumbnailUrl,
            ExistingType = item.Type,
            Category = item.Category,
            IsFeatured = item.IsFeatured,
            DisplayOrder = item.DisplayOrder,
            ExistingImages = item.Type.Equals("Image", StringComparison.OrdinalIgnoreCase)
                ? item.MediaUrls.ToList()
                : new List<string>()
        });
    }

    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Gallery.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, GalleryFormViewModel model)
    {
        var items = await Sender.Send(new GetGalleryQuery(IncludeArchived: true, Category: null));
        var current = items.FirstOrDefault(i => i.Id == id);
        if (current is null) return NotFound();
        var isVideoPost = current.Type.Equals("Video", StringComparison.OrdinalIgnoreCase);

        var files = UploadedFiles(model);
        if (isVideoPost && files.Count > 1)
            ModelState.AddModelError("MediaFiles", "Video posts are single-media uploads.");
        if (!isVideoPost && files.Count > 0 && files.Any(f => IsVideoFile(f.FileName)))
            ModelState.AddModelError("MediaFiles", "This post is an image carousel; videos can't be added to it.");

        var (newUrls, uploadError) = await UploadAllAsync(files);
        if (uploadError is not null)
            ModelState.AddModelError(string.Empty, uploadError);

        string? thumb = null;
        if (isVideoPost)
        {
            var (thumbUrl, thumbError) = await UploadThumbnailAsync(model);
            if (thumbError is not null) ModelState.AddModelError(string.Empty, thumbError);
            thumb = thumbUrl;
        }

        if (!ModelState.IsValid)
        {
            if (string.IsNullOrWhiteSpace(model.ThumbnailUrl))
                model.ThumbnailUrl = current.ThumbnailUrl;
            model.ExistingImages = current.MediaUrls.ToList();
            model.ExistingType = current.Type;
            return View(model);
        }

        if (isVideoPost)
        {
            // When no new file is uploaded the request keeps url/type null, so the existing
            // media and its auto-detected type are preserved.
            await Sender.Send(new UpdateGalleryMediaCommand(id, new UpdateGalleryMediaRequest(
                model.Title.Trim(), model.Category, model.IsFeatured, model.DisplayOrder,
                Url: newUrls.FirstOrDefault(), ThumbnailUrl: thumb,
                Type: newUrls.Count > 0 ? "Video" : null)));
        }
        else
        {
            var ordered = ResolveFinalOrder(model, current.MediaUrls, newUrls);
            if (ordered.Count == 0)
            {
                ModelState.AddModelError("MediaFiles", "Keep at least one image in the post.");
                model.ExistingImages = current.MediaUrls.ToList();
                model.ExistingType = current.Type;
                return View(model);
            }

            await Sender.Send(new UpdateGalleryMediaCommand(id, new UpdateGalleryMediaRequest(
                model.Title.Trim(), model.Category, model.IsFeatured, model.DisplayOrder)));

            // Reconcile the carousel: first url becomes the thumbnail, the rest the children.
            await Sender.Send(new SetGalleryImagesCommand(id, ordered));

            // Best-effort cleanup of images dropped from the post.
            foreach (var removed in current.MediaUrls.Where(u => !ordered.Contains(u)))
                await TryDeleteStoredFileAsync(removed);
        }

        TempData["Success"] = "Gallery item updated.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Turns the edit form's FinalOrder tokens ("u:&lt;url&gt;" kept image / "f:&lt;i&gt;" uploaded
    /// file i) into the new carousel order. Without tokens (no-JS submit) the existing order
    /// is kept and new uploads are appended.
    /// </summary>
    private static List<string> ResolveFinalOrder(GalleryFormViewModel model, IReadOnlyList<string> currentUrls, IReadOnlyList<string> newUrls)
    {
        if (model.FinalOrder is not { Count: > 0 })
            return currentUrls.Concat(newUrls).ToList();

        var ordered = new List<string>();
        foreach (var token in model.FinalOrder)
        {
            string? url = null;
            if (token.StartsWith("u:", StringComparison.OrdinalIgnoreCase))
                url = currentUrls.FirstOrDefault(u => u == token[2..]);
            else if (token.StartsWith("f:", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(token[2..], out var i) && i >= 0 && i < newUrls.Count)
                url = newUrls[i];
            if (url is not null && !ordered.Contains(url)) ordered.Add(url);
        }
        // NOTE: no safety net that re-appends unmentioned current urls - an existing
        // image absent from FinalOrder was explicitly REMOVED in the strip and must stay
        // gone. The no-token fallback above already covers no-JS submissions.
        return ordered;
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = Permissions.Gallery.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(Guid id, bool archive = true)
    {
        await Sender.Send(new ArchiveGalleryMediaCommand(id, archive));
        TempData["Success"] = archive ? "Media archived." : "Media restored.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("reencode")]
    [Authorize(Policy = Permissions.Gallery.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReEncode()
    {
        var result = await _reEncode.RunAsync(HttpContext.RequestAborted);
        if (result.Failed > 0)
            TempData["Success"] =
                $"Gallery re-encode done: {result.ReEncoded} fixed, {result.AlreadyPlayable} already OK, " +
                $"{result.MissingFiles} missing file(s), {result.Failed} failed. Re-check the failed/missing items.";
        else
            TempData["Success"] =
                $"Gallery re-encode done: {result.ReEncoded} fixed, {result.AlreadyPlayable} already playable, " +
                $"{result.MissingFiles} missing file(s).";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Gallery.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var items = await Sender.Send(new GetGalleryQuery(IncludeArchived: true, Category: null));
        var item = items.FirstOrDefault(i => i.Id == id);
        await Sender.Send(new DeleteGalleryMediaCommand(id));

        // Best-effort cleanup of the stored files (post media + every carousel image).
        if (item is not null)
        {
            foreach (var url in item.MediaUrls)
                await TryDeleteStoredFileAsync(url);
            if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl))
                await TryDeleteStoredFileAsync(item.ThumbnailUrl);
        }

        TempData["Success"] = "Gallery item deleted.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Merges the multi-select and legacy single-file inputs, in form order.</summary>
    private static List<IFormFile> UploadedFiles(GalleryFormViewModel model)
    {
        var files = new List<IFormFile>();
        files.AddRange(model.MediaFiles.Where(f => f is { Length: > 0 }));
        if (model.MediaFile is { Length: > 0 }) files.Add(model.MediaFile);
        return files;
    }

    /// <summary>Enforces the media-mix rules: videos are single-media, image posts images only.</summary>
    private bool ValidateFileMix(List<IFormFile> files, bool allowMultiple)
    {
        if (files.Count == 0) return true;
        if (IsVideoFile(files[0].FileName))
        {
            if (files.Count > 1)
            {
                ModelState.AddModelError("MediaFiles", "Video posts are single-media uploads. Remove the extra files or upload images only.");
                return false;
            }
            return true;
        }
        if (files.Any(f => IsVideoFile(f.FileName)))
        {
            ModelState.AddModelError("MediaFiles", "A post is either images (multiple allowed) or one video - not mixed.");
            return false;
        }
        if (!allowMultiple && files.Count > 1)
        {
            ModelState.AddModelError("MediaFiles", "Only one file is allowed here.");
            return false;
        }
        return true;
    }

    /// <summary>Uploads every selected file in submission order; per-file storage validation surfaces as a model error.</summary>
    private async Task<(List<string> Urls, string? Error)> UploadAllAsync(List<IFormFile> files)
    {
        var urls = new List<string>();
        foreach (var file in files)
        {
            try
            {
                using var stream = file.OpenReadStream();
                var uploaded = await _storage.UploadAsync(new FileUploadRequest(
                    stream, file.FileName, file.ContentType, file.Length, FileCategory.GalleryMedia));
                urls.Add(uploaded.Url);
            }
            catch (DeChimex.Application.Common.Exceptions.ValidationAppException ex)
            {
                var msg = ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "One of the files was rejected.";
                return (urls, $"{file.FileName}: {msg}");
            }
        }
        return (urls, null);
    }

    /// <summary>Resolves the optional video poster from an uploaded file or an external URL.</summary>
    private async Task<(string? Thumbnail, string? Error)> UploadThumbnailAsync(GalleryFormViewModel model)
    {
        var thumb = string.IsNullOrWhiteSpace(model.ThumbnailUrl) ? null : model.ThumbnailUrl.Trim();
        if (model.ThumbnailFile is not { Length: > 0 }) return (thumb, null);
        try
        {
            using var stream = model.ThumbnailFile.OpenReadStream();
            var uploaded = await _storage.UploadAsync(new FileUploadRequest(
                stream, model.ThumbnailFile.FileName, model.ThumbnailFile.ContentType, model.ThumbnailFile.Length, FileCategory.GalleryMedia));
            return (uploaded.Url, null);
        }
        catch (DeChimex.Application.Common.Exceptions.ValidationAppException ex)
        {
            return (thumb, ex.Errors.Values.SelectMany(v => v).FirstOrDefault());
        }
    }

    private static string GuessMediaType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".mp4" or ".webm" ? "Video" : "Image";
    }

    private static bool IsVideoFile(string fileName) => GuessMediaType(fileName) == "Video";

    private async Task TryDeleteStoredFileAsync(string url)
    {
        try
        {
            var publicBase = _storageOptions.PublicBaseUrl.TrimEnd('/');
            if (string.IsNullOrEmpty(publicBase) || !url.StartsWith(publicBase, StringComparison.OrdinalIgnoreCase))
                return;
            var storedName = url.Substring(publicBase.Length).TrimStart('/');
            if (string.IsNullOrEmpty(storedName)) return;
            await _storage.DeleteAsync(storedName);
        }
        catch
        {
            // The database row is the source of truth; a leftover file is not user visible.
        }
    }
}
