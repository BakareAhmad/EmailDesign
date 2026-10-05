using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Application.Features.Users;
using DeChimex.Contracts.Auth;
using DeChimex.Contracts.Dashboard;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

public sealed class DashboardQueries(DeChimexDbContext db, IRevenueQueries revenue) : IDashboardQueries
{
    private const string Currency = "NGN";

    /// <summary>
    /// SQL-translatable expression that reconstructs an Order's *raw* total from persisted
    /// columns. The domain's <c>Order.GrandTotal</c> is a computed property (mapped with
    /// <c>b.Ignore(x =&gt; x.GrandTotal)</c>), so EF cannot translate it inside a query. It must
    /// be rebuilt from the underlying data:
    ///   raw = sum(item UnitPrice*Qty) + sum(service UnitPrice*Qty) + DeliveryFee + TaxAmount - DiscountAmount
    /// The domain floors this at 0 (discount can never exceed the total). We do NOT floor here,
    /// because <c>Math.Max</c> does not translate on SQL Server; the floor is applied in memory
    /// after materialisation (see call sites). This is a harmless safety net for paid orders.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<Domain.Entities.Order, decimal>> OrderRawTotal =
        o => o.Items.Sum(i => i.UnitPrice.Amount * i.Quantity)
                + o.Services.Sum(s => s.UnitPrice.Amount * s.Quantity)
                + o.DeliveryFee.Amount
                + o.TaxAmount.Amount
                - o.DiscountAmount.Amount;

    public async Task<DashboardKpiDto> GetKpisAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // AUD-001/016: revenue comes from the ONE shared cash-basis paid-like query
        // (IRevenueQueries), so the Dashboard can never disagree with Analytics/Reports.
        var now1 = now;
        var monthlyRevenue = await revenue.GetPaidOrdersAsync(monthStart, now1, ct);
        var revenueThisMonth = monthlyRevenue.Sum(r => Math.Max(0m, r.Amount));

        var totalOrders = await db.Orders.AsNoTracking().CountAsync(ct);
        var pendingOrders = await db.Orders.AsNoTracking()
            .CountAsync(o => o.Status == OrderStatus.PendingPayment || o.Status == OrderStatus.Paid || o.Status == OrderStatus.InProduction, ct);
        var completedOrders = await db.Orders.AsNoTracking()
            .CountAsync(o => o.Status == OrderStatus.Completed || o.Status == OrderStatus.Delivered, ct);
        var awaitingProcurement = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.AwaitingProcurement, ct);

        // OWNER DIRECTIVE 2026-09-08 ("confirm if inventory value isn't affected upon a
        // successful order and fix"): a paid order RESERVES stock (Available drops) but
        // on-hand only deducts later when production starts, so valuing raw OnHand left
        // the KPI untouched by successful orders. Value AVAILABLE stock (OnHand minus
        // Reserved) - the same definition every other surface uses - so the inventory
        // value drops the moment an order succeeds; the later fulfilment StockOut moves
        // Reserved to OnHand-deducted, leaving Available (and the value) unchanged.
        var inventoryValue = await db.Inventories.AsNoTracking()
            .Join(db.Products, i => i.ProductId, p => p.Id, (i, p) => new { i.QuantityOnHand, i.QuantityReserved, p.SellingPrice.Amount })
            .SumAsync(x => (decimal?)((x.QuantityOnHand - x.QuantityReserved) * x.Amount), ct) ?? 0m;

        var lowStock = await db.Inventories.AsNoTracking()
            .CountAsync(i => i.QuantityOnHand - i.QuantityReserved <= i.LowStockThreshold, ct);

        return new DashboardKpiDto(revenueThisMonth, totalOrders, pendingOrders, completedOrders,
            inventoryValue, lowStock, awaitingProcurement, Currency);
    }

    public async Task<IReadOnlyList<RevenuePointDto>> GetRevenueTrendAsync(int months, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(months - 1));

        // AUD-016: same shared cash-basis query as every other revenue surface.
        var paid = await revenue.GetPaidOrdersAsync(start, DateTime.UtcNow, ct);

        var result = new List<RevenuePointDto>();
        for (var i = 0; i < months; i++)
        {
            var m = start.AddMonths(i);
            var value = paid.Where(x => x.PaidAtUtc.Year == m.Year && x.PaidAtUtc.Month == m.Month)
                .Sum(x => Math.Max(0m, x.Amount));
            result.Add(new RevenuePointDto(m.ToString("MMM"), value));
        }
        return result;
    }

    public async Task<IReadOnlyList<CategorySalesDto>> GetSalesByCategoryAsync(CancellationToken ct)
    {
        var data = await db.OrderItems.AsNoTracking()
            .Join(db.Products, oi => oi.ProductId, p => p.Id, (oi, p) => new { oi, p.CategoryId })
            .Join(db.Categories, x => x.CategoryId, c => c.Id, (x, c) => new { c.Name, Total = x.oi.UnitPrice.Amount * x.oi.Quantity })
            .GroupBy(x => x.Name)
            .Select(g => new { Category = g.Key, Amount = g.Sum(x => x.Total) })
            .ToListAsync(ct);

        var grand = data.Sum(x => x.Amount);
        return data.OrderByDescending(x => x.Amount)
            .Select(x => new CategorySalesDto(x.Category, x.Amount, grand == 0 ? 0 : Math.Round(x.Amount / grand * 100, 1)))
            .ToList();
    }
}

public sealed class UserQueries(DeChimexDbContext db) : IUserQueries
{
    public async Task<PagedResult<UserDto>> SearchAsync(PagedRequest request, CancellationToken ct)
    {
        var q = db.Users.AsNoTracking().Include(u => u.Roles).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim();
            q = q.Where(u => EF.Functions.Like(u.FirstName, $"%{s}%")
                          || EF.Functions.Like(u.LastName, $"%{s}%")
                          || EF.Functions.Like(u.Email.Value, $"%{s}%"));
        }
        var total = await q.CountAsync(ct);
        var users = await q.OrderBy(u => u.FirstName)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync(ct);

        var roleLookup = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var dtos = users.Select(u => new UserDto(u.Id, u.Email.Value, u.FullName, u.Username == null ? null : u.Username.Value, u.IsActive, u.IsEmailVerified,
            u.LastLoginUtc, u.Roles.Select(r => roleLookup.TryGetValue(r.RoleId, out var n) ? n : "").Where(n => n != "").ToList())).ToList();
        return new PagedResult<UserDto>(dtos, total, request.Page, request.PageSize);
    }

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().Include(x => x.Roles).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u is null) return null;
        var roleLookup = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        return new UserDto(u.Id, u.Email.Value, u.FullName, u.Username == null ? null : u.Username.Value, u.IsActive, u.IsEmailVerified, u.LastLoginUtc,
            u.Roles.Select(r => roleLookup.TryGetValue(r.RoleId, out var n) ? n : "").Where(n => n != "").ToList());
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).ThenInclude(rp => rp.Permission).ToListAsync(ct);

        // Get user counts per role
        var userCounts = await db.UserRoles.AsNoTracking()
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, ct);

        return roles.Select(r => new RoleDto(r.Id, r.Name, r.Description, r.IsSystemRole,
            r.Permissions.Select(p => p.Permission!.Name).ToList(),
            userCounts.GetValueOrDefault(r.Id, 0))).ToList();
    }

    public async Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken ct)
        => await db.Permissions.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new PermissionDto(p.Id, p.Name, p.Description)).ToListAsync(ct);
}