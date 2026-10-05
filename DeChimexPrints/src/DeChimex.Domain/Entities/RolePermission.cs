using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>Join entity linking a role to a permission.</summary>
public sealed class RolePermission : Entity
{
    private RolePermission() { } // EF
    private RolePermission(Guid roleId, Guid permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }
    public Permission? Permission { get; private set; }

    internal static RolePermission Create(Guid roleId, Guid permissionId) => new(roleId, permissionId);
}
