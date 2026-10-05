using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Reviews;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentValidation;

namespace DeChimex.Application.Features.Reviews.Commands;

/// <summary>
/// A signed-in customer reviews a product. One review per customer per product;
/// new reviews start unapproved and surface only after admin moderation.
/// </summary>
public sealed record CreateReviewCommand(CreateReviewRequest Request) : ICommand<ReviewDto>;

public sealed class CreateReviewValidator : AbstractValidator<CreateReviewCommand>
{
    public CreateReviewValidator()
    {
        RuleFor(x => x.Request.ProductId).NotEmpty();
        RuleFor(x => x.Request.Rating).InclusiveBetween(1, 5).WithMessage("Rating must be between 1 and 5.");
        RuleFor(x => x.Request.Title).MaximumLength(200);
        RuleFor(x => x.Request.Comment).MaximumLength(4000);
    }
}

public sealed class CreateReviewHandler(
    ICurrentUser currentUser,
    ICustomerRepository customers,
    IProductRepository products,
    IReviewRepository reviews,
    IOrderQueries orders,
    IUnitOfWork uow) : ICommandHandler<CreateReviewCommand, ReviewDto>
{
    public async Task<ReviewDto> Handle(CreateReviewCommand command, CancellationToken ct)
    {
        var r = command.Request;

        var product = await products.GetByIdAsync(r.ProductId, ct)
            ?? throw new NotFoundException("Product", r.ProductId);

        if (currentUser.UserId is not Guid userId)
            throw new BusinessRuleValidationException("review.auth", "You must be signed in to review products.");

        var customer = await customers.GetByUserIdAsync(userId, ct)
            ?? throw new BusinessRuleValidationException("review.customer", "No customer profile is linked to this account.");

        // Purchase-verification (applies identically to direct purchases and custom
        // orders): only a customer with a paid-or-later order containing this product
        // may review it. Failed, abandoned, unpaid, cancelled and refunded orders do
        // not qualify as a purchase.
        if (!await orders.HasQualifyingPurchaseAsync(customer.Id, r.ProductId, ct))
            throw new BusinessRuleValidationException("review.purchase",
                "Only customers who have purchased this product can review it.");

        var alreadyReviewed = await reviews.ExistsAsync(x => x.ProductId == r.ProductId && x.CustomerId == customer.Id, ct);
        if (alreadyReviewed)
            throw new ConflictException("review.duplicate", "You have already reviewed this product.");

        var review = Domain.Entities.Review.Create(r.ProductId, customer.Id, r.Rating, r.Title, r.Comment);
        await reviews.AddAsync(review, ct);
        await uow.SaveChangesAsync(ct);

        return new ReviewDto(review.Id, review.ProductId, customer.FullName, review.Rating,
            review.Title, review.Comment, review.IsApproved, review.CreatedDateUtc);
    }
}
