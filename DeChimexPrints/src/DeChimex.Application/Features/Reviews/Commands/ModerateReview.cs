using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Repositories;
using MediatR;

namespace DeChimex.Application.Features.Reviews.Commands;

public sealed record ApproveReviewCommand(Guid Id) : ICommand<Unit>;

public sealed class ApproveReviewHandler(IReviewRepository reviews, IUnitOfWork uow)
    : ICommandHandler<ApproveReviewCommand, Unit>
{
    public async Task<Unit> Handle(ApproveReviewCommand command, CancellationToken ct)
    {
        var review = await reviews.GetByIdAsync(command.Id, ct) ?? throw new NotFoundException("Review", command.Id);
        review.Approve();
        reviews.Update(review);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record RejectReviewCommand(Guid Id) : ICommand<Unit>;

public sealed class RejectReviewHandler(IReviewRepository reviews, IUnitOfWork uow)
    : ICommandHandler<RejectReviewCommand, Unit>
{
    public async Task<Unit> Handle(RejectReviewCommand command, CancellationToken ct)
    {
        var review = await reviews.GetByIdAsync(command.Id, ct) ?? throw new NotFoundException("Review", command.Id);
        review.Reject();
        reviews.Update(review);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record DeleteReviewCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteReviewHandler(IReviewRepository reviews, IUnitOfWork uow)
    : ICommandHandler<DeleteReviewCommand, Unit>
{
    public async Task<Unit> Handle(DeleteReviewCommand command, CancellationToken ct)
    {
        var review = await reviews.GetByIdAsync(command.Id, ct) ?? throw new NotFoundException("Review", command.Id);
        reviews.Remove(review);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
