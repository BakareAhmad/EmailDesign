using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

/// <summary>
/// Read side for the sales-assignment workflow. Eligibility = active, non-deleted users
/// holding the sales role; active workload = assigned orders in the live review states
/// (mirrors <see cref="Order.IsActiveSalesWork"/> — keep the two in sync).
/// </summary>
public sealed class SalesInboxQueries(DeChimexDbContext db) : ISalesInboxQueries
{
    private static readonly OrderStatus[] ActiveSalesStatuses =
    [
        OrderStatus.AwaitingReview,
        OrderStatus.InDiscussion,
        OrderStatus.RequirementsConfirmed,
        OrderStatus.AwaitingDesignApproval
    ];

    public async Task<IReadOnlyList<SalesStaffOptionDto>> GetEligibleSalesStaffAsync(CancellationToken ct)
    {
        var salesRole = RoleNames.SalesStaff.ToUpperInvariant();
        var salesUserIds = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.Role != null && ur.Role.NormalizedName == salesRole)
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(ct);

        var workloads = await db.Orders.AsNoTracking()
            .Where(o => o.AssignedSalesUserId != null && ActiveSalesStatuses.Contains(o.Status))
            .GroupBy(o => o.AssignedSalesUserId!.Value)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.UserId, g => g.Count, ct);

        var staff = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && salesUserIds.Contains(u.Id))
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new { u.Id, u.FirstName, u.LastName, Email = u.Email.Value, u.CreatedDateUtc })
            .ToListAsync(ct);

        return staff
            .Select(u => new SalesStaffOptionDto(
                u.Id, $"{u.FirstName} {u.LastName}".Trim(), u.Email,
                workloads.TryGetValue(u.Id, out var count) ? count : 0,
                u.CreatedDateUtc))
            .ToList();
    }

    public async Task<PagedResult<SalesInboxItemDto>> GetInboxAsync(SalesInboxFilter filter, CancellationToken ct)
    {
        var q = ApplyBucket(db.Orders.AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Messages)
            .AsQueryable(), filter.Bucket, filter.StaffUserId);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(o => EF.Functions.Like(o.OrderNumber, $"%{s}%")
                || (o.Customer != null && (EF.Functions.Like(o.Customer.FirstName, $"%{s}%")
                    || EF.Functions.Like(o.Customer.LastName, $"%{s}%"))));
        }

        var total = await q.CountAsync(ct);
        q = filter.Bucket == "resolved"
            ? q.OrderByDescending(o => o.ModifiedDateUtc ?? o.CreatedDateUtc)
            : q.OrderByDescending(o => o.CreatedDateUtc);
        var list = await q.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToListAsync(ct);

        return new PagedResult<SalesInboxItemDto>(list.Select(o =>
        {
            var last = o.Messages.OrderByDescending(m => m.CreatedDateUtc).FirstOrDefault();
            var preview = last is null ? null
                : last.Body.Length > 120 ? last.Body[..120] + "…" : last.Body;
            return new SalesInboxItemDto(
                o.Id, o.OrderNumber, o.Customer?.FullName ?? "", o.Status.ToString(),
                o.ConversationState.ToString(), o.AssignedSalesName, o.CreatedDateUtc,
                o.Messages.Count, last?.CreatedDateUtc, preview);
        }).ToList(), total, filter.Page, filter.PageSize);
    }

    public async Task<SalesBucketCounts> GetBucketCountsAsync(Guid? staffUserId, CancellationToken ct)
    {
        // Same visibility rules as before: a staff view counts only their own orders; the
        // unassigned bucket stays assignment-agnostic for owner/manager triage.
        var q = db.Orders.AsNoTracking().AsQueryable();
        if (staffUserId is Guid sid)
            q = q.Where(o => o.AssignedSalesUserId == sid);

        // AUD-031: the buckets are computed by a GROUP BY aggregate in the database instead
        // of materialising one row per order ever created and counting in memory.
        var groups = await q
            .GroupBy(o => new { o.Status, o.ConversationState, Unassigned = o.AssignedSalesUserId == null })
            .Select(g => new { g.Key.Status, g.Key.ConversationState, g.Key.Unassigned, Count = g.Count() })
            .ToListAsync(ct);

        var rows = groups.Select(g => (g.Status, g.ConversationState, g.Unassigned, g.Count)).ToList();
        int Sum(Func<(OrderStatus Status, OrderConversationState State, bool Unassigned, int Count), bool> predicate)
            => rows.Where(predicate).Sum(r => r.Count);

        return new SalesBucketCounts(
            New: Sum(r => r.Status == OrderStatus.AwaitingReview),
            Active: Sum(r => ActiveSalesStatuses.Contains(r.Status) && r.State == OrderConversationState.Active),
            WaitingForCustomer: Sum(r => ActiveSalesStatuses.Contains(r.Status) && r.State == OrderConversationState.WaitingForCustomer),
            WaitingForStaff: Sum(r => ActiveSalesStatuses.Contains(r.Status) && r.State == OrderConversationState.WaitingForStaff),
            Resolved: Sum(r => r.State == OrderConversationState.Resolved),
            Unassigned: Sum(r => ActiveSalesStatuses.Contains(r.Status) && r.Unassigned));
    }

    private static IQueryable<Order> ApplyBucket(IQueryable<Order> q, string bucket, Guid? staffUserId)
    {
        // The unassigned bucket is assignment-agnostic by definition (owner/manager triage).
        if (staffUserId is Guid sid && bucket != "unassigned")
            q = q.Where(o => o.AssignedSalesUserId == sid);

        return bucket switch
        {
            "active" => q.Where(o => ActiveSalesStatuses.Contains(o.Status)
                && o.ConversationState == OrderConversationState.Active),
            "waiting-customer" => q.Where(o => ActiveSalesStatuses.Contains(o.Status)
                && o.ConversationState == OrderConversationState.WaitingForCustomer),
            "waiting-staff" => q.Where(o => ActiveSalesStatuses.Contains(o.Status)
                && o.ConversationState == OrderConversationState.WaitingForStaff),
            "resolved" => q.Where(o => o.ConversationState == OrderConversationState.Resolved),
            "unassigned" => q.Where(o => ActiveSalesStatuses.Contains(o.Status)
                && o.AssignedSalesUserId == null),
            _ => q.Where(o => o.Status == OrderStatus.AwaitingReview), // "new"
        };
    }
}
