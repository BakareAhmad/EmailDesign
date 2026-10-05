using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Orders.Commands;

// ------------------------------------------------------------------------------------
// Design Review upload: customer or staff uploads a design file for review.
// Unlike DesignProof (staff-only, superseding), DesignReview maintains independent history.
// ------------------------------------------------------------------------------------

public sealed record UploadDesignReviewCommand(
    Guid OrderId, 
    UploadDesignReviewRequest Request, 
    string UploadedByRole) : ICommand<OrderDesignReviewDto>;

public sealed class UploadDesignReviewValidator : AbstractValidator<UploadDesignReviewCommand>
{
    public UploadDesignReviewValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Request.StoredFileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Request.OriginalFileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Request.Url).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.Request.ContentType).MaximumLength(120);
        RuleFor(x => x.Request.SizeBytes).GreaterThan(0);
        RuleFor(x => x.Request.Notes).MaximumLength(2000);
        RuleFor(x => x.Request.PreviewUrl).MaximumLength(1000);
        RuleFor(x => x.Request.PreviewStoredFileName).MaximumLength(500);
        RuleFor(x => x.UploadedByRole).Must(r => r is "customer" or "staff")
            .WithMessage("Uploader role must be customer or staff.");
    }
}

public sealed class UploadDesignReviewHandler(
    IOrderRepository orders,
    ICustomerRepository customers,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<UploadDesignReviewCommand, OrderDesignReviewDto>
{
    public async Task<OrderDesignReviewDto> Handle(UploadDesignReviewCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        // Authorization: customer can only upload to their own orders. The order is
        // owned by the CUSTOMER entity, so the current user's linked profile (not the
        // user id itself — different id spaces) must match (same rule as proofs).
        if (command.UploadedByRole == "customer")
        {
            if (currentUser.UserId is null)
                throw new UnauthorizedAppException("Please sign in to upload design reviews.");
            if ((await customers.GetByUserIdAsync(currentUser.UserId.Value, ct))?.Id != order.CustomerId)
                throw new ForbiddenException("You can only upload design reviews to your own orders.");
        }
        // Staff uploading - no additional check needed (handled by permissions)

        var review = order.AddDesignReview(
            command.Request.StoredFileName,
            command.Request.OriginalFileName,
            command.Request.Url,
            command.Request.ContentType,
            command.Request.SizeBytes,
            currentUser.UserId ?? Guid.Empty,
            command.UploadedByRole,
            command.Request.Notes,
            command.Request.PreviewUrl,
            command.Request.PreviewStoredFileName);

        order.MarkConversationWaitingForStaff(); // customer acted: staff must review
        order.AddTimelineEntry("designreview-uploaded", 
            $"Design review '{review.OriginalFileName}' uploaded.",
            currentUser.UserId, command.UploadedByRole);
        
        await uow.SaveChangesAsync(ct);

        var dto = ProjectDesignReview(review);

        // Live: viewers with the workspace open see the new design review appear (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderDesignReview", new { designReview = dto }, ct);
        
        return dto;
    }

    private static OrderDesignReviewDto ProjectDesignReview(OrderDesignReview review)
    {
        return new OrderDesignReviewDto(
            review.Id,
            review.OriginalFileName,
            review.Url,
            review.ContentType,
            review.SizeBytes,
            review.PreviewUrl,
            review.UploadedByRole,
            review.Status,
            review.RejectionReason,
            review.ApprovedByUserId,
            review.ApprovedAtUtc,
            review.RejectedByUserId,
            review.RejectedAtUtc,
            review.CreatedDateUtc,
            review.Notes);
    }
}

// ------------------------------------------------------------------------------------
// Design Review approval: staff approves a design review submission.
// ------------------------------------------------------------------------------------

public sealed record ApproveDesignReviewCommand(
    Guid OrderId, 
    Guid DesignReviewId,
    ApproveDesignReviewRequest Request) : ICommand<Unit>;

public sealed class ApproveDesignReviewValidator : AbstractValidator<ApproveDesignReviewCommand>
{
    public ApproveDesignReviewValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.DesignReviewId).NotEmpty();
        RuleFor(x => x.Request.ApprovalNotes).MaximumLength(2000);
    }
}

public sealed class ApproveDesignReviewHandler(
    IOrderRepository orders,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<ApproveDesignReviewCommand, Unit>
{
    public async Task<Unit> Handle(ApproveDesignReviewCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        if (currentUser.UserId is null)
            throw new UnauthorizedAppException("Please sign in to approve design reviews.");

        var review = order.ApproveDesignReview(
            command.DesignReviewId, 
            currentUser.UserId.Value, 
            command.Request.ApprovalNotes);

        order.MarkConversationWaitingForCustomer(); // staff acted: customer notified
        order.AddTimelineEntry("designreview-approved", 
            $"Design review '{review.OriginalFileName}' approved.",
            currentUser.UserId, currentUser.Email);
        
        await uow.SaveChangesAsync(ct);

        // Live: viewers with the workspace open see the approval (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderDesignReviewStatus", new
        {
            designReviewId = review.Id,
            status = review.Status.ToString(),
            approvedAtUtc = review.ApprovedAtUtc
        }, ct);
        
        return Unit.Value;
    }
}

// ------------------------------------------------------------------------------------
// Design Review rejection: staff rejects a design review submission with a reason.
// ------------------------------------------------------------------------------------

public sealed record RejectDesignReviewCommand(
    Guid OrderId, 
    Guid DesignReviewId,
    RejectDesignReviewRequest Request) : ICommand<Unit>;

public sealed class RejectDesignReviewValidator : AbstractValidator<RejectDesignReviewCommand>
{
    public RejectDesignReviewValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.DesignReviewId).NotEmpty();
        RuleFor(x => x.Request.Reason).NotEmpty().MaximumLength(2000);
    }
}

public sealed class RejectDesignReviewHandler(
    IOrderRepository orders,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<RejectDesignReviewCommand, Unit>
{
    public async Task<Unit> Handle(RejectDesignReviewCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        if (currentUser.UserId is null)
            throw new UnauthorizedAppException("Please sign in to reject design reviews.");

        var review = order.RejectDesignReview(
            command.DesignReviewId, 
            currentUser.UserId.Value, 
            command.Request.Reason);

        order.MarkConversationWaitingForCustomer(); // staff acted: customer must revise
        order.AddTimelineEntry("designreview-rejected", 
            $"Design review '{review.OriginalFileName}' rejected: {review.RejectionReason}",
            currentUser.UserId, currentUser.Email);
        
        await uow.SaveChangesAsync(ct);

        // Live: viewers with the workspace open see the rejection (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderDesignReviewStatus", new
        {
            designReviewId = review.Id,
            status = review.Status.ToString(),
            rejectedAtUtc = review.RejectedAtUtc,
            reason = review.RejectionReason
        }, ct);
        
        return Unit.Value;
    }
}
