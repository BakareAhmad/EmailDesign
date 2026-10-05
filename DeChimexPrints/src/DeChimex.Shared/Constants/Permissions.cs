namespace DeChimex.Shared.Constants;

/// <summary>
/// Central registry of all permission claims used for policy-based authorization.
/// No magic strings: every policy references a constant here.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "permission";

    /// <summary>
    /// Policy for staff-only counter workflows (POS orders, quote invoices):
    /// requires the orders.create permission AND excludes the Customer role,
    /// so portal customers can never reach staff-only forms.
    /// </summary>
    public const string StaffCounterPolicy = "staff-counter-orders";

    public static class Products
    {
        public const string View = "products.view";
        public const string Create = "products.create";
        public const string Update = "products.update";
        public const string Delete = "products.delete";
    }

    public static class Categories
    {
        public const string View = "categories.view";
        public const string Manage = "categories.manage";
    }

    public static class Services
    {
        public const string View = "services.view";
        public const string Manage = "services.manage";
    }

    public static class Inventory
    {
        public const string View = "inventory.view";
        public const string Adjust = "inventory.adjust";
        public const string StockIn = "inventory.stockin";
        public const string StockOut = "inventory.stockout";
    }

    public static class Orders
    {
        public const string View = "orders.view";
        public const string ViewAll = "orders.viewall";
        public const string Create = "orders.create";
        public const string UpdateStatus = "orders.updatestatus";
        public const string Cancel = "orders.cancel";
        public const string Refund = "orders.refund";
    }

    public static class Procurement
    {
        public const string View = "procurement.view";
        public const string Approve = "procurement.approve";
        public const string Receive = "procurement.receive";
    }

    public static class Customers
    {
        public const string View = "customers.view";
        public const string Manage = "customers.manage";
    }

    public static class Users
    {
        public const string View = "users.view";
        public const string Create = "users.create";
        public const string Update = "users.update";
        public const string Disable = "users.disable";
        public const string AssignRoles = "users.assignroles";
    }

    public static class Gallery
    {
        public const string View = "gallery.view";
        public const string Manage = "gallery.manage";
    }

    public static class Messages
    {
        public const string View = "messages.view";
    }

    /// <summary>
    /// Marketing email broadcasts (admin-composed campaigns to registered customers).
    /// Deliberately separate from transactional email permissions: only Owner/Manager
    /// get these by default (the Manager canonical map is All() minus users.*, so it
    /// picks marketing up automatically).
    /// </summary>
    public static class Marketing
    {
        public const string View = "marketing.view";
        public const string Manage = "marketing.manage";
    }

    public static class Activity { public const string View = "activity.view"; }

    public static class Reports
    {
        public const string View = "reports.view";
        public const string Export = "reports.export";
    }

    public static class Settings
    {
        public const string View = "settings.view";
        public const string Manage = "settings.manage";
    }

    /// <summary>
    /// DEVELOPER PORTAL permissions (technical operations). Deliberately NOT part of
    /// <see cref="All"/>: the Owner/Manager canonical maps are built from All(), so keeping
    /// these separate guarantees Owner/Manager never receive developer access implicitly.
    /// They are attached only to the seeded <c>Developer</c> system role; access is granted
    /// by assigning that role (see DEV_PORTAL_REPORT.md).
    /// </summary>
    public static class Developer
    {
        /// <summary>Open the Developer Portal: dashboard, health, jobs, database, configuration, performance, system info.</summary>
        public const string PortalView = "dev.portal.view";
        /// <summary>Technical log + error center (includes stack traces).</summary>
        public const string LogsView = "dev.logs.view";
        /// <summary>Paystack / Brevo integration diagnostics.</summary>
        public const string PaymentsView = "dev.payments.view";
        /// <summary>Service-fee accumulation, ledger and reconciliation export.</summary>
        public const string ServiceFeesView = "dev.servicefees.view";
        /// <summary>Run the audited, idempotent maintenance actions.</summary>
        public const string MaintenanceRun = "dev.maintenance.run";

        public const string Prefix = "dev.";

        public static IReadOnlyList<string> All() => new[]
        {
            PortalView, LogsView, PaymentsView, ServiceFeesView, MaintenanceRun
        };

        public static bool IsDeveloperPermission(string? permission) =>
            permission is not null && permission.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>True for any permission string known to the application (business or developer).</summary>
    public static bool IsKnown(string policyName) => All().Contains(policyName) || Developer.All().Contains(policyName);

    /// <summary>Returns every permission value declared in this registry.</summary>
    public static IReadOnlyList<string> All()
    {
        return new[]
        {
            Products.View, Products.Create, Products.Update, Products.Delete,
            Categories.View, Categories.Manage,
            Services.View, Services.Manage,
            Inventory.View, Inventory.Adjust, Inventory.StockIn, Inventory.StockOut,
            Orders.View, Orders.ViewAll, Orders.Create, Orders.UpdateStatus, Orders.Cancel, Orders.Refund,
            Procurement.View, Procurement.Approve, Procurement.Receive,
            Customers.View, Customers.Manage,
            Users.View, Users.Create, Users.Update, Users.Disable, Users.AssignRoles,
            Gallery.View, Gallery.Manage,
            Messages.View,
            Marketing.View, Marketing.Manage,
            Activity.View, Reports.View, Reports.Export,
            Settings.View, Settings.Manage
        };
    }
}
