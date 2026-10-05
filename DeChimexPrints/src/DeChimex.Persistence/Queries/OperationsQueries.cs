using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Cart;
using DeChimex.Contracts.Customers;
using DeChimex.Contracts.Gallery;
using DeChimex.Contracts.Inventory;
using DeChimex.Contracts.Notifications;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Payments;
using DeChimex.Contracts.Procurement;
using DeChimex.Contracts.Wishlist;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

public sealed class InventoryQueries(DeChimexDbContext db) : IInventoryQueries
{
    private static InventoryDto Map(Domain.Entities.Inventory i) => new(
        i.Id, i.ProductId, i.ProductName, i.QuantityOnHand, i.QuantityReserved,
        i.QuantityOnHand - i.QuantityReserved, i.LowStockThreshold,
        (i.QuantityOnHand - i.QuantityReserved) <= i.LowStockThreshold,
        (i.QuantityOnHand - i.QuantityReserved) <= 0);

    public async Task<PagedResult<InventoryDto>> SearchAsync(PagedRequest request, CancellationToken ct)
    {
        var q = db.Inventories.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
            q = q.Where(i => EF.Functions.Like(i.ProductName, $"%{request.Search.Trim()}%"));
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(i => i.ProductName)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync(ct);
        return new PagedResult<InventoryDto>(items.Select(Map).ToList(), total, request.Page, request.PageSize);
    }

    public async Task<InventoryDto?> GetByProductIdAsync(Guid productId, CancellationToken ct)
    {
        var i = await db.Inventories.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == productId, ct);
        return i is null ? null : Map(i);
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> GetHistoryAsync(Guid productId, CancellationToken ct)
        => await db.InventoryTransactions.AsNoTracking().Where(t => t.ProductId == productId)
            .OrderByDescending(t => t.OccurredOnUtc)
            .Select(t => new InventoryTransactionDto(t.Id, t.Type.ToString(), t.QuantityChange, t.ResultingQuantity,
                t.Reference, t.Notes, t.PerformedBy, t.OccurredOnUtc)).ToListAsync(ct);

    public async Task<IReadOnlyList<InventoryDto>> GetLowStockAsync(CancellationToken ct)
    {
        var items = await db.Inventories.AsNoTracking()
            .Where(i => i.QuantityOnHand - i.QuantityReserved <= i.LowStockThreshold).ToListAsync(ct);
        return items.Select(Map).ToList();
    }
}

public sealed class CustomerQueries(DeChimexDbContext db) : ICustomerQueries
{
    public async Task<PagedResult<CustomerDto>> SearchAsync(PagedRequest request, CancellationToken ct)
    {
        var q = db.Customers.AsNoTracking().Include(c => c.Addresses).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim();
            q = q.Where(c => EF.Functions.Like(c.FirstName, $"%{s}%")
                          || EF.Functions.Like(c.LastName, $"%{s}%")
                          || (c.Email != null && EF.Functions.Like(c.Email.Value, $"%{s}%"))
                          || (c.Phone != null && EF.Functions.Like(c.Phone.Value, $"%{s}%")));
        }
        var total = await q.CountAsync(ct);
        var list = await q.OrderBy(c => c.FirstName)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync(ct);
        return new PagedResult<CustomerDto>(list.Select(MapCustomer).ToList(), total, request.Page, request.PageSize);
    }

    public async Task<CustomerDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().Include(x => x.Addresses).FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? null : MapCustomer(c);
    }

    private static CustomerDto MapCustomer(Domain.Entities.Customer c) => new(
        c.Id, c.FirstName, c.LastName, c.FullName, c.Email?.Value, c.IsGuest, c.Phone?.Value, c.Notes, c.LifetimeSpending.Amount,
        c.CreatedDateUtc,
        c.Addresses.Select(a => new AddressDto(a.Id, a.Label, a.Address.Street, a.Address.City, a.Address.State,
            a.Address.Country, a.Address.PostalCode, a.IsDefault)).ToList());
}

