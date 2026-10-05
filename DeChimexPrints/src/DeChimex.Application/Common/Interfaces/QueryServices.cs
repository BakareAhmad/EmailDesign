using DeChimex.Application.Common.Models;
using DeChimex.Domain.Enums;
using DeChimex.Contracts.Cart;
using DeChimex.Contracts.Contacts;
using DeChimex.Contracts.Customers;
using DeChimex.Contracts.Dashboard;
using DeChimex.Contracts.Gallery;
using DeChimex.Contracts.Inventory;
using DeChimex.Contracts.Notifications;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Payments;
using DeChimex.Contracts.Procurement;
using DeChimex.Contracts.Products;
using DeChimex.Contracts.Services;
using DeChimex.Contracts.Wishlist;

namespace DeChimex.Application.Common.Interfaces;

/// <summary>
/// CQRS read side. These interfaces are implemented in Persistence using efficient
/// EF Core projections (no aggregate hydration), keeping queries fast and decoupled.
/// </summary>
public interface IProductQueries
{
    Task<PagedResult<ProductSummaryDto>> SearchAsync(ProductFilter filter, CancellationToken ct);
    Task<ProductDto?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ProductSummaryDto>> GetFeaturedAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<string>> GetSkusAsync(CancellationToken ct);
}

public sealed class ProductFilter : PagedRequest
{
    public Guid? MajorCategoryId { get; set; }
    public Guid? CategoryId { get; set; }
    public bool? IsFeatured { get; set; }
    public bool ActiveOnly { get; set; } = true;
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
}

public interface IServiceQueries
{
    Task<IReadOnlyList<ServiceDto>> GetAllAsync(bool activeOnly, CancellationToken ct);
    Task<ServiceDto?> GetByIdAsync(Guid id, CancellationToken ct);
}

/// <summary>Read side for a category's ordering options (active only, ordered).</summary>
public interface ICategoryOptionsQueries
{
    Task<IReadOnlyList<CategoryOptionDto>> GetForCategoryAsync(Guid categoryId, CancellationToken ct);
    Task<bool> CategoryHasActiveOptionsAsync(Guid categoryId, CancellationToken ct);
}

/// <summary>Read side for the order workspace (order + responses/files/chat/timeline/proofs/lines).</summary>
public interface IOrderWorkspaceQueries
{
    Task<OrderWorkspaceDto?> GetAsync(Guid orderId, CancellationToken ct);
}

/// <summary>Filter for the sales inbox/work queue.</summary>
public sealed class SalesInboxFilter : PagedRequest
{
    /// <summary>Scope to one sales staff member; null = every assignment (owner/manager view).</summary>
    public Guid? StaffUserId { get; set; }

    /// <summary>new | active | waiting-customer | waiting-staff | resolved | unassigned.</summary>
    public string Bucket { get; set; } = "new";
}

/// <summary>Read side for the sales-assignment workflow (inbox, bucket counts, eligible staff).</summary>
public interface ISalesInboxQueries
{
    Task<PagedResult<SalesInboxItemDto>> GetInboxAsync(SalesInboxFilter filter, CancellationToken ct);
    Task<SalesBucketCounts> GetBucketCountsAsync(Guid? staffUserId, CancellationToken ct);
    Task<IReadOnlyList<SalesStaffOptionDto>> GetEligibleSalesStaffAsync(CancellationToken ct);
}

public interface IInventoryQueries
{
    Task<PagedResult<InventoryDto>> SearchAsync(PagedRequest request, CancellationToken ct);
    Task<InventoryDto?> GetByProductIdAsync(Guid productId, CancellationToken ct);
    Task<IReadOnlyList<InventoryTransactionDto>> GetHistoryAsync(Guid productId, CancellationToken ct);
    Task<IReadOnlyList<InventoryDto>> GetLowStockAsync(CancellationToken ct);
}

public interface ICustomerQueries
{
    Task<PagedResult<CustomerDto>> SearchAsync(PagedRequest request, CancellationToken ct);
    Task<CustomerDto?> GetByIdAsync(Guid id, CancellationToken ct);
}

public interface IOrderQueries
{
    Task<PagedResult<OrderSummaryDto>> SearchAsync(OrderFilter filter, CancellationToken ct);
    Task<OrderDto?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<OrderDto?> GetByNumberAsync(string orderNumber, CancellationToken ct);
    Task<IReadOnlyList<OrderSummaryDto>> GetForCustomerAsync(Guid customerId, CancellationToken ct);

    /// <summary>
    /// Review purchase-verification: true when the customer has an order containing
    /// this product in a paid-or-later state (Paid..Delivered). Unpaid, cancelled and
    /// refunded orders do not qualify as a purchase (DIRECT_CHECKOUT_EVALUATION.md §8).
    /// </summary>
    Task<bool> HasQualifyingPurchaseAsync(Guid customerId, Guid productId, CancellationToken ct);
}

