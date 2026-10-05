using System.Collections.Concurrent;
using System.Security.Claims;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities.Diagnostics;
using DeChimex.Infrastructure.Diagnostics;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.DevPortal;

/// <summary>
/// Developer-only realtime channel (/hubs/dev). Deliberately separate from NotificationHub:
/// only connections that pass the policy AND a live DB permission check join the group, and
/// only Error/Critical technical events are pushed (rate-limited per error group), so ordinary
/// activity never floods the portal and nothing reaches customers or normal staff.
/// </summary>
[Authorize(Policy = Permissions.Developer.PortalView)]
public sealed class DevHub(DeChimexDbContext db) : Hub
{
    public const string Group = "developers";

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !await DevAccess.HasPermissionAsync(db, userId, Permissions.Developer.PortalView))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group);
        await base.OnConnectedAsync();
    }
}

/// <summary>Pushes Error/Critical technical events to connected developers. At most one alert per
/// error group (fingerprint) per minute. Best-effort; never throws.</summary>
public sealed class DevAlertPusher(IServiceProvider services, IOptions<DevPortalOptions> options) : ITechnicalEventObserver
{
    private readonly ConcurrentDictionary<string, DateTime> _lastSent = new(StringComparer.Ordinal);
    private IHubContext<DevHub>? _hub;

    public void OnRecorded(TechnicalEvent e)
    {
        if (!options.Value.RealtimeAlerts) return;
        if (TechnicalSeverity.Rank(e.Severity) < TechnicalSeverity.Rank(TechnicalSeverity.Error)) return;

        var now = DateTime.UtcNow;
        if (_lastSent.TryGetValue(e.Fingerprint, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        _lastSent[e.Fingerprint] = now;
        if (_lastSent.Count > 2000) _lastSent.Clear();

        _hub ??= services.GetService<IHubContext<DevHub>>();
        if (_hub is null) return;
        var payload = new
        {
            id = e.Id,
            severity = e.Severity,
            category = e.Category,
            eventType = e.EventType,
            message = e.Message.Length > 240 ? e.Message[..240] + "…" : e.Message,
            fingerprint = e.Fingerprint,
            timestampUtc = e.TimestampUtc
        };
        _ = _hub.Clients.Group(DevHub.Group).SendAsync("devAlert", payload)
            .ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
