using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Middleware;

/// <summary>
/// Authorized delivery for CONFIDENTIAL uploads (AUD-021). Storefront media (product
/// images, gallery) stays publicly served by UseStaticFiles; every order-bound category
/// (orderattachment, chatmedia, designproof, designreview) is intercepted here and served
/// only after <see cref="IUploadAccessService.CanViewAsync"/> confirms the caller owns the
/// order or holds the staff permission. Files themselves live OUTSIDE wwwroot
/// (Storage:PrivateRootPath), so no middleware mis-ordering can ever leak them.
///
/// Hardening applied while serving:
///  - 404 for unauthenticated/unauthorized/unknown (existence is not revealed);
///  - non-inline types (documents, archives, Adobe files) download as attachments;
///  - SVG — an executable document format — is never rendered inline: it downloads with
///    a sandboxing CSP, killing the stored-XSS vector the old public static serving had
///    under the unsafe-inline CSP;
///  - cache headers are private (a leaked URL must not be cached by shared caches).
/// </summary>
public sealed class UploadAuthorizationMiddleware(RequestDelegate next)
{
    /// <summary>Category folder names (FileCategory lower-cased) that require authorization.</summary>
    private static readonly HashSet<string> PrivateCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "orderattachment", "chatmedia", "designproof", "designreview"
    };

    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    /// <summary>Served inline (rendered by the browser); everything else downloads.</summary>
    private static readonly HashSet<string> InlineContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "image/tiff",
        "audio/mpeg", "audio/ogg", "audio/wav", "audio/x-m4a", "audio/webm",
        "video/mp4", "video/webm", "application/pdf"
    };

    public async Task InvokeAsync(HttpContext context, IUploadAccessService access, IOptions<StorageOptions> storageOptions)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var segments = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /uploads/{category}/{file} for a PRIVATE category is ours to guard.
        if (segments.Length >= 3
            && segments[0].Equals("uploads", StringComparison.OrdinalIgnoreCase)
            && PrivateCategories.Contains(segments[1]))
        {
            var options = storageOptions.Value;
            // Normalize to the canonical stored URL form ("{subFolder}/{fileName}").
            var subFolder = segments[1].ToLowerInvariant();
            var fileName = segments[2];

            // Path-traversal guard: only a bare file name may be served.
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\') || Path.IsPathRooted(fileName))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var publicUrl = $"{options.PublicBaseUrl.TrimEnd('/')}/{subFolder}/{fileName}";

            if (!await access.CanViewAsync(publicUrl, context.RequestAborted))
            {
                // Unauthenticated or not yours: identical answer either way.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var fullPath = Path.GetFullPath(Path.Combine(options.PrivateRootPath, subFolder, fileName));
            var rootFullPath = Path.GetFullPath(options.PrivateRootPath);
            if (!fullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!ContentTypeProvider.TryGetContentType(fileName, out var contentType))
                contentType = "application/octet-stream";

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.ContentType = contentType;
            // Private caching only: an authorized file must not land in shared caches.
            context.Response.Headers.CacheControl = "private,max-age=600,must-revalidate";

            var inline = InlineContentTypes.Contains(contentType);
            if (string.Equals(Path.GetExtension(fileName), ".svg", StringComparison.OrdinalIgnoreCase))
            {
                // Executable document: force download + sandbox the response document.
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                context.Response.Headers["Content-Security-Policy"] = "sandbox";
            }
            else if (!inline)
            {
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            }

            await context.Response.SendFileAsync(fullPath, context.RequestAborted);
            return; // short-circuit: never reaches UseStaticFiles
        }

        await next(context);
    }
}
