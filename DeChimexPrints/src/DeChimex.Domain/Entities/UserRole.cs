using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>Join entity linking a user to a role.</summary>
public sealed class UserRole : Entity
{
    private UserRole() { } // EF
    private UserRole(Guid userId, Guid roleId)
    {
        UserId = userId;
        RoleId = roleId;
    }

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public Role? Role { get; private set; }

    internal static UserRole Create(Guid userId, Guid roleId) => new(userId, roleId);
}
