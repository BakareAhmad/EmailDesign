using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Store;

public sealed class ProductReviewFormViewModel
{
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5.")]
    public int Rating { get; set; } = 5;

    [StringLength(200, ErrorMessage = "Title is too long.")]
    public string? Title { get; set; }

    [StringLength(4000, ErrorMessage = "Review is too long.")]
    public string? Comment { get; set; }
}
