using System.Security.Claims;
using DeChimex.Host.Realtime;
using DeChimex.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace DeChimex.IntegrationTests.Realtime;

/// <summary>Only business staff join the staff notification audience (the all-staff and
/// perm:* groups). Customers — who hold orders.create — and developer-only accounts do not.</summary>
public class NotificationHubAudienceTests
{
    private static ClaimsPrincipal Principal(string[] roles, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Theory]
    [InlineData(RoleNames.Owner)]
    [InlineData(RoleNames.Manager)]
    [InlineData(RoleNames.SalesStaff)]
    [InlineData(RoleNames.InventoryStaff)]
    [InlineData(RoleNames.Designer)]
    public void Staff_roles_join_the_staff_audience(string role)
        => NotificationHub.IsStaffAudience(Principal(new[] { role }, Permissions.Orders.View)).Should().BeTrue();

    [Fact]
    public void Customer_with_a_permission_claim_is_not_staff()
        => NotificationHub.IsStaffAudience(Principal(new[] { RoleNames.Customer }, Permissions.Orders.Create)).Should().BeFalse();

    [Fact]
    public void Developer_only_account_is_not_staff()
        => NotificationHub.IsStaffAudience(Principal(new[] { RoleNames.Developer }, Permissions.Developer.All().ToArray())).Should().BeFalse();

    [Fact]
    public void Developer_who_is_also_staff_is_staff()
        => NotificationHub.IsStaffAudience(Principal(new[] { RoleNames.Developer, RoleNames.Manager })).Should().BeTrue();
}
