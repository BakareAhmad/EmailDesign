using System.Security.Claims;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Primitives;

namespace DeChimex.Host.Middleware;

/// <summary>
/// Per-request authorization freshness (AUD-006). The auth cookie freezes roles, permission
/// claims and the must_change_password flag at sign-in time and only expires after 8 sliding
/// hours — so a demoted or DISABLED staff member used to keep full access for the rest of the
/// cookie's life. The Developer Portal already solves this (DevAccess re-checks per request);
/// this middleware brings the same semantics to the business app:
///  - a disabled/deleted user is signed out on their very next request;
///  - when the user's SecurityStamp (bumped by role/permission/activation/password changes)
///    differs from the stamp claim, the principal is rebuilt from the database and the cookie
///    is re-issued — permission changes apply immediately.
/// Cost: one indexed user read per authenticated request (explicitly within budget at this
/// scale); the permission query runs only on a detected stamp mismatch.
/// </summary>
public sealed class SessionFreshnessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IUserRepository users,
        IRoleRepository roles,
        IPermissionRepository permissions)
    {
        // A client that already left must get NOTHING (the Dev-portal abort pipeline asserts
        // an empty 499): bail before any database work, and run the lookups to completion
        // (CancellationToken.None) so a mid-flight abort can't turn into an exception that
        // would be rendered as an error page for a request nobody is reading.
        if (context.RequestAborted.IsCancellationRequested)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true
            && string.Equals(context.User.Identity.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            var user = await users.GetWithRolesAsync(userId, CancellationToken.None);

            // Offboarding: the account is gone or disabled — terminate the session NOW.
            if (user is null || !user.IsActive)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
                if (IsApi(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                context.Response.Redirect("/Account/Login");
                return;
            }

            var stampClaim = context.User.FindFirst("secstamp")?.Value;
            if (!string.Equals(stampClaim, user.SecurityStamp, StringComparison.Ordinal))
            {
                // Stale authority (or legacy pre-stamp cookie): re-issue from current facts.
                var roleIds = user.Roles.Select(r => r.RoleId).ToList();
                var roleNames = (await roles.GetByIdsAsync(roleIds, CancellationToken.None)).Select(r => r.Name).ToList();
                var permissionNames = await permissions.GetPermissionNamesForUserAsync(user.Id, CancellationToken.None);

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new(ClaimTypes.Email, user.Email.Value),
                    new("name", user.FullName),
                    new("secstamp", user.SecurityStamp)
                };
                claims.AddRange(roleNames.Select(r => new Claim(ClaimTypes.Role, r)));
                claims.AddRange(permissionNames.Select(p => new Claim(Permissions.ClaimType, p)));
                if (user.MustChangePassword)
                    claims.Add(new Claim("must_change_password", "true"));

                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
                context.User = principal;
                // Re-issue the cookie so the refresh persists (and doesn't repeat per request).
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            }
        }

        await next(context);
    }

    private static bool IsApi(HttpContext context)
        => context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
           || context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);
}
