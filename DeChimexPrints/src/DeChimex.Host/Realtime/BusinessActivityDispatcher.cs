using DeChimex.Persistence.Context;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
namespace DeChimex.Host.Realtime;

/// <summary>Database-backed delivery using the EXISTING audit rows and notification hub.
/// Reads through a fresh scope: only committed business rows are visible. Delivery is
/// at-least-once; clients reconcile persisted rows by ID. No separate event store/bus.</summary>
public sealed class BusinessActivityDispatcher(IServiceScopeFactory scopes,IHubContext<NotificationHub> hub,
    ILogger<BusinessActivityDispatcher> logger, DeChimex.Application.Common.Interfaces.IJobMonitor jobs) : BackgroundService
{
    public const string JobKey = "business-activity-dispatcher";

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.Register(JobKey, "Business activity dispatcher",
            "Pushes new business-activity audit rows to authorised staff over SignalR.", TimeSpan.FromSeconds(2));
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            using var run = jobs.Begin(JobKey);
            try
            {
                using var scope=scopes.CreateScope();
                var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
                var batch=await db.AuditLogs.Where(a=>a.ActivityType!=null&&a.IsRealtime&&a.RealtimeDispatchedAtUtc==null)
                    .OrderBy(a=>a.TimestampUtc).Take(100).ToListAsync(ct);
                if(batch.Count>0)
                {
                    var users=await ActivityAccess.AuthorizedUsers(db).ToListAsync(ct);
                    if(users.Count>0) await hub.Clients.Groups(users.Select(NotificationHub.UserGroup).ToArray())
                        .SendAsync("businessActivityChanged",new { refresh=true },ct);
                    foreach(var row in batch)row.MarkDispatched(DateTime.UtcNow);
                    await db.SaveChangesAsync(ct);
                }
                run.Succeeded(batch.Count>0?$"{batch.Count} activity row(s) dispatched":null);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex){run.Failed(ex,"Activity delivery deferred: "+ex.GetType().Name,recordEvent:false);logger.LogWarning("Activity delivery deferred ({Type}); persisted history remains available.",ex.GetType().Name);}
            try { await Task.Delay(TimeSpan.FromSeconds(2),ct); }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
        }
    }
}
