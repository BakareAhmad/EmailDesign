using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Marketing.Commands;

// ---- Create (draft) ----
public sealed record CreateMarketingBroadcastCommand(string Subject, string HtmlContent, string? PlainTextContent, string CreatedBy)
    : ICommand<Guid>;

public sealed class CreateMarketingBroadcastValidator : AbstractValidator<CreateMarketingBroadcastCommand>
{
    public CreateMarketingBroadcastValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.HtmlContent).NotEmpty();
    }
}

public sealed class CreateMarketingBroadcastHandler(IMarketingRepository broadcasts, IUnitOfWork uow)
    : ICommandHandler<CreateMarketingBroadcastCommand, Guid>
{
    public async Task<Guid> Handle(CreateMarketingBroadcastCommand command, CancellationToken ct)
    {
        // Sanitize BEFORE persisting: stored content is the exact content customers receive.
        var sanitized = MarketingHtmlSanitizer.Sanitize(command.HtmlContent);
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new DeChimex.Application.Common.Exceptions.ValidationAppException(
                new Dictionary<string, string[]> { ["HtmlContent"] = ["The email content contains no renderable HTML."] });

        var broadcast = MarketingBroadcast.Create(command.Subject, sanitized,
            command.PlainTextContent ?? string.Empty, command.CreatedBy);
        await broadcasts.AddAsync(broadcast, ct);
        await uow.SaveChangesAsync(ct);
        return broadcast.Id;
    }
}

// ---- Send (queue for the background worker) ----
public sealed record SendMarketingBroadcastCommand(Guid BroadcastId) : ICommand<Unit>;

/// <summary>
/// Moves a DRAFT broadcast to QUEUED and snapshots the eligible recipients. The actual
/// sending happens in the background worker (MarketingBroadcastProcessor) - this command
/// NEVER sends emails itself, so the admin's HTTP request returns immediately no matter
/// how large the audience is. The Draft-only guard in the domain makes re-clicking Send
/// (double click / refresh) a no-op error instead of a duplicate send.
/// </summary>
public sealed class SendMarketingBroadcastHandler(IMarketingRepository broadcasts, IUnitOfWork uow)
    : ICommandHandler<SendMarketingBroadcastCommand, Unit>
{
    public async Task<Unit> Handle(SendMarketingBroadcastCommand command, CancellationToken ct)
    {
        var broadcast = await broadcasts.GetWithRecipientsAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing broadcast", command.BroadcastId);

        var recipients = await broadcasts.GetEligibleRecipientsAsync(ct);
        broadcast.Queue(recipients);

        broadcasts.Update(broadcast);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Retry failed recipients ----
public sealed record RetryFailedRecipientsCommand(Guid BroadcastId) : ICommand<Unit>;

public sealed class RetryFailedRecipientsHandler(IMarketingRepository broadcasts, IUnitOfWork uow)
    : ICommandHandler<RetryFailedRecipientsCommand, Unit>
{
    public async Task<Unit> Handle(RetryFailedRecipientsCommand command, CancellationToken ct)
    {
        var broadcast = await broadcasts.GetWithRecipientsAsync(command.BroadcastId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Marketing broadcast", command.BroadcastId);

        broadcast.RetryFailed(); // only FAILED rows return to Pending; SENT rows are never re-sent

        broadcasts.Update(broadcast);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
