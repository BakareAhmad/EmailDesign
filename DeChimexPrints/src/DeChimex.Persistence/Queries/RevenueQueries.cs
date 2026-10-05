using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

/// <summary>
/// The shared revenue read-side (AUD-001/016). Both predicates live here exactly once:
///  - paid-like status via <see cref="OrderStatusExtensions.IsPaidLike"/> (an explicit
///    whitelist — a numeric <c>Status &gt;= Paid</c> range would count the additive
///    custom-order states 10-15, which are UNPAID, as revenue);
///  - the CASH basis: the order's money counts in the period it was COLLECTED
///    (<c>PaidAtUtc</c>), so Dashboard, Analytics and Reports reconcile to the kobo the
///    same way the service-fee ledger already does.
/// </summary>
public sealed class RevenueQueries(DeChimexDbContext db) : IRevenueQueries
{
    private static readonly OrderStatus[] PaidLikeStatuses =
    [
        OrderStatus.Paid, OrderStatus.AwaitingProcurement, OrderStatus.InProduction,
        OrderStatus.QualityCheck, OrderStatus.Ready, OrderStatus.OutForDelivery,
        OrderStatus.Completed, OrderStatus.Delivered
    ];

    private IQueryable<Domain.Entities.Order> PaidOrders(DateTime from, DateTime to)
        => db.Orders.AsNoTracking()
            .Where(o => o.PaidAtUtc != null && o.PaidAtUtc >= from && o.PaidAtUtc <= to)
            .Where(o => PaidLikeStatuses.Contains(o.Status));

    public async Task<IReadOnlyList<RevenueOrderRow>> GetPaidOrdersAsync(DateTime from, DateTime to, CancellationToken ct = default)
        => await PaidOrders(from, to)
            .Select(o => new RevenueOrderRow(o.Id, o.OrderNumber, o.Status, o.PaidAtUtc!.Value,
                o.Items.Sum(i => i.UnitPrice.Amount * i.Quantity)
                    + o.Services.Sum(s => s.UnitPrice.Amount * s.Quantity)
                    + o.CustomLines.Sum(c => c.UnitPrice.Amount * c.Quantity)
                    + o.DeliveryFee.Amount + o.TaxAmount.Amount - o.DiscountAmount.Amount,
                o.DiscountAmount.Currency))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetPaidOrderIdsAsync(DateTime from, DateTime to, CancellationToken ct = default)
        => await PaidOrders(from, to).Select(o => o.Id).ToListAsync(ct);
}
