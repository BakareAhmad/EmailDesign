using DeChimex.Application.Features.Activity;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
namespace DeChimex.Persistence.Queries;
public sealed class ActivityQueries(DeChimexDbContext db) : IActivityQueries
{
    public async Task<ActivityPage> SearchAsync(ActivityFilter f,CancellationToken ct)
    {
        var q=db.AuditLogs.AsNoTracking().Where(a=>a.ActivityType!=null);
        if(!string.IsNullOrWhiteSpace(f.Category))q=q.Where(a=>a.Category==f.Category);
        if(!string.IsNullOrWhiteSpace(f.Type))q=q.Where(a=>a.ActivityType==f.Type);
        if(!string.IsNullOrWhiteSpace(f.Search)){var s=f.Search.Trim();q=q.Where(a=>a.Summary!.Contains(s)||a.Title!.Contains(s)||(a.OrderId!=null&&db.Orders.Any(o=>o.Id==a.OrderId&&o.OrderNumber.Contains(s))));}
        if(!string.IsNullOrWhiteSpace(f.Actor)){var s=f.Actor.Trim();q=q.Where(a=>a.ActorName!.Contains(s));}
        if(!string.IsNullOrWhiteSpace(f.Order)){var s=f.Order.Trim();q=q.Where(a=>db.Orders.Any(o=>o.Id==a.OrderId&&o.OrderNumber.Contains(s)));}
        if(!string.IsNullOrWhiteSpace(f.Customer)){var s=f.Customer.Trim();q=q.Where(a=>db.Customers.Any(c=>c.Id==a.CustomerId&&(c.FirstName+" "+c.LastName).Contains(s)));}
        if(!string.IsNullOrWhiteSpace(f.Product)){var s=f.Product.Trim();q=q.Where(a=>db.Products.Any(p=>p.Id==a.ProductId&&p.Name.Contains(s)));}
        if(!string.IsNullOrWhiteSpace(f.Staff)){var s=f.Staff.Trim();q=q.Where(a=>db.Users.Any(u=>u.Id==a.StaffUserId&&u.Username!=null&&u.Username.Value.Contains(s)));}
        if(f.ActorUserId.HasValue)q=q.Where(a=>a.ActorUserId==f.ActorUserId);
        if(f.OrderId.HasValue)q=q.Where(a=>a.OrderId==f.OrderId);
        if(f.CustomerId.HasValue)q=q.Where(a=>a.CustomerId==f.CustomerId);
        if(f.ProductId.HasValue)q=q.Where(a=>a.ProductId==f.ProductId);
        if(f.StaffUserId.HasValue)q=q.Where(a=>a.StaffUserId==f.StaffUserId);
        if(f.From.HasValue)q=q.Where(a=>a.TimestampUtc>=f.From.Value.Date);
        if(f.To.HasValue){var end=f.To.Value.Date.AddDays(1);q=q.Where(a=>a.TimestampUtc<end);}
        if(!string.IsNullOrWhiteSpace(f.Importance))q=q.Where(a=>a.Importance==f.Importance);
        if(f.HistoryCutoff.HasValue)q=q.Where(a=>a.IsRealtime||a.TimestampUtc<=f.HistoryCutoff);
        var items=await q.OrderByDescending(a=>a.TimestampUtc).ThenByDescending(a=>a.Id)
            .Skip((f.Page-1)*50).Take(51)
            .Select(a=>new ActivityItem(a.Id,a.ActivityType!,a.Category!,a.Title!,a.Summary!,a.TimestampUtc,
                a.ActorUserId,a.ActorName!,a.ActorRole!,a.EntityName,a.EntityId,a.OrderId,a.CustomerId,a.ProductId,a.StaffUserId,a.Importance!,a.IsRealtime))
            .ToListAsync(ct);
        return new ActivityPage(items.Take(50).ToList(),f.Page,items.Count>50);
    }
}
