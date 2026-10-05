using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Admin;

/// <summary>Read-only report row models for the admin reports page.</summary>

public sealed record SalesReportRow(string Period, int Orders, decimal Revenue);
public sealed record InventoryReportRow(string Product, int StockIn, int StockOut, int Adjustments);
public sealed record LowStockRow(string Product, int Available, int Threshold, string Status);
public sealed record CustomerReportRow(string Customer, int Orders, decimal LifetimeSpend);
public sealed record ProductReportRow(string Product, int Units, decimal Revenue);
public sealed record ServiceReportRow(string Service, int Bookings, decimal Revenue);
public sealed record StaffReportRow(string Staff, string Role, int Actions);
public sealed record PaymentReportRow(string Status, int Count, decimal Amount);

public sealed class GalleryFormViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Title is required.")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Uploaded media file (image or video). The media type is auto-detected from the file.</summary>
    public Microsoft.AspNetCore.Http.IFormFile? MediaFile { get; set; }

    /// <summary>
    /// Multi-select upload. Images selected together become ONE carousel post in the
    /// submitted order (first = grid thumbnail); a video selection must be a single file.
    /// Kept side-by-side with <see cref="MediaFile"/> so older single-file clients keep working.
    /// </summary>
    public System.Collections.Generic.List<Microsoft.AspNetCore.Http.IFormFile> MediaFiles { get; set; } = new();

    /// <summary>
    /// Final carousel order submitted by the edit form. Tokens: "u:&lt;url&gt;" for a kept
    /// existing image, "f:&lt;index&gt;" for the n-th uploaded file (index into MediaFiles).
    /// The first token decides the post thumbnail.
    /// </summary>
    public System.Collections.Generic.List<string> FinalOrder { get; set; } = new();

    /// <summary>The post's current images in carousel order (edit form only).</summary>
    public System.Collections.Generic.List<string> ExistingImages { get; set; } = new();

    /// <summary>Optional thumbnail (e.g. a poster for a video). Falls back to the media itself when absent.</summary>
    public Microsoft.AspNetCore.Http.IFormFile? ThumbnailFile { get; set; }

    /// <summary>Optional external thumbnail URL.</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>Existing media type (set on edit) so the form can show the thumbnail fields for video items only.</summary>
    public string? ExistingType { get; set; }

    public string? Category { get; set; }
    public bool IsFeatured { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class RoleFormViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Role name is required.")]
    [StringLength(100, ErrorMessage = "Role name is too long.")]
    public string Name { get; set; } = "";

    [StringLength(500, ErrorMessage = "Description is too long.")]
    public string? Description { get; set; }
}

// QuotationFormViewModel / QuotationLineFormViewModel / QuotationConvertViewModel were removed
// (2026-09-01) with the Quotation feature; quoting now uses the Invoice quote invoice path.
