using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Reviews;
using DeChimex.Domain.Repositories;

namespace DeChimex.Application.Features.Reviews.Queries;

/// <summary>Product reviews for a product (approved only by default) with customer names.</summary>
public sealed record GetProductReviewsQuery(Guid ProductId, bool ApprovedOnly = true) : IQuery<IReadOnlyList<ReviewDto>>;

public sealed class GetProductReviewsHandler(IReviewRepository reviews, ICustomerRepository customers)
    : IQueryHandler<GetProductReviewsQuery, IReadOnlyList<ReviewDto>>
{
    public async Task<IReadOnlyList<ReviewDto>> Handle(GetProductReviewsQuery query, CancellationToken ct)
    {
        var list = await reviews.GetForProductAsync(query.ProductId, query.ApprovedOnly, ct);

        var customerIds = list.Select(r => r.CustomerId).Distinct().ToList();
        var names = new Dictionary<Guid, string>();
        foreach (var id in customerIds)
        {
            var c = await customers.GetByIdAsync(id, ct);
            if (c is not null) names[id] = c.FullName;
        }

        return list
            .OrderByDescending(r => r.CreatedDateUtc)
            .Select(r => new ReviewDto(
                r.Id, r.ProductId, names.GetValueOrDefault(r.CustomerId, "Customer"),
                r.Rating, r.Title, r.Comment, r.IsApproved, r.CreatedDateUtc))
            .ToList();
    }
}

/// <summary>Whether a customer already has a review on a product (any moderation state).</summary>
public sealed record HasProductReviewQuery(Guid ProductId, Guid CustomerId) : IQuery<bool>;

/// <summary>Review purchase-verification: has this customer bought this product in a
/// paid-or-later order (direct and custom orders count identically)?</summary>
public sealed record HasPurchasedProductQuery(Guid ProductId, Guid CustomerId) : IQuery<bool>;

public sealed class HasPurchasedProductHandler(Common.Interfaces.IOrderQueries orders)
    : IQueryHandler<HasPurchasedProductQuery, bool>
{
    public Task<bool> Handle(HasPurchasedProductQuery query, CancellationToken ct)
        => orders.HasQualifyingPurchaseAsync(query.CustomerId, query.ProductId, ct);
}

public sealed class HasProductReviewHandler(IReviewRepository reviews)
    : IQueryHandler<HasProductReviewQuery, bool>
{
    public Task<bool> Handle(HasProductReviewQuery query, CancellationToken ct)
        => reviews.ExistsAsync(r => r.ProductId == query.ProductId && r.CustomerId == query.CustomerId, ct);
}
