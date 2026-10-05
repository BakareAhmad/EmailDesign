using System.Security.Claims;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Http;

namespace DeChimex.Infrastructure.Authentication;

/// <summary>Resolves the current principal from the HTTP context claims.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid? UserId
    {
        get
        {
            var value = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email);

    public IReadOnlyList<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() ?? new List<string>();

    public IReadOnlyList<string> Permissions =>
        Principal?.FindAll(DeChimex.Shared.Constants.Permissions.ClaimType).Select(c => c.Value).ToList() ?? new List<string>();

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public bool HasPermission(string permission) => Permissions.Contains(permission);
}