public sealed class OrderQueries(DeChimexDbContext db) : IOrderQueries
{
    public async Task<PagedResult<OrderSummaryDto>> SearchAsync(OrderFilter filter, CancellationToken ct)
    {
        // AUD-007: the Type predicate (derived from the lines present, mirroring Order.Type)
        // is evaluated IN SQL so totals and page sizes stay correct under a type filter, and
        // list rows project straight to the DTO instead of loading full Items/Services graphs.
        var q = db.Orders.AsNoTracking().Include(o => o.Customer).AsQueryable();

        if (filter.CustomerId is not null) q = q.Where(o => o.CustomerId == filter.CustomerId);
        if (!string.IsNullOrWhiteSpace(filter.Status) && Enum.TryParse<OrderStatus>(filter.Status, true, out var st))
            q = q.Where(o => o.Status == st);
        if (filter.FromUtc is not null) q = q.Where(o => o.CreatedDateUtc >= filter.FromUtc);
        if (filter.ToUtc is not null) q = q.Where(o => o.CreatedDateUtc <= filter.ToUtc);
        if (!string.IsNullOrWhiteSpace(filter.Search))
            q = q.Where(o => EF.Functions.Like(o.OrderNumber, $"%{filter.Search.Trim()}%"));

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            q = filter.Type.Trim().ToLowerInvariant() switch
            {
                "inventory" => q.Where(o => o.Items.Any() && !o.Services.Any()),
                "service" => q.Where(o => o.Services.Any() && !o.Items.Any()),
                "mixed" => q.Where(o => o.Items.Any() && o.Services.Any()),
                _ => q
            };
        }

        var total = await q.CountAsync(ct);
        // Server-side projection: only the columns the list shows (plus the totals
        // re-expressed from their line items, mirroring the Order aggregate's formulas —
        // including custom lines, which the previous Include-based list never loaded and
        // so silently under-reported on finalized custom orders).
        var rows = await q.OrderByDescending(o => o.CreatedDateUtc)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(o => new
            {
                o.Id,
                o.OrderNumber,
                CustomerName = o.Customer == null ? "" : o.Customer.FullName,
                HasProducts = o.Items.Any(),
                HasServices = o.Services.Any(),
                o.Status,
                o.Kind,
                o.CreatedDateUtc,
                Currency = o.DiscountAmount.Currency, // owned money column; all order money shares the order currency
                GrandTotal = o.Items.Sum(i => i.UnitPrice.Amount * i.Quantity)
                           + o.Services.Sum(s => s.UnitPrice.Amount * s.Quantity)
                           + o.CustomLines.Sum(c => c.UnitPrice.Amount * c.Quantity)
                           + o.DeliveryFee.Amount + o.TaxAmount.Amount
                           - (o.DiscountAmount.Amount >= o.Items.Sum(i => i.UnitPrice.Amount * i.Quantity)
                                + o.Services.Sum(s => s.UnitPrice.Amount * s.Quantity)
                                + o.CustomLines.Sum(c => c.UnitPrice.Amount * c.Quantity)
                                + o.DeliveryFee.Amount + o.TaxAmount.Amount
                            ? o.Items.Sum(i => i.UnitPrice.Amount * i.Quantity)
                                + o.Services.Sum(s => s.UnitPrice.Amount * s.Quantity)
                                + o.CustomLines.Sum(c => c.UnitPrice.Amount * c.Quantity)
                                + o.DeliveryFee.Amount + o.TaxAmount.Amount
                            : o.DiscountAmount.Amount)
            })
            .ToListAsync(ct);

        var list = rows.Select(r => new OrderSummaryDto(r.Id, r.OrderNumber, r.CustomerName,
            (r.HasProducts && r.HasServices ? OrderType.Mixed
                : r.HasServices ? OrderType.Service : OrderType.Inventory).ToString(),
            r.Status.ToString(), r.GrandTotal, r.Currency, r.CreatedDateUtc, r.Kind.ToString())).ToList();

