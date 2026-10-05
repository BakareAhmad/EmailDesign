using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Domain.ValueObjects;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Repositories;

public sealed class CategoryRepository(DeChimexDbContext context) : RepositoryBase<Category>(context), ICategoryRepository
{
    public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default)
    {
        var n = name.Trim();
        return Set.AnyAsync(c => c.Name == n && (excludeId == null || c.Id != excludeId), ct);
    }

    public async Task<int> GetMaxDisplayOrderAsync(CancellationToken ct = default)
        => await Set.MaxAsync(c => (int?)c.DisplayOrder, ct) ?? 0;

    public Task<Category?> GetWithOptionsAsync(Guid id, CancellationToken ct = default)
        => Set.Include(c => c.Options).ThenInclude(o => o.Choices).FirstOrDefaultAsync(c => c.Id == id, ct);
}

public sealed class ProductRepository(DeChimexDbContext context) : RepositoryBase<Product>(context), IProductRepository
{
    public Task<Product?> GetWithImagesAsync(Guid id, CancellationToken ct = default)
        => Set.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<bool> SkuExistsAsync(string sku, Guid? excludeId = null, CancellationToken ct = default)
    {
        var s = sku.Trim().ToUpperInvariant();
        return Set.AnyAsync(p => p.Sku == s && (excludeId == null || p.Id != excludeId), ct);
    }

    // SQL-SERVER TRANSLATION NOTE: compare the Slug VALUE OBJECT directly, never
    // p.Slug.Value == <string> — EF Core cannot translate member access on a
    // converted value object inside a PREDICATE (it can in projections), so the
    // .Value form throws InvalidOperationException ("could not be translated")
    // on any relational provider (InMemory happily runs it, which is why this
    // only surfaced once the app moved to SQL Server). Comparing the VO itself
    // lets EF apply the configured HasConversion to the parameter. Callers pass
    // already-normalized slugs (Slug.FromText outputs); FromText is idempotent
    // on such values, so re-normalizing here cannot change the result.
    public Task<bool> SlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default)
    {
        var normalized = Slug.FromText(slug);
        return Set.AnyAsync(p => p.Slug == normalized && (excludeId == null || p.Id != excludeId), ct);
    }
}

public sealed class ServiceRepository(DeChimexDbContext context) : RepositoryBase<Service>(context), IServiceRepository { }

public sealed class InventoryRepository(DeChimexDbContext context) : RepositoryBase<Inventory>(context), IInventoryRepository
{
    public Task<Inventory?> GetByProductIdAsync(Guid productId, CancellationToken ct = default)
        => Set.Include(i => i.Transactions).FirstOrDefaultAsync(i => i.ProductId == productId, ct);

    // InventoryTransaction children carry a client-assigned Guid key (Entity base). When a tracked
    // Inventory records a new transaction (StockIn/Reserve/Adjust), the change tracker marks that
    // non-default-key child Modified (-> UPDATE a non-existent row -> DbUpdateConcurrencyException).
    // Transactions are append-only records (created, never updated), so any Modified transaction is a
    // freshly-recorded one that must INSERT. Detect changes, then force such entries to Added.
    public override void Update(Inventory aggregate)
    {
        base.Update(aggregate);
        Context.ChangeTracker.DetectChanges();
        foreach (var e in Context.ChangeTracker.Entries<InventoryTransaction>())
            if (e.State == EntityState.Modified)
                e.State = EntityState.Added;
    }

    public async Task<IReadOnlyList<Inventory>> GetLowStockAsync(CancellationToken ct = default)
        => await Set.Where(i => i.QuantityOnHand - i.QuantityReserved <= i.LowStockThreshold).ToListAsync(ct);
}

public sealed class CustomerRepository(DeChimexDbContext context) : RepositoryBase<Customer>(context), ICustomerRepository
{
    public Task<Customer?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(c => c.UserId == userId, ct);

    public Task<Customer?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return Set.FirstOrDefaultAsync(c => c.Email.Value == normalized, ct);
    }

    public Task<Customer?> GetWithAddressesAsync(Guid id, CancellationToken ct = default)
        => Set.Include(c => c.Addresses).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Customer?> GetByUnsubscribeTokenAsync(Guid token, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(c => c.UnsubscribeToken == token, ct);
}

