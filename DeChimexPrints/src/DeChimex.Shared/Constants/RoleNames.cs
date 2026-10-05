namespace DeChimex.Shared.Constants;

/// <summary>Well-known staff roles for the administration dashboard.</summary>
public static class RoleNames
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string SalesStaff = "SalesStaff";
    public const string InventoryStaff = "InventoryStaff";
    public const string Designer = "Designer";
    public const string Customer = "Customer";

    /// <summary>
    /// Technical maintenance role for the Developer Portal. NOT a business staff role:
    /// it carries only dev.* permissions and is intentionally absent from StaffRoles(),
    /// so developers never appear in sales assignment / staff lists and cannot reach
    /// business screens unless an Owner explicitly grants business permissions.
    /// </summary>
    public const string Developer = "Developer";

    public static IReadOnlyList<string> StaffRoles() =>
        new[] { Owner, Manager, SalesStaff, InventoryStaff, Designer };

    /// <summary>True when the roles include a business staff role. Customers and
    /// developer-only accounts are not staff (e.g. for the staff notification audience).</summary>
    public static bool IsStaff(IEnumerable<string> roles)
    {
        var staff = StaffRoles();
        return roles.Any(r => staff.Contains(r, StringComparer.OrdinalIgnoreCase));
    }
}