        return new PagedResult<OrderSummaryDto>(list, total, filter.Page, filter.PageSize);
    }

    public async Task<OrderDto?> GetByIdAsync(Guid id, CancellationToken ct)
        => MapFull(await db.Orders.AsNoTracking().Include(o => o.Customer)
            .Include(o => o.Items).Include(o => o.Services).FirstOrDefaultAsync(o => o.Id == id, ct));

    public async Task<OrderDto?> GetByNumberAsync(string orderNumber, CancellationToken ct)
        => MapFull(await db.Orders.AsNoTracking().Include(o => o.Customer)
            .Include(o => o.Items).Include(o => o.Services).FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, ct));

    public async Task<IReadOnlyList<OrderSummaryDto>> GetForCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var list = await db.Orders.AsNoTracking().Include(o => o.Customer)
            .Include(o => o.Items).Include(o => o.Services)
            .Where(o => o.CustomerId == customerId).OrderByDescending(o => o.CreatedDateUtc).ToListAsync(ct);
        return list.Select(o => new OrderSummaryDto(o.Id, o.OrderNumber,
            o.Customer == null ? "" : o.Customer.FullName, o.Type.ToString(), o.Status.ToString(),
            o.GrandTotal.Amount, o.GrandTotal.Currency, o.CreatedDateUtc, o.Kind.ToString())).ToList();
    }

    public Task<bool> HasQualifyingPurchaseAsync(Guid customerId, Guid productId, CancellationToken ct)
    {
        // Review purchase-verification: a paid-or-later order containing the product.
        // Unpaid/pending, cancelled and refunded orders do not qualify as a purchase.
        var qualifying = new[]
        {
            Domain.Enums.OrderStatus.Paid, Domain.Enums.OrderStatus.AwaitingProcurement,
            Domain.Enums.OrderStatus.InProduction, Domain.Enums.OrderStatus.QualityCheck,
            Domain.Enums.OrderStatus.Ready, Domain.Enums.OrderStatus.OutForDelivery,
            Domain.Enums.OrderStatus.Completed, Domain.Enums.OrderStatus.Delivered
        };
        return db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == customerId && qualifying.Contains(o.Status))
            .AnyAsync(o => o.Items.Any(i => i.ProductId == productId), ct);
    }

    private static OrderDto? MapFull(Domain.Entities.Order? o)
    {
        if (o is null) return null;
        return new OrderDto(o.Id, o.OrderNumber, o.CustomerId, o.Customer?.FullName ?? "",
            o.Type.ToString(), o.Status.ToString(), o.ItemsSubtotal.Amount, o.DiscountAmount.Amount,
            o.DeliveryFee.Amount, o.TaxAmount.Amount, o.GrandTotal.Amount, o.GrandTotal.Currency,
            o.RequiresProcurement, o.PaidAtUtc, o.CompletedAtUtc, o.CreatedDateUtc,
            o.Items.Select(i => new OrderItemDto(i.Id, i.ProductId, i.ProductName, i.Sku, i.UnitPrice.Amount, i.Quantity, i.LineTotal.Amount)).ToList(),
            o.Services.Select(s => new OrderServiceDto(s.Id, s.ServiceId, s.ServiceName, s.UnitPrice.Amount, s.Quantity, s.Notes, s.LineTotal.Amount)).ToList(),
            o.Kind.ToString());
    }
}

public sealed class PaymentQueries(DeChimexDbContext db) : IPaymentQueries
{
    public async Task<IReadOnlyList<PaymentDto>> GetForCustomerAsync(Guid customerId, CancellationToken ct)
        => await db.Payments.AsNoTracking().Where(p => p.CustomerId == customerId)
            .OrderByDescending(p => p.CreatedDateUtc)
            .Select(p => new PaymentDto(p.Id, p.OrderId, p.Amount.Amount, p.Amount.Currency, p.Method.ToString(),
                p.Status.ToString(), p.Reference, p.GatewayReference, p.PaidAtUtc, p.CreatedDateUtc, p.FailureReason,
                p.OrderAmount, p.ServiceFeePercent, p.CustomerBorneCharge, p.PaystackFeeExpected, p.PaystackFeeActual)).ToListAsync(ct);

    public async Task<InvoiceDto?> GetInvoiceByOrderIdAsync(Guid orderId, CancellationToken ct)
        => await db.Invoices.AsNoTracking().Where(i => i.OrderId == orderId)
            .Select(i => new InvoiceDto(i.Id, i.InvoiceNumber, i.OrderId, i.Subtotal.Amount, i.Discount.Amount,
                i.DeliveryFee.Amount, i.Tax.Amount, i.GrandTotal.Amount, i.GrandTotal.Currency, i.Status.ToString(),
                i.IssuedAtUtc, i.PaidAtUtc, i.DocumentUrl)).FirstOrDefaultAsync(ct);
}