public sealed class OrderRepository(DeChimexDbContext context) : RepositoryBase<Order>(context), IOrderRepository
{
    public Task<Order?> GetWithLinesAsync(Guid id, CancellationToken ct = default)
        => Set.Include(o => o.Items).Include(o => o.Services).Include(o => o.CustomLines)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> GetWithWorkspaceAsync(Guid id, CancellationToken ct = default)
        => Set.Include(o => o.Items).Include(o => o.Services)
            .Include(o => o.Responses)
            .Include(o => o.Files)
            .Include(o => o.Messages).ThenInclude(m => m.Attachments)
            .Include(o => o.Timeline)
            .Include(o => o.Proofs)
            .Include(o => o.DesignReviews)
            .Include(o => o.CustomLines)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<int> CountForYearAsync(int year, CancellationToken ct = default)
        => Set.IgnoreQueryFilters().CountAsync(o => o.CreatedDateUtc.Year == year, ct);

    public async Task<IReadOnlyList<Order>> GetForCustomerAsync(Guid customerId, CancellationToken ct = default)
        => await Set.Where(o => o.CustomerId == customerId).OrderByDescending(o => o.CreatedDateUtc).ToListAsync(ct);

    public async Task<int> RenameStaffAuthorAsync(Guid authorUserId, string newName, CancellationToken ct = default)
    {
        // Load-then-rename (no ExecuteUpdate): the InMemory provider used by tests
        // cannot translate bulk updates, and per-author message counts are small.
        var messages = await Context.OrderMessages
            .Where(m => m.AuthorRole == "staff" && m.AuthorUserId == authorUserId && m.AuthorName != newName)
            .ToListAsync(ct);
        foreach (var m in messages)
            m.RenameAuthor(newName);
        return messages.Count;
    }
}

public sealed class InvoiceRepository(DeChimexDbContext context) : RepositoryBase<Invoice>(context), IInvoiceRepository
{
    public Task<Invoice?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(i => i.OrderId == orderId, ct);

    // Only real invoice references (INV-) count toward the invoice sequence; quote-only
    // documents (QTE-) run on their own sequence so quotes never consume invoice numbers.
    public Task<int> CountForYearAsync(int year, CancellationToken ct = default)
        => Set.IgnoreQueryFilters().CountAsync(i => i.CreatedDateUtc.Year == year && !i.InvoiceNumber.StartsWith("QTE-"), ct);

    public Task<int> CountQuotesForYearAsync(int year, CancellationToken ct = default)
        => Set.IgnoreQueryFilters().CountAsync(i => i.CreatedDateUtc.Year == year && i.InvoiceNumber.StartsWith("QTE-"), ct);
}

public sealed class PaymentRepository(DeChimexDbContext context) : RepositoryBase<Payment>(context), IPaymentRepository
{
    public Task<Payment?> GetByReferenceAsync(string reference, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(p => p.Reference == reference, ct);

    public Task<Payment?> GetByRefundReferenceAsync(string refundReference, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(p => p.RefundReference == refundReference, ct);

    public async Task<IReadOnlyList<Payment>> GetForCustomerAsync(Guid customerId, CancellationToken ct = default)
        => await Set.Where(p => p.CustomerId == customerId).OrderByDescending(p => p.CreatedDateUtc).ToListAsync(ct);

    public async Task<IReadOnlyList<Payment>> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => await Set.Where(p => p.OrderId == orderId).OrderByDescending(p => p.CreatedDateUtc).ToListAsync(ct);
}

public sealed class ProcurementRepository(DeChimexDbContext context) : RepositoryBase<Procurement>(context), IProcurementRepository
{
    public Task<Procurement?> GetWithItemsAsync(Guid id, CancellationToken ct = default)
        => Set.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<int> CountForYearAsync(int year, CancellationToken ct = default)
        => Set.IgnoreQueryFilters().CountAsync(p => p.CreatedDateUtc.Year == year, ct);
}

public sealed class GalleryRepository(DeChimexDbContext context) : RepositoryBase<GalleryMedia>(context), IGalleryRepository
{
    public Task<GalleryMedia?> GetWithImagesAsync(Guid id, CancellationToken ct = default)
        => Set.Include(m => m.Images).FirstOrDefaultAsync(m => m.Id == id, ct);
}

public sealed class ShoppingCartRepository(DeChimexDbContext context) : RepositoryBase<ShoppingCart>(context), IShoppingCartRepository
{
    public Task<ShoppingCart?> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
        => Set.Include(c => c.Items).FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);

    // A new CartItem (client Guid key) added to a tracked cart is misdetected as Modified and would
    // UPDATE a row that was never inserted. Unlike inventory transactions, CartItems ARE edited in
    // place (quantity), so only flip to Added those with no database row (genuinely new).
    public override void Update(ShoppingCart aggregate)
    {
        base.Update(aggregate);
        Context.ChangeTracker.DetectChanges();
        foreach (var e in Context.ChangeTracker.Entries<CartItem>())
            if (e.State == EntityState.Modified && e.GetDatabaseValues() is null)
                e.State = EntityState.Added;
    }
}

public sealed class WishlistRepository(DeChimexDbContext context) : RepositoryBase<WishlistItem>(context), IWishlistRepository
{
    // The unique customer/product key includes soft-deleted rows. Reuse a removed
    // membership instead of inserting a second row that would violate that key.
    public override async Task AddAsync(WishlistItem item, CancellationToken ct = default)
    {
        var previous = await Set.IgnoreQueryFilters().FirstOrDefaultAsync(
            w => w.CustomerId == item.CustomerId && w.ProductId == item.ProductId, ct);
        if (previous is null) { await base.AddAsync(item, ct); return; }
        if (!previous.IsDeleted) return;
        var entry = Context.Entry(previous);
        entry.Property(w => w.IsDeleted).CurrentValue = false;
        entry.Property(w => w.DeletedDateUtc).CurrentValue = null;
        entry.Property(w => w.DeletedBy).CurrentValue = null;
    }

