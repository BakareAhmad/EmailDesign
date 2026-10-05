using DeChimex.Domain.Entities;

namespace DeChimex.Domain.Repositories;

public interface ICategoryRepository : IRepository<Category>
{
    Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default);

    /// <summary>Highest DisplayOrder currently in use (0 when there are no categories).</summary>
    Task<int> GetMaxDisplayOrderAsync(CancellationToken ct = default);

    /// <summary>Loads a category with its ordering options (and their choices).</summary>
    Task<Category?> GetWithOptionsAsync(Guid id, CancellationToken ct = default);
}

public interface IProductRepository : IRepository<Product>
{
    Task<Product?> GetWithImagesAsync(Guid id, CancellationToken ct = default);
    Task<bool> SkuExistsAsync(string sku, Guid? excludeId = null, CancellationToken ct = default);

    /// <summary>True when another ACTIVE product already owns the given slug. Mirrors the
    /// unique filtered index on Products.Slug ([IsDeleted] = 0) that SQL Server enforces —
    /// callers resolve collisions BEFORE saving so a valid edit is never lost to a
    /// DbUpdateException (which the InMemory test provider would never raise).</summary>
    Task<bool> SlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default);
}

public interface IServiceRepository : IRepository<Service> { }

public interface IInventoryRepository : IRepository<Inventory>
{
    Task<Inventory?> GetByProductIdAsync(Guid productId, CancellationToken ct = default);
    Task<IReadOnlyList<Inventory>> GetLowStockAsync(CancellationToken ct = default);
}

public interface ICustomerRepository : IRepository<Customer>
{
    Task<Customer?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<Customer?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<Customer?> GetWithAddressesAsync(Guid id, CancellationToken ct = default);

    /// <summary>Resolves a customer from the opaque token carried by marketing unsubscribe links.</summary>
    Task<Customer?> GetByUnsubscribeTokenAsync(Guid token, CancellationToken ct = default);
}

public interface IOrderRepository : IRepository<Order>
{
    Task<Order?> GetWithLinesAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads an order with every custom-order child (responses, files, messages,
    /// timeline, proofs, custom lines) for the workspace read/write path.</summary>
    Task<Order?> GetWithWorkspaceAsync(Guid id, CancellationToken ct = default);
    Task<int> CountForYearAsync(int year, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetForCustomerAsync(Guid customerId, CancellationToken ct = default);
    /// <summary>Re-labels every staff-authored message by <paramref name="authorUserId"/>
    /// (username changes / historical backfill). Returns the number relabelled.</summary>
    Task<int> RenameStaffAuthorAsync(Guid authorUserId, string newName, CancellationToken ct = default);
}

public interface IInvoiceRepository : IRepository<Invoice>
{
    Task<Invoice?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default);
    Task<int> CountForYearAsync(int year, CancellationToken ct = default);

    /// <summary>Count of quote-only documents (QTE- references) issued in the year —
    /// feeds the separate QTE sequence so quotes never consume invoice numbers.</summary>
    Task<int> CountQuotesForYearAsync(int year, CancellationToken ct = default);
}

public interface IReceiptRepository : IRepository<Receipt>
{
    Task<Receipt?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default);
    Task<int> CountForYearAsync(int year, CancellationToken ct = default);
}

public interface IPaymentRepository : IRepository<Payment>
{
    Task<Payment?> GetByReferenceAsync(string reference, CancellationToken ct = default);
    /// <summary>Locates a payment by the provider's refund reference (AUD-005 refund.* settlement events).</summary>
    Task<Payment?> GetByRefundReferenceAsync(string refundReference, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetForCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default);
}

public interface IProcurementRepository : IRepository<Procurement>
{
    Task<Procurement?> GetWithItemsAsync(Guid id, CancellationToken ct = default);
    Task<int> CountForYearAsync(int year, CancellationToken ct = default);
}

