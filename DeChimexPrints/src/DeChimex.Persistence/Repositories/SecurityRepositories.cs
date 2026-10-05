using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Repositories;

public sealed class UserRepository(DeChimexDbContext context) : RepositoryBase<User>(context), IUserRepository
{
    public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return Set.FirstOrDefaultAsync(u => u.Email.Value == normalized, ct);
    }

    public Task<User?> GetWithRolesAsync(Guid id, CancellationToken ct = default)
        => Set.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return Set.AnyAsync(u => u.Email.Value == normalized, ct);
    }

    public Task<bool> UsernameExistsAsync(string username, Guid? excludeUserId = null, CancellationToken ct = default)
    {
        var normalized = username.Trim().ToLowerInvariant();
        return Set.AnyAsync(u => u.Username != null && u.Username.Value == normalized
            && (excludeUserId == null || u.Id != excludeUserId.Value), ct);
    }

    public override Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Set.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<User>> GetUsersInRoleAsync(Guid roleId, CancellationToken ct = default)
        => await Set.Include(u => u.Roles)
            .Where(u => u.Roles.Any(r => r.RoleId == roleId))
            .ToListAsync(ct);
}

public sealed class RoleRepository(DeChimexDbContext context) : RepositoryBase<Role>(context), IRoleRepository
{
    public Task<Role?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalized = name.ToUpperInvariant();
        return Set.FirstOrDefaultAsync(r => r.NormalizedName == normalized, ct);
    }

    public Task<Role?> GetWithPermissionsAsync(Guid id, CancellationToken ct = default)
        => Set.Include(r => r.Permissions).ThenInclude(rp => rp.Permission).FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<Role>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        return await Set.Where(r => idList.Contains(r.Id)).ToListAsync(ct);
    }

    public Task<int> CountUsersInRoleAsync(Guid roleId, CancellationToken ct = default)
        => Context.UserRoles.CountAsync(ur => ur.RoleId == roleId, ct);
}

public sealed class PermissionRepository(DeChimexDbContext context) : IPermissionRepository
{
    public async Task<IReadOnlyList<Permission>> ListAsync(CancellationToken ct = default)
        => await context.Permissions.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GetPermissionNamesForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var query =
            from ur in context.UserRoles.Where(x => x.UserId == userId)
            join rp in context.RolePermissions on ur.RoleId equals rp.RoleId
            join p in context.Permissions on rp.PermissionId equals p.Id
            select p.Name;
        return await query.Distinct().ToListAsync(ct);
    }
}
