using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;
namespace DeChimex.Host.Realtime;
/// <summary>Reuses the permission tables; live reads never trust stale cookie permissions.</summary>
public static class ActivityAccess
{
    public static IQueryable<Guid> AuthorizedUsers(DeChimexDbContext db) =>
        db.Users.Where(u=>u.IsActive && u.Username!=null && db.UserRoles.Any(ur=>ur.UserId==u.Id &&
            db.Roles.Any(r=>r.Id==ur.RoleId && db.RolePermissions.Any(rp=>rp.RoleId==r.Id &&
                db.Permissions.Any(p=>p.Id==rp.PermissionId&&p.Name==Permissions.Activity.View)))))
            .Select(u=>u.Id);
}