public interface IGalleryRepository : IRepository<GalleryMedia>
{
    /// <summary>Loads a gallery post together with its extra carousel images.</summary>
    Task<GalleryMedia?> GetWithImagesAsync(Guid id, CancellationToken ct = default);
}

public interface IShoppingCartRepository : IRepository<ShoppingCart>
{
    Task<ShoppingCart?> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default);
}

public interface IWishlistRepository : IRepository<WishlistItem>
{
    Task<IReadOnlyList<WishlistItem>> GetForCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task<WishlistItem?> FindAsync(Guid customerId, Guid productId, CancellationToken ct = default);
}

public interface IReviewRepository : IRepository<Review>
{
    Task<IReadOnlyList<Review>> GetForProductAsync(Guid productId, bool approvedOnly, CancellationToken ct = default);
}

public interface INotificationRepository : IRepository<Notification>
{
}

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> GetWithRolesAsync(Guid id, CancellationToken ct = default);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    /// <summary>Case-insensitive username check (usernames are stored normalised to
    /// lower-case). <paramref name="excludeUserId"/> skips one user for change flows.</summary>
    Task<bool> UsernameExistsAsync(string username, Guid? excludeUserId = null, CancellationToken ct = default);
    /// <summary>AUD-006: users currently holding a role (with roles tracked) so permission
    /// changes on that role can bump every affected user's SecurityStamp.</summary>
    Task<IReadOnlyList<User>> GetUsersInRoleAsync(Guid roleId, CancellationToken ct = default);
}

public interface IRoleRepository : IRepository<Role>
{
    Task<Role?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<Role?> GetWithPermissionsAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Role>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<int> CountUsersInRoleAsync(Guid roleId, CancellationToken ct = default);
}

public interface IPushSubscriptionRepository : IRepository<PushSubscription>
{
    Task<PushSubscription?> GetByUserAndEndpointAsync(Guid userId, string endpoint, CancellationToken ct = default);
    Task<IReadOnlyList<PushSubscription>> ListByUserAsync(Guid userId, CancellationToken ct = default);
    Task<PushSubscription?> GetByEndpointAsync(string endpoint, CancellationToken ct = default);
}

public interface IPermissionRepository
{
    Task<IReadOnlyList<Permission>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPermissionNamesForUserAsync(Guid userId, CancellationToken ct = default);
}

public interface IApplicationSettingRepository : IRepository<ApplicationSetting>
{
    Task<ApplicationSetting?> GetByKeyAsync(string key, CancellationToken ct = default);
}

// IAuditLogWriter was removed (2026-09-05): the audit-log WRITE side lives in
// Persistence/Interceptors/AuditableSoftDeleteInterceptor, which adds AuditLog rows
// to the same DbContext so they commit atomically with the business change. Nothing
// ever injected this abstraction, and a separate SaveChanges here would have broken
// that atomicity. Do not reintroduce it without wiring it into the interceptor.

public interface IContactMessageRepository : IRepository<ContactMessage> { }

/// <summary>
/// Marketing broadcasts: admin-composed campaigns to subscribed registered customers.
/// Recipient rows double as the at-most-once delivery ledger (see MarketingBroadcastRecipient).
/// </summary>
public interface IMarketingRepository : IRepository<MarketingBroadcast>
{
    Task<MarketingBroadcast?> GetWithRecipientsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Eligible marketing recipients: registered (non-guest) customers with an
    /// email address who have not unsubscribed. Staff/admin accounts are never customers.</summary>
    Task<IReadOnlyList<(Guid CustomerId, string Email, string FirstName)>> GetEligibleRecipientsAsync(CancellationToken ct = default);

    /// <summary>Next batch of PENDING recipients for the background worker (bounded so a
    /// huge broadcast is processed in manageable chunks, not one giant loop).</summary>
    Task<IReadOnlyList<MarketingBroadcastRecipient>> GetPendingRecipientsAsync(Guid broadcastId, int take, CancellationToken ct = default);
}
