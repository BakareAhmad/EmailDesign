using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Contracts.Orders;
using Microsoft.AspNetCore.Http;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>Read-beacon body: ids the client actually rendered while visible.</summary>
public sealed record ChatMarkReadRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// Shared parsing for workspace chat sends (portal + staff). The composer posts one
/// multipart form — text, an optional quoted message id, and optional files with
/// per-file kinds/durations — and both controllers run it through here so the portal
/// and staff pipelines can never drift apart.
/// </summary>
internal static class ChatSendSupport
{
    /// <summary>Fetch submits ask for JSON; plain form posts keep the PRG redirect,
    /// so the conversation still works with JavaScript disabled.</summary>
    public static bool WantsJson(HttpRequest request)
        => request.Headers.Accept.Any(h =>
            h?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Uploads staged composer files to chat-media storage. Kinds declared by
    /// the client are advisory: the render kind derives from the content type, with
    /// "voice" honored only for recorded audio. Extension allow-listing stays with the
    /// storage service (it rejects anything outside the chat-media set).</summary>
    public static async Task<IReadOnlyList<ChatMessageAttachmentInput>> UploadAttachmentsAsync(
        IFileStorageService storage,
        IList<IFormFile>? files,
        IList<string>? kinds,
        IList<string>? durations,
        CancellationToken ct)
    {
        if (files is null || files.Count == 0)
            return [];
        if (files.Count > 6)
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["Files"] = ["A message carries at most 6 attachments."]
            });

        var result = new List<ChatMessageAttachmentInput>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (file.Length <= 0)
                continue;
            var declared = kinds is not null && i < kinds.Count ? kinds[i]?.Trim().ToLowerInvariant() : null;
            var kind = DeriveKind(file.ContentType, declared);
            int? duration = null;
            if (kind is "audio" or "voice" && durations is not null && i < durations.Count &&
                int.TryParse(durations[i], out var seconds) && seconds is >= 1 and <= 3600)
                duration = seconds;

            using var stream = file.OpenReadStream();
            var uploaded = await storage.UploadAsync(new FileUploadRequest(
                stream, file.FileName, file.ContentType, file.Length, FileCategory.ChatMedia), ct);
            result.Add(new ChatMessageAttachmentInput(kind, file.FileName, uploaded.StoredFileName,
                uploaded.Url, file.ContentType, uploaded.Size, duration));
        }
        return result;
    }

    private static string DeriveKind(string? contentType, string? declared)
    {
        var ct = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
        if (ct.StartsWith("image/", StringComparison.Ordinal)) return "image";
        if (ct.StartsWith("video/", StringComparison.Ordinal)) return "video";
        if (ct.StartsWith("audio/", StringComparison.Ordinal))
            return declared == "voice" ? "voice" : "audio";
        return "file";
    }
}
