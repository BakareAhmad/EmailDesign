using System.Security.Claims;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Authorization;

/// <summary>Authorization requirement for a single permission claim.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>Grants access when the principal carries the required permission claim.</summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var hasPermission = context.User.Claims
            .Any(c => c.Type == Permissions.ClaimType && c.Value == requirement.Permission);

        if (hasPermission)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

/// <summary>Requires that the principal does NOT carry a given role claim.</summary>
public sealed class NotRoleRequirement(string role) : IAuthorizationRequirement
{
    public string Role { get; } = role;
}

public sealed class NotRoleAuthorizationHandler : AuthorizationHandler<NotRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, NotRoleRequirement requirement)
    {
        if (!context.User.HasClaim(ClaimTypes.Role, requirement.Role))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Dynamically materialises an authorization policy for any "permission:{name}" policy name,
/// so controllers can annotate [Authorize(Policy = Permissions.Products.Create)] without
/// registering every policy by hand.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        => _fallback = new DefaultAuthorizationPolicyProvider(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // Every known permission string (business or Developer Portal) is a valid policy name.
        if (Permissions.IsKnown(policyName))
        {
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }
        return _fallback.GetPolicyAsync(policyName);
    }
}
