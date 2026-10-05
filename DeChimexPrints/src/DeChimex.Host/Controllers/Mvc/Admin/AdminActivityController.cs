using DeChimex.Application.Features.Activity;
using DeChimex.Host.Realtime;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace DeChimex.Host.Controllers.Mvc.Admin;
// Authentication plus the current database permission below: stale cookie claims must
// neither keep revoked access nor block a newly delegated grant.
[Authorize]
[Route("admin/activity")]
public sealed class AdminActivityController(DeChimexDbContext db,IActivityQueries activity) : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] ActivityFilter filter)
    {
        if(CurrentUser.UserId is not Guid id||!await ActivityAccess.AuthorizedUsers(db).ContainsAsync(id))return StatusCode(403);
        ViewData["Title"]="Business activity";ViewData["Heading"]="Business activity";ViewData["ActiveNav"]="activity";
        ViewBag.Filter=filter;
        Response.Headers.CacheControl="no-store";
        if(filter.From>filter.To)ModelState.AddModelError("To","The end date must not precede the start date.");
        if(filter.To?.Year>=9999)ModelState.AddModelError("To","Invalid end date.");
        if(!ModelState.IsValid)
        {
            if(Request.Query["format"]=="rows")return BadRequest("Invalid activity filters.");
            Response.StatusCode=400;
            return View(new ActivityPage(Array.Empty<ActivityItem>(),1,false));
        }
        var page=await activity.SearchAsync(filter,HttpContext.RequestAborted);
        Response.Headers.CacheControl="no-store";
        if(Request.Query["format"]=="rows")return PartialView("_Rows",page);
        ViewData["Title"]="Business activity";ViewData["Heading"]="Business activity";ViewData["ActiveNav"]="activity";
        ViewBag.Filter=filter;
        return View(page);
    }
}
