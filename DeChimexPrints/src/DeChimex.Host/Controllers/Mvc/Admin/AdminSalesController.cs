using System.Security.Claims;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Orders.Queries;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Sales inbox / work queue for the custom-order conversation workflow. Sales staff see
/// their own assigned work; owner/manager see everything, can filter per staff member,
/// and triage unassigned requests. Read access follows the orders workspace (orders.view).
/// </summary>
[Authorize(Policy = Permissions.Orders.View)]
[Route("admin/sales")]
public sealed class AdminSalesController : MvcControllerBase
{
    private static readonly string[] Buckets =
        ["new", "active", "waiting-customer", "waiting-staff", "resolved", "unassigned"];

    [HttpGet]
    public async Task<IActionResult> Index(string bucket = "new", Guid? staffId = null, string? search = null, int page = 1)
    {
        ViewData["Title"] = "Sales inbox";
        ViewData["Heading"] = "Sales inbox";
        ViewData["ActiveNav"] = "sales";

        if (!Buckets.Contains(bucket))
            bucket = "new";

        var isLead = User.IsInRole(RoleNames.Owner) || User.IsInRole(RoleNames.Manager);
        Guid? effectiveStaff = null;
        if (!isLead)
        {
            // Sales staff work their own queue only.
            var self = User.FindFirstValue(ClaimTypes.NameIdentifier);
            effectiveStaff = Guid.TryParse(self, out var uid) ? uid : Guid.Empty;
        }
        else if (bucket != "unassigned")
        {
            effectiveStaff = staffId;
        }

        var filter = new SalesInboxFilter
        {
            Page = page, PageSize = 20, StaffUserId = effectiveStaff, Bucket = bucket, Search = search
        };
        var result = await Sender.Send(new SearchSalesInboxQuery(filter));
        var counts = await Sender.Send(new GetSalesBucketCountsQuery(effectiveStaff));

        ViewBag.Bucket = bucket;
        ViewBag.Counts = counts;
        ViewBag.Search = search;
        ViewBag.Page = result.Page;
        ViewBag.PageSize = result.PageSize;
        ViewBag.TotalRecords = result.TotalRecords;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        ViewBag.IsLead = isLead;
        ViewBag.StaffId = staffId?.ToString();
        ViewBag.UnassignedCount = counts.Unassigned;
        ViewBag.StaffOptions = isLead
            ? (await Sender.Send(new GetEligibleSalesStaffQuery()))
                .Select(s => new SelectListItem($"{s.Name} ({s.ActiveWorkload} active)", s.UserId.ToString()))
                .ToList()
            : new List<SelectListItem>();
        return View(result.Items);
    }
}
