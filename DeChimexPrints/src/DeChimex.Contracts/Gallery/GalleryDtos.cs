namespace DeChimex.Contracts.Gallery;

/// <summary>
/// A gallery post. <see cref="MediaUrls"/> is the post's ordered media list (carousel
/// order): for image posts the first entry is the grid thumbnail and the rest are the
/// extra images revealed in the viewer; for video posts it holds the single video url.
/// </summary>
public sealed record GalleryMediaDto(Guid Id, string Title, string Url, string? ThumbnailUrl, string Type,
    string? Category, bool IsFeatured, bool IsArchived, int DisplayOrder, IReadOnlyList<string> MediaUrls);

public sealed record CreateGalleryMediaRequest(string Title, string Url, string Type, string? Category, string? ThumbnailUrl,
    bool IsFeatured, int DisplayOrder, IReadOnlyList<string>? AdditionalImageUrls = null);

public sealed record UpdateGalleryMediaRequest(string Title, string? Category, bool IsFeatured, int DisplayOrder,
    string? Url = null, string? ThumbnailUrl = null, string? Type = null);
