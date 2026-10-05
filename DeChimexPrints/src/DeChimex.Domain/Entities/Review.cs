using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>A product review left by a customer (1-5 stars).</summary>
public sealed class Review : AggregateRoot
{
    private Review() { } // EF
    private Review(Guid productId, Guid customerId, int rating, string? title, string? comment)
    {
        ProductId = productId;
        CustomerId = customerId;
        Rating = rating;
        Title = title;
        Comment = comment;
        IsApproved = false;
    }

    public Guid ProductId { get; private set; }
    public Guid CustomerId { get; private set; }
    public int Rating { get; private set; }
    public string? Title { get; private set; }
    public string? Comment { get; private set; }
    public bool IsApproved { get; private set; }

    public static Review Create(Guid productId, Guid customerId, int rating, string? title, string? comment)
    {
        if (rating is < 1 or > 5)
            throw new BusinessRuleValidationException("review.rating", "Rating must be between 1 and 5.");
        return new Review(productId, customerId, rating, title?.Trim(), comment?.Trim());
    }

    public void Approve() => IsApproved = true;
    public void Reject() => IsApproved = false;
}