public sealed class GalleryQueries(DeChimexDbContext db) : IGalleryQueries
{
    public async Task<IReadOnlyList<GalleryMediaDto>> GetAllAsync(bool includeArchived, string? category, CancellationToken ct)
    {
        // Only post rows (ParentId null) are gallery entries; extra carousel images are
        // children and must never surface as their own grid tile / admin row.
        var q = db.GalleryMedia.AsNoTracking().Include(m => m.Images).Where(m => m.ParentId == null);
        if (!includeArchived) q = q.Where(m => !m.IsArchived);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(m => m.Category == category);
        var posts = await q.OrderBy(m => m.DisplayOrder).ToListAsync(ct);
        return posts.Select(m => new GalleryMediaDto(m.Id, m.Title, m.Url, m.ThumbnailUrl, m.Type.ToString(),
            m.Category, m.IsFeatured, m.IsArchived, m.DisplayOrder, MapMediaUrls(m))).ToList();
    }

    /// <summary>Ordered carousel media: the post's own media first, then its extra images.</summary>
    private static IReadOnlyList<string> MapMediaUrls(Domain.Entities.GalleryMedia m)
    {
        var urls = new List<string> { m.Url };
        urls.AddRange(m.Images.OrderBy(i => i.DisplayOrder).Select(i => i.Url));
        return urls;
    }
}

public sealed class ProcurementQueries(DeChimexDbContext db) : IProcurementQueries
{
    public async Task<PagedResult<ProcurementDto>> SearchAsync(PagedRequest request, CancellationToken ct)
    {
        var q = db.Procurements.AsNoTracking().Include(p => p.Items).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
            q = q.Where(p => EF.Functions.Like(p.Reference, $"%{request.Search.Trim()}%"));
        var total = await q.CountAsync(ct);
        var list = await q.OrderByDescending(p => p.CreatedDateUtc)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync(ct);
        return new PagedResult<ProcurementDto>(list.Select(Map).ToList(), total, request.Page, request.PageSize);
    }

    public async Task<ProcurementDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Procurements.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var dto = Map(p);
        // Surfaces show the business order number (DCX-…), never the raw linked id.
        var orderNumber = await db.Orders.AsNoTracking()
            .Where(o => o.Id == p.SourceOrderId)
            .Select(o => o.OrderNumber)
            .FirstOrDefaultAsync(ct);
        return dto with { SourceOrderNumber = orderNumber };
    }

    private static ProcurementDto Map(Domain.Entities.Procurement p) => new(
        p.Id, p.Reference, p.SourceOrderId, p.Status.ToString(), p.ApprovedBy, p.ApprovedAtUtc, p.ReceivedAtUtc,
        p.SupplierName, p.Notes,
        p.Items.Select(i => new ProcurementItemDto(i.Id, i.ProductId, i.ProductName, i.Quantity, i.UnitCost != null ? i.UnitCost.Amount : null, i.QuantityReceived)).ToList());
}

