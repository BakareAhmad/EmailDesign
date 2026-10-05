using System.Net;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Repositories;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebPush;

namespace DeChimex.Infrastructure.Services;

/// <summary>
/// Sends Web Push notifications to all of a user's registered browsers/devices using the
/// VAPID keys seeded into ApplicationSettings (push.vapid.*). Design rules:
///  - best effort and non-blocking: DispatchAsync returns immediately, sends run in the
///    background IN THEIR OWN SERVICE SCOPE (never the request's DbContext), and any
///    failure is logged without affecting the persisted notification;
///  - 404/410 from the push service deletes the subscription (browser uninstalled, profile
///    cleared, or subscription expired);
///  - a successful send marks the row so stale devices are visible for diagnosis;
///  - missing VAPID configuration disables push silently (debug log).
/// Payload privacy: carries only the title/body/link the user already sees in their own
/// notification centre on their own locked device.
/// </summary>
public sealed class PwaWebPushDispatcher(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    ILogger<PwaWebPushDispatcher> logger)
    : IWebPushDispatcher
{
    private const string KeysCacheKey = "push:vapid:details";
    private static readonly TimeSpan KeysTtl = TimeSpan.FromMinutes(5);

    public Task DispatchAsync(Guid userId, string title, string message, string? url, CancellationToken ct = default)
    {
        // Fire-and-forget: NotifyAsync must stay fast; push delivery latency is irrelevant.
        // The background task creates its OWN service scope: the request's scoped DbContext
        // is disposed as soon as the request ends, and using it here raced the very next
        // query on that context ("a second operation was started on this context instance").
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var subscriptions = scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>();
                var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingRepository>();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await SendToAllDevicesAsync(subscriptions, settings, uow, userId, title, message, url);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Web push dispatch failed for user {UserId}: {Message}", userId, ex.Message);
            }
        });
        return Task.CompletedTask;
    }

    private async Task SendToAllDevicesAsync(
        IPushSubscriptionRepository subscriptions,
        IApplicationSettingRepository settings,
        IUnitOfWork uow,
        Guid userId, string title, string message, string? url)
    {
        var vapid = await GetVapidDetailsAsync(settings);
        if (vapid is null)
        {
            logger.LogDebug("Web push skipped: VAPID keys are not configured.");
            return;
        }

        var rows = await subscriptions.ListByUserAsync(userId);
        if (rows.Count == 0) return;

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            title,
            body = message,
            url = string.IsNullOrWhiteSpace(url) ? "/" : url
        });

        using var client = new WebPushClient();
        var changed = false;
        foreach (var row in rows)
        {
            try
            {
                await client.SendNotificationAsync(new PushSubscription(row.Endpoint, row.P256dh, row.Auth), payload, vapid);
                row.MarkSuccess();
                subscriptions.Update(row);
                changed = true;
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // Subscription expired or the browser discarded it: remove so we stop trying.
                logger.LogInformation("Removing expired push subscription {Endpoint}.",
                    row.Endpoint[..Math.Min(60, row.Endpoint.Length)]);
                subscriptions.Remove(row);
                changed = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Web push to {Endpoint} failed: {Message}",
                    row.Endpoint[..Math.Min(60, row.Endpoint.Length)], ex.Message);
            }
        }
        if (changed)
            await uow.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<VapidDetails?> GetVapidDetailsAsync(IApplicationSettingRepository settings)
    {
        if (cache.TryGetValue(KeysCacheKey, out VapidDetails? cached)) return cached;

        var publicKey = (await settings.GetByKeyAsync("push.vapid.public_key"))?.Value;
        var privateKey = (await settings.GetByKeyAsync("push.vapid.private_key"))?.Value;
        var subject = (await settings.GetByKeyAsync("push.vapid.subject"))?.Value;
        if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
            return null;

        var details = new VapidDetails(
            string.IsNullOrWhiteSpace(subject) ? "mailto:hello@dechimexprinthub.com" : subject!,
            publicKey, privateKey);
        cache.Set(KeysCacheKey, details, KeysTtl);
        return details;
    }
}
