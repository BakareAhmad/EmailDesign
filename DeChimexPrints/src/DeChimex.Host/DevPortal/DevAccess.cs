using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.DevPortal;

/// <summary>
/// Live permission check against the role/permission tables (same pattern as
/// Realtime/ActivityAccess): the Developer Portal never trusts permissions cached in the
/// auth cookie alone, so revoking the Developer role or a dev.* permission takes effect on
/// the very next request, not at cookie expiry.
/// </summary>
public static class DevAccess
{
    public static Task<bool> HasPermissionAsync(DeChimexDbContext db, Guid userId, string permission, CancellationToken ct = default)
        => db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive &&
            db.UserRoles.Any(ur => ur.UserId == u.Id &&
                db.RolePermissions.Any(rp => rp.RoleId == ur.RoleId &&
                    db.Permissions.Any(p => p.Id == rp.PermissionId && p.Name == permission))), ct);

    public static async Task<HashSet<string>> DevPermissionsAsync(DeChimexDbContext db, Guid userId, CancellationToken ct = default)
    {
        var devNames = Permissions.Developer.All().ToList();
        var names = await (
            from u in db.Users.AsNoTracking()
            where u.Id == userId && u.IsActive
            from ur in db.UserRoles where ur.UserId == u.Id
            from rp in db.RolePermissions where rp.RoleId == ur.RoleId
            from p in db.Permissions where p.Id == rp.PermissionId && devNames.Contains(p.Name)
            select p.Name).Distinct().ToListAsync(ct);
        return names.ToHashSet(StringComparer.Ordinal);
    }
}
