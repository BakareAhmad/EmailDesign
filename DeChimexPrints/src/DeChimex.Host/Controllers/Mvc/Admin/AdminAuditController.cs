using DeChimex.Domain.Entities;
using DeChimex.Host.Controllers.Mvc;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Raw audit-trail viewer (AUD-026). AuditableSoftDeleteInterceptor already persists a full
/// Old/New diff for every aggregate change, atomically with the change — but the business
/// activity feed only projects rows with a business ActivityType, so the generic diff rows
/// (e.g. "who changed this price, from what, to what?") had no reader anywhere. This page
/// answers the forensic question directly from AuditLogs, paged in SQL.
/// </summary>
[Authorize(Policy = Permissions.Activity.View)]
[Route("admin/audit")]
public sealed class AdminAuditController(Persistence.Context.DeChimexDbContext db) : MvcControllerBase
{
    [HttpGet("raw")]
    public async Task<IActionResult> Raw(
        string? entity = null, string? entityId = null, string? actor = null, string? action = null,
        DateTime? from = null, DateTime? to = null, int page = 1)
    {
        ViewData["Title"] = "Raw audit trail";
        ViewData["Heading"] = "Audit";
        ViewData["ActiveNav"] = "activity";

        page = page < 1 ? 1 : page;

        var q = db.AuditLogs.AsNoTracking().AsQueryable();

        // Only the generic capture rows — the business-activity projection has its own page.
        q = q.Where(a => a.ActivityType == null);

        if (!string.IsNullOrWhiteSpace(entity))
            q = q.Where(a => a.EntityName == entity.Trim());
        if (!string.IsNullOrWhiteSpace(entityId))
            q = q.Where(a => a.EntityId == entityId.Trim());
        if (!string.IsNullOrWhiteSpace(action))
            q = q.Where(a => EF.Functions.Like(a.Action, $"%{action.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(actor))
            q = q.Where(a => EF.Functions.Like(a.PerformedBy!, $"%{actor.Trim()}%"));
        if (from.HasValue)
            q = q.Where(a => a.TimestampUtc >= from.Value.Date);
        if (to.HasValue)
        {
            var end = to.Value.Date.AddDays(1);
            q = q.Where(a => a.TimestampUtc < end);
        }

        var pageSize = 25;
        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(a => a.TimestampUtc).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new RawAuditRow(a.Id, a.TimestampUtc, a.Action, a.EntityName, a.EntityId,
                a.PerformedBy, a.OldValues, a.NewValues, a.IpAddress, a.CorrelationId))
            .ToListAsync();

        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.TotalRecords = total;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
        ViewBag.Entity = entity;
        ViewBag.EntityId = entityId;
        ViewBag.Actor = actor;
        ViewBag.Action = action;
        ViewBag.From = from;
        ViewBag.To = to;
        return View(items);
    }

    public sealed record RawAuditRow(
        Guid Id, DateTime TimestampUtc, string Action, string EntityName, string? EntityId,
        string? PerformedBy, string? OldValues, string? NewValues, string? IpAddress, string? CorrelationId);
}