public interface IInvoiceQueries
{
    Task<DeChimex.Contracts.Invoices.InvoiceDocumentDto?> GetDocumentByIdAsync(Guid id, CancellationToken ct);
    Task<DeChimex.Contracts.Invoices.InvoiceDocumentDto?> GetDocumentByOrderIdAsync(Guid orderId, CancellationToken ct);
    Task<DeChimex.Contracts.Invoices.InvoiceDocumentDto?> GetDocumentByShareTokenAsync(string shareToken, CancellationToken ct);
    Task<DeChimex.Contracts.Invoices.ReceiptDocumentDto?> GetReceiptByOrderIdAsync(Guid orderId, CancellationToken ct);
    Task<DeChimex.Contracts.Invoices.ReceiptDocumentDto?> GetReceiptByIdAsync(Guid id, CancellationToken ct);
    Task<DeChimex.Contracts.Invoices.ReceiptDocumentDto?> GetReceiptByShareTokenAsync(string shareToken, CancellationToken ct);
}

public sealed class OrderFilter : PagedRequest
{
    public string? Status { get; set; }
    public string? Type { get; set; }
    public Guid? CustomerId { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
}

public interface IPaymentQueries
{
    Task<IReadOnlyList<PaymentDto>> GetForCustomerAsync(Guid customerId, CancellationToken ct);
    Task<InvoiceDto?> GetInvoiceByOrderIdAsync(Guid orderId, CancellationToken ct);
}

public interface IGalleryQueries
{
    Task<IReadOnlyList<GalleryMediaDto>> GetAllAsync(bool includeArchived, string? category, CancellationToken ct);
}

public interface IProcurementQueries
{
    Task<PagedResult<ProcurementDto>> SearchAsync(PagedRequest request, CancellationToken ct);
    Task<ProcurementDto?> GetByIdAsync(Guid id, CancellationToken ct);
}

public interface ICartQueries
{
    Task<CartDto?> GetForCustomerAsync(Guid customerId, CancellationToken ct);
}

public interface IWishlistQueries
{
    Task<IReadOnlyList<WishlistItemDto>> GetForCustomerAsync(Guid customerId, CancellationToken ct);
}

public interface INotificationQueries
{
    /// <param name="userPermissions">The requesting user's permission claims; a notification with
    /// a non-null RequiredPermission is only returned when the user holds that permission.</param>
    Task<IReadOnlyList<NotificationDto>> GetForUserAsync(Guid userId, bool unreadOnly, IReadOnlyCollection<string> userPermissions, bool isStaff, CancellationToken ct);
    Task<int> CountUnreadAsync(Guid userId, IReadOnlyCollection<string> userPermissions, bool isStaff, CancellationToken ct);
}

public interface IDashboardQueries
{
    Task<DashboardKpiDto> GetKpisAsync(CancellationToken ct);
    Task<IReadOnlyList<RevenuePointDto>> GetRevenueTrendAsync(int months, CancellationToken ct);
    Task<IReadOnlyList<CategorySalesDto>> GetSalesByCategoryAsync(CancellationToken ct);
}

public interface IContactMessageQueries
{
    Task<PagedResult<ContactMessageDto>> SearchAsync(PagedRequest request, bool unresolvedOnly, CancellationToken ct);
    Task<ContactMessageDto?> GetAsync(Guid id, CancellationToken ct);
}

/// <summary>Read model for marketing broadcasts (admin).</summary>
public interface IMarketingQueries
{
    Task<Common.Models.PagedResult<Contracts.Marketing.MarketingBroadcastDto>> GetBroadcastsAsync(
        Common.Models.PagedRequest request, CancellationToken ct);
    Task<Contracts.Marketing.MarketingBroadcastDetailsDto?> GetDetailsAsync(Guid id, CancellationToken ct);
    Task<int> GetEligibleRecipientCountAsync(CancellationToken ct);
}

/// <summary>
/// The single definition of "revenue" shared by Dashboard, Analytics and Reports (AUD-001/016):
/// an order contributes only when its status is paid-like (OrderStatusExtensions.IsPaidLike —
/// NEVER a numeric range, which the additive custom-order statuses break) and it is summed on
/// the CASH basis (PaidAtUtc), so every surface reconciles to the same figures.
/// </summary>
public interface IRevenueQueries
{
    /// <summary>Paid orders whose money was collected in [from, to] on the cash basis.</summary>
    Task<IReadOnlyList<RevenueOrderRow>> GetPaidOrdersAsync(DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Ids of paid orders collected in [from, to] (cash basis) — for per-line joins.</summary>
    Task<IReadOnlyList<Guid>> GetPaidOrderIdsAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

/// <summary>One paid order projected for revenue reporting (cash basis).</summary>
public sealed record RevenueOrderRow(
    Guid Id, string OrderNumber, OrderStatus Status, DateTime PaidAtUtc, decimal Amount, string Currency);
