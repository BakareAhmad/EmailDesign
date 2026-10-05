using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Portfolio media (image or video) shown in the gallery. A row with no parent IS the
/// gallery post: its <see cref="Url"/> is the post's first image (or its video) and it
/// carries the post metadata (title, category, featured, grid order). Extra images of
/// the same post are child rows pointing at the post through <see cref="ParentId"/>,
/// ordered by <see cref="DisplayOrder"/> - one post, N images, first image = thumbnail,
/// mirroring how <see cref="Product"/> owns its <see cref="ProductImage"/> children.
/// Legacy single-media rows simply have no children and behave exactly as before.
/// </summary>
public sealed class GalleryMedia : AggregateRoot
{
    private readonly List<GalleryMedia> _images = new();

    private GalleryMedia() { } // EF
    private GalleryMedia(string title, string url, GalleryMediaType type, string? category, string? thumbnailUrl)
    {
        Title = title;
        Url = url;
        Type = type;
        Category = category;
        ThumbnailUrl = thumbnailUrl;
        IsArchived = false;
    }

    public string Title { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string? ThumbnailUrl { get; private set; }
    public GalleryMediaType Type { get; private set; }
    public string? Category { get; private set; }
    public bool IsFeatured { get; private set; }
    public bool IsArchived { get; private set; }
    public int DisplayOrder { get; private set; }

    /// <summary>Null on post rows; set on the extra-image children of a post.</summary>
    public Guid? ParentId { get; private set; }
    public GalleryMedia? Parent { get; private set; }

    /// <summary>The extra images of this post (empty for single-image and video posts).</summary>
    public IReadOnlyCollection<GalleryMedia> Images => _images.AsReadOnly();

    public static GalleryMedia Create(string title, string url, GalleryMediaType type, string? category = null, string? thumbnailUrl = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new BusinessRuleValidationException("gallery.title", "Title is required.");
        if (string.IsNullOrWhiteSpace(url))
            throw new BusinessRuleValidationException("gallery.url", "Media URL is required.");
        return new GalleryMedia(title.Trim(), url.Trim(), type, category?.Trim(), thumbnailUrl?.Trim());
    }

    /// <summary>Creates an extra-image child row belonging to <paramref name="parentId"/>.</summary>
    internal static GalleryMedia CreateChild(Guid parentId, string title, string url, int displayOrder)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new BusinessRuleValidationException("gallery.url", "Media URL is required.");
        var child = new GalleryMedia(title, url.Trim(), GalleryMediaType.Image, null, null)
        {
            ParentId = parentId,
            DisplayOrder = displayOrder
        };
        return child;
    }

    /// <summary>
    /// Appends an extra image to this post. Only image posts can be carousels; videos
    /// stay single-media and children can never own children.
    /// </summary>
    public GalleryMedia AddImage(string url)
    {
        if (ParentId is not null)
            throw new BusinessRuleValidationException("gallery.images.nested",
                "Only a gallery post can hold additional images.");
        if (Type != GalleryMediaType.Image)
            throw new BusinessRuleValidationException("gallery.images.videopost",
                "Video posts are single-media; additional images are not allowed.");
        if (string.IsNullOrWhiteSpace(url))
            throw new BusinessRuleValidationException("gallery.url", "Media URL is required.");

        var child = CreateChild(Id, Title, url, _images.Count + 1);
        _images.Add(child);
        return child;
    }

    /// <summary>Removes one extra image from this post. Unknown ids are ignored.</summary>
    public void RemoveImage(Guid childId)
    {
        var child = _images.FirstOrDefault(i => i.Id == childId);
        if (child is not null) _images.Remove(child);
    }

    /// <summary>
    /// Replaces the post's ordered image list (carousel order). The first url becomes the
    /// post's own media (grid thumbnail); the rest replace the child rows in order, so
    /// reordering changes the thumbnail exactly like reordering an Instagram carousel.
    /// </summary>
    public void SetImages(IReadOnlyList<string> orderedUrls)
    {
        if (ParentId is not null)
            throw new BusinessRuleValidationException("gallery.images.nested",
                "Only a gallery post can hold additional images.");
        if (Type != GalleryMediaType.Image)
            throw new BusinessRuleValidationException("gallery.images.videopost",
                "Video posts are single-media; additional images are not allowed.");
        if (orderedUrls is null || orderedUrls.Count == 0 || orderedUrls.Any(u => string.IsNullOrWhiteSpace(u)))
            throw new BusinessRuleValidationException("gallery.images.required",
                "An image post needs at least one image.");

        Url = orderedUrls[0].Trim();
        _images.Clear();
        for (var i = 1; i < orderedUrls.Count; i++)
            _images.Add(CreateChild(Id, Title, orderedUrls[i], i));
    }

    public void Update(string title, string? category, bool isFeatured, int displayOrder,
        string? url = null, string? thumbnailUrl = null, GalleryMediaType? type = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new BusinessRuleValidationException("gallery.title", "Title is required.");
        Title = title.Trim();
        Category = category?.Trim();
        IsFeatured = isFeatured;
        DisplayOrder = displayOrder;

        // Media replacement (e.g. an uploaded file on edit): only provided values change.
        if (!string.IsNullOrWhiteSpace(url))
            Url = url.Trim();
        if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            ThumbnailUrl = thumbnailUrl.Trim();
        if (type is not null)
            Type = type.Value;
    }

    public void Archive() => IsArchived = true;
    public void Unarchive() => IsArchived = false;
}