    public async Task<IReadOnlyList<WishlistItem>> GetForCustomerAsync(Guid customerId, CancellationToken ct = default)
        => await Set.Where(w => w.CustomerId == customerId).ToListAsync(ct);

    public Task<WishlistItem?> FindAsync(Guid customerId, Guid productId, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(w => w.CustomerId == customerId && w.ProductId == productId, ct);
}

public sealed class ReviewRepository(DeChimexDbContext context) : RepositoryBase<Review>(context), IReviewRepository
{
    public async Task<IReadOnlyList<Review>> GetForProductAsync(Guid productId, bool approvedOnly, CancellationToken ct = default)
        => await Set.Where(r => r.ProductId == productId && (!approvedOnly || r.IsApproved)).ToListAsync(ct);
}

public sealed class NotificationRepository(DeChimexDbContext context) : RepositoryBase<Notification>(context), INotificationRepository
{
}

public sealed class ApplicationSettingRepository(DeChimexDbContext context) : RepositoryBase<ApplicationSetting>(context), IApplicationSettingRepository
{
    public Task<ApplicationSetting?> GetByKeyAsync(string key, CancellationToken ct = default)
        => Set.FirstOrDefaultAsync(s => s.Key == key, ct);
}

// AuditLogWriter was removed (2026-09-05) together with IAuditLogWriter: it was dead
// code (no caller anywhere in src/ or tests/) and its own SaveChangesAsync would have
// broken the atomicity that AuditableSoftDeleteInterceptor now guarantees for audit rows.

public sealed class ReceiptRepository(DeChimexDbContext context) : RepositoryBase<Receipt>(context), IReceiptRepository
{
    public Task<Receipt?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => Set.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == orderId, ct);

    public Task<int> CountForYearAsync(int year, CancellationToken ct = default)
        => Set.IgnoreQueryFilters().CountAsync(r => r.PaidAtUtc.Year == year, ct);
}

public sealed class ContactMessageRepository(DeChimexDbContext context) : RepositoryBase<ContactMessage>(context), IContactMessageRepository { }

public sealed class MarketingRepository(DeChimexDbContext context) : RepositoryBase<MarketingBroadcast>(context), IMarketingRepository
{
    public Task<MarketingBroadcast?> GetWithRecipientsAsync(Guid id, CancellationToken ct = default)
        => Set.Include(b => b.Recipients).FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<(Guid CustomerId, string Email, string FirstName)>> GetEligibleRecipientsAsync(CancellationToken ct = default)
    {
        // Registered (non-guest) customers with an email who haven't unsubscribed.
        // Staff/admin users are NEVER customers (separate aggregates), so they cannot
        // appear here; guests and unsubscribed customers are excluded by design.
        var rows = await context.Customers
            .AsNoTracking()
            .Where(c => !c.IsGuest && c.Email != null && c.MarketingSubscribed)
            .Select(c => new { c.Id, Email = c.Email!.Value, c.FirstName })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Email, r.FirstName)).ToList();
    }

    public async Task<IReadOnlyList<MarketingBroadcastRecipient>> GetPendingRecipientsAsync(Guid broadcastId, int take, CancellationToken ct = default)
        => await context.MarketingBroadcastRecipients
            .Where(r => r.BroadcastId == broadcastId && r.Status == MarketingRecipientStatus.Pending)
            .OrderBy(r => r.Id)
            .Take(take)
            .ToListAsync(ct);
}
