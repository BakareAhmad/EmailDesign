using System.Linq.Expressions;
using System.Reflection;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Entities.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DeChimex.Persistence.Context;

/// <summary>
/// EF Core database context. All mapping lives in IEntityTypeConfiguration classes. Never
/// inline Fluent API here, per the project standards.
/// </summary>
public sealed class DeChimexDbContext(DbContextOptions<DeChimexDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<MajorCategory> MajorCategories => Set<MajorCategory>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<GalleryMedia> GalleryMedia => Set<GalleryMedia>();

    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderServiceLine> OrderServiceLines => Set<OrderServiceLine>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<EmailOutbox> EmailOutboxes => Set<EmailOutbox>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<Receipt> Receipts => Set<Receipt>();

    // Custom-order workflow (CUSTOM_ORDER_FLOW_PLAN.md §4)
    public DbSet<CategoryOption> CategoryOptions => Set<CategoryOption>();
    public DbSet<CategoryOptionChoice> CategoryOptionChoices => Set<CategoryOptionChoice>();
    public DbSet<OrderRequirementResponse> OrderRequirementResponses => Set<OrderRequirementResponse>();
    public DbSet<OrderFile> OrderFiles => Set<OrderFile>();
    public DbSet<OrderDesignProof> OrderDesignProofs => Set<OrderDesignProof>();
    public DbSet<OrderDesignReview> OrderDesignReviews => Set<OrderDesignReview>();
    public DbSet<OrderMessage> OrderMessages => Set<OrderMessage>();
    public DbSet<OrderMessageAttachment> OrderMessageAttachments => Set<OrderMessageAttachment>();
    public DbSet<OrderTimelineEntry> OrderTimelineEntries => Set<OrderTimelineEntry>();
    public DbSet<OrderCustomLine> OrderCustomLines => Set<OrderCustomLine>();

    public DbSet<Procurement> Procurements => Set<Procurement>();
    public DbSet<ProcurementItem> ProcurementItems => Set<ProcurementItem>();

    public DbSet<ShoppingCart> ShoppingCarts => Set<ShoppingCart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<Review> Reviews => Set<Review>();

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();
    public DbSet<ContactMessageReply> ContactMessageReplies => Set<ContactMessageReply>();

    public DbSet<MarketingBroadcast> MarketingBroadcasts => Set<MarketingBroadcast>();
    public DbSet<MarketingBroadcastRecipient> MarketingBroadcastRecipients => Set<MarketingBroadcastRecipient>();

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    // Developer Portal diagnostics (technical, append-only, retention-pruned; not business data).
    public DbSet<TechnicalEvent> TechnicalEvents => Set<TechnicalEvent>();
    public DbSet<IntegrationCall> IntegrationCalls => Set<IntegrationCall>();
    public DbSet<RequestMetricBucket> RequestMetricBuckets => Set<RequestMetricBucket>();
    // Quotations / QuotationLines DbSets removed (2026-09-01); the tables remain dormant in
    // the database. Quoting is handled by the Invoice "quote invoice" (Draft) path.

    /// <summary>All persisted DateTimes are UTC; mark them Utc on read (see UtcDateTimeConverter).</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveConversion<Conventions.UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<Conventions.NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }

    /// <summary>
    /// Every entity in this codebase assigns its Guid key in the constructor (see
    /// DeChimex.Domain.Common.Entity). When a NEW child is added to a tracked navigation
    /// (e.g. a new QuotationLine on a loaded Quotation, an InventoryTransaction on a loaded
    /// Inventory, a RolePermission on a loaded Role), change detection cannot tell the new
    /// row from an existing one and marks it Modified; saving then fails with
    /// DbUpdateConcurrencyException ("Attempted to update or delete an entity that does not
    /// exist in the store"). Promote Modified entries whose key is absent from the store to
    /// Added so the insert path runs instead.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await PromoteNewEntitiesWithUnknownKeysAsync(cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }

    private async Task PromoteNewEntitiesWithUnknownKeysAsync(CancellationToken ct)
    {
        var modified = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Modified)
            .ToList();

        foreach (var entry in modified)
        {
            if (!TryGetSingleGuidId(entry, out var keyProperty, out var id) || id == Guid.Empty)
                continue;

            if (await KeyExistsInStoreAsync(entry.Metadata.ClrType, keyProperty, id, ct))
                continue;

            entry.State = EntityState.Added;
        }
    }

    private Dictionary<Type, System.Linq.IQueryable>? _storeQueryables;

    /// <summary>Maps each mapped entity type to a queryable over its store rows (EF Core 9 has no non-generic Set(Type)).</summary>
    private System.Linq.IQueryable GetStoreQueryable(Type entityType)
    {
        _storeQueryables ??= GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToDictionary(
                p => p.PropertyType.GetGenericArguments()[0],
                p => (System.Linq.IQueryable)p.GetValue(this)!);

        return _storeQueryables[entityType];
    }

    private static bool TryGetSingleGuidId(EntityEntry entry, out string keyProperty, out Guid id)
    {
        var keyProps = entry.Properties.Where(p => p.Metadata.IsKey()).ToList();
        keyProperty = default!;
        id = Guid.Empty;
        if (keyProps.Count != 1) return false;
        if (keyProps[0].CurrentValue is not Guid value) return false;
        keyProperty = keyProps[0].Metadata.Name;
        id = value;
        return true;
    }

    private async Task<bool> KeyExistsInStoreAsync(Type entityType, string keyProperty, Guid id, CancellationToken ct)
    {
        try
        {
            var parameter = Expression.Parameter(entityType, "e");
            var predicate = Expression.Lambda(
                Expression.Equal(
                    Expression.Property(parameter, keyProperty),
                    Expression.Constant(id, typeof(Guid))),
                parameter);

            var queryable = GetStoreQueryable(entityType);
            // Physical existence includes soft-deleted rows. A restored wishlist row
            // is an UPDATE, not a new child to promote to INSERT.
            queryable = queryable.Provider.CreateQuery(Expression.Call(
                typeof(EntityFrameworkQueryableExtensions),
                nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters),
                new[] { entityType }, queryable.Expression));
            var anyAsync = typeof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync)
                    && m.GetParameters().Length == 3)
                .MakeGenericMethod(entityType);

            var task = (Task<bool>)anyAsync.Invoke(null, new object[] { queryable, predicate, ct })!;
            return await task;
        }
        catch
        {
            // Unmappable key shape (composite, shadow, non-Guid): leave the entry as-is.
            return true;
        }
    }
}
