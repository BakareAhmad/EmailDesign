namespace DeChimex.Contracts.Reviews;

public sealed record ReviewDto(
    Guid Id, Guid ProductId, string CustomerName, int Rating,
    string? Title, string? Comment, bool IsApproved, DateTime CreatedDateUtc);

public sealed record CreateReviewRequest(Guid ProductId, int Rating, string? Title, string? Comment);