public sealed class CartQueries(DeChimexDbContext db) : ICartQueries
{
    public async Task<CartDto?> GetForCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var cart = await db.ShoppingCarts.AsNoTracking().Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);
        if (cart is null) return null;
        if (!cart.Items.Any()) return new CartDto(cart.Id, [], 0, "NGN", 0);

        // Batch fetch to avoid N+1 by using a single query per line type
        var productIds = cart.Items.Where(i => i.LineType == OrderLineType.Product).Select(i => i.ReferenceId).Distinct().ToList();
        var serviceIds = cart.Items.Where(i => i.LineType == OrderLineType.Service).Select(i => i.ReferenceId).Distinct().ToList();

        var productDict = new Dictionary<Guid, (string Name, decimal Amount, string Currency, string? ImageUrl)>();
        if (productIds.Count > 0)
        {
            var list = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, Amount = p.SellingPrice.Amount, Currency = p.SellingPrice.Currency,
                    ImageUrl = p.Images.Where(im => im.IsPrimary).Select(im => im.Url).FirstOrDefault() })
                .ToListAsync(ct);
            productDict = list.ToDictionary(x => x.Id, x => (x.Name, x.Amount, x.Currency, x.ImageUrl));
        }

        var serviceDict = new Dictionary<Guid, (string Name, decimal Amount, string Currency)>();
        if (serviceIds.Count > 0)
        {
            var list = await db.Services.AsNoTracking().Where(s => serviceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name, Amount = s.BasePrice.Amount, Currency = s.BasePrice.Currency })
                .ToListAsync(ct);
            serviceDict = list.ToDictionary(x => x.Id, x => (x.Name, x.Amount, x.Currency));
        }

        var items = new List<CartItemDto>();
        decimal subtotal = 0;
        foreach (var it in cart.Items)
        {
            string name; decimal price; string currency; string? imageUrl = null;
            if (it.LineType == OrderLineType.Product)
            {
                if (productDict.TryGetValue(it.ReferenceId, out var p))
                {
                    name = p.Name; price = p.Amount; currency = p.Currency; imageUrl = p.ImageUrl;
                }
                else { name = "Product"; price = 0; currency = "NGN"; }
            }
            else
            {
                if (serviceDict.TryGetValue(it.ReferenceId, out var s))
                {
                    name = s.Name; price = s.Amount; currency = s.Currency;
                }
                else { name = "Service"; price = 0; currency = "NGN"; }
            }
            var lineTotal = price * it.Quantity;
            subtotal += lineTotal;
            items.Add(new CartItemDto(it.Id, it.LineType.ToString(), it.ReferenceId, name, price, currency, it.Quantity, lineTotal, it.Notes, imageUrl));
        }
        return new CartDto(cart.Id, items, subtotal, "NGN", items.Sum(i => i.Quantity));
    }
}

public sealed class WishlistQueries(DeChimexDbContext db) : IWishlistQueries
{
    public async Task<IReadOnlyList<WishlistItemDto>> GetForCustomerAsync(Guid customerId, CancellationToken ct)
        => await db.WishlistItems.AsNoTracking().Where(w => w.CustomerId == customerId)
            .Join(db.Products, w => w.ProductId, p => p.Id, (w, p) => new WishlistItemDto(
                w.Id, p.Id, p.Name, p.SellingPrice.Amount, p.SellingPrice.Currency,
                p.Images.Where(im => im.IsPrimary).Select(im => im.Url).FirstOrDefault())).ToListAsync(ct);
}

public sealed class NotificationQueries(DeChimexDbContext db) : INotificationQueries
{
    public async Task<IReadOnlyList<NotificationDto>> GetForUserAsync(Guid userId, bool unreadOnly, IReadOnlyCollection<string> userPermissions, bool isStaff, CancellationToken ct)
    {
        // Rows with a recipient are visible to that user only. Rows without one are the
        // STAFF audience: visible only to business staff (isStaff), further scoped by
        // RequiredPermission; null-permission rows are legacy all-staff broadcasts.
        // Customers/developers never see staff rows (they may hold permissions such as
        // orders.create, so a permission check alone is not a staff check).
        return await db.Notifications.AsNoTracking()
            .Where(n => (n.RecipientUserId == userId || (isStaff && n.RecipientUserId == null && (n.RequiredPermission == null || userPermissions.Contains(n.RequiredPermission))))
                        && (!unreadOnly || !n.IsRead))
            .OrderByDescending(n => n.CreatedDateUtc)
            .Select(n => new NotificationDto(n.Id, n.Type.ToString(), n.Title, n.Message, n.Link, n.IsRead, n.CreatedDateUtc))
            .ToListAsync(ct);
    }

    public async Task<int> CountUnreadAsync(Guid userId, IReadOnlyCollection<string> userPermissions, bool isStaff, CancellationToken ct)
        => await db.Notifications.AsNoTracking()
            .Where(n => (n.RecipientUserId == userId || (isStaff && n.RecipientUserId == null && (n.RequiredPermission == null || userPermissions.Contains(n.RequiredPermission))))
                        && !n.IsRead)
            .CountAsync(ct);
}
