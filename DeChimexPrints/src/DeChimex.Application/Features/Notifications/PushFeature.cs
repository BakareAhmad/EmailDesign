using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DeChimex.Application.Features.Notifications;

// ---------------------------------------------------------------------------------
// PWA Web Push (browser push notifications).
// Subscriptions are managed by the signed-in user from the notifications dropdown;
// dispatch happens server-side when a DIRECT recipient notification is created
// (see Infrastructure NotificationService -> IWebPushDispatcher). Staff-audience
// broadcasts are intentionally never pushed (notification-fatigue control).
// ---------------------------------------------------------------------------------

public sealed record SubscribePushRequest(string Endpoint, string P256dh, string Auth, string? UserAgent);
public sealed record SubscribePushCommand(Guid UserId, SubscribePushRequest Request) : ICommand<Unit>;
public sealed class SubscribePushValidator : AbstractValidator<SubscribePushCommand>
{
    public SubscribePushValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Request.Endpoint).NotEmpty().MaximumLength(450)
            .Must(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Push endpoints must be HTTPS.");
        RuleFor(x => x.Request.P256dh).NotEmpty().MaximumLength(160);
        RuleFor(x => x.Request.Auth).NotEmpty().MaximumLength(80);
    }
}

public sealed class SubscribePushHandler(IPushSubscriptionRepository subscriptions, IUnitOfWork uow)
    : ICommandHandler<SubscribePushCommand, Unit>
{
    public async Task<Unit> Handle(SubscribePushCommand c, CancellationToken ct)
    {
        var existing = await subscriptions.GetByUserAndEndpointAsync(c.UserId, c.Request.Endpoint, ct);
        if (existing is null)
        {
            await subscriptions.AddAsync(PushSubscription.Create(
                c.UserId, c.Request.Endpoint, c.Request.P256dh, c.Request.Auth, c.Request.UserAgent), ct);
        }
        else
        {
            // Same browser re-subscribing (keys rotate): update in place instead of stacking rows.
            existing.UpdateKeys(c.Request.P256dh, c.Request.Auth);
            subscriptions.Update(existing);
        }
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record UnsubscribePushCommand(Guid? UserId, string Endpoint) : ICommand<Unit>;
public sealed class UnsubscribePushValidator : AbstractValidator<UnsubscribePushCommand>
{
    public UnsubscribePushValidator() => RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(450);
}

/// <summary>Removes a subscription. The endpoint owner check means a browser can only
/// unsubscribe itself (or its own user's rows); endpoints are server-private URLs.</summary>
public sealed class UnsubscribePushHandler(IPushSubscriptionRepository subscriptions, IUnitOfWork uow)
    : ICommandHandler<UnsubscribePushCommand, Unit>
{
    public async Task<Unit> Handle(UnsubscribePushCommand c, CancellationToken ct)
    {
        PushSubscription? row;
        if (c.UserId is Guid userId)
            row = await subscriptions.GetByUserAndEndpointAsync(userId, c.Endpoint, ct);
        else
            row = await subscriptions.GetByEndpointAsync(c.Endpoint, ct);
        if (row is null) return Unit.Value; // idempotent
        subscriptions.Remove(row);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record GetVapidPublicKeyQuery : IQuery<string?>;
public sealed class GetVapidPublicKeyHandler(IApplicationSettingRepository settings)
    : IQueryHandler<GetVapidPublicKeyQuery, string?>
{
    public async Task<string?> Handle(GetVapidPublicKeyQuery q, CancellationToken ct)
    {
        // Single indexed settings lookup; the subscribe flow is rare enough to skip caching.
        var setting = await settings.GetByKeyAsync("push.vapid.public_key", ct);
        return string.IsNullOrWhiteSpace(setting?.Value) ? null : setting!.Value;
    }
}
