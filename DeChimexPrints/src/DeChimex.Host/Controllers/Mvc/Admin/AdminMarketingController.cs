using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Marketing.Commands;
using DeChimex.Application.Features.Marketing.Queries;
using DeChimex.Domain.Exceptions;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Marketing email broadcasts (promotions/newsletters to registered customers).
/// Visibly separate from transactional email: content is free-form sanitized HTML and
/// delivery runs in the background worker - the Send action only queues.
/// </summary>
[Authorize(Policy = Permissions.Marketing.View)]
[Route("admin/marketing")]
public sealed class AdminMarketingController : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1)
    {
        ViewData["Title"] = "Marketing";
        ViewData["Heading"] = "Marketing broadcasts";
        ViewData["ActiveNav"] = "marketing";

        var result = await Sender.Send(new GetMarketingBroadcastsQuery(
            new Application.Common.Models.PagedRequest { Page = page, PageSize = 20 }));

        ViewBag.Page = result.Page;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        ViewBag.EligibleRecipients = await Sender.Send(new GetMarketingRecipientCountQuery());
        return View(result.Items);
    }

    [HttpGet("compose")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    public async Task<IActionResult> Compose()
    {
        ViewData["Title"] = "New broadcast";
        ViewData["Heading"] = "New marketing broadcast";
        ViewData["ActiveNav"] = "marketing";
        ViewBag.EligibleRecipients = await Sender.Send(new GetMarketingRecipientCountQuery());
        return View(new MarketingBroadcastFormViewModel());
    }

    [HttpPost("compose")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Compose(MarketingBroadcastFormViewModel model)
    {
        ViewData["Title"] = "New broadcast";
        ViewData["Heading"] = "New marketing broadcast";
        ViewData["ActiveNav"] = "marketing";
        ViewBag.EligibleRecipients = await Sender.Send(new GetMarketingRecipientCountQuery());

        if (!ModelState.IsValid)
            return View(model);

        try
        {
            var id = await Sender.Send(new CreateMarketingBroadcastCommand(
                model.Subject.Trim(), model.HtmlContent, null, CurrentUser.Email ?? "admin"));
            TempData["Success"] = "Broadcast saved as draft. Review it, then send when ready.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (ValidationAppException ex)
        {
            AddValidationErrors(ex);
            return View(model);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var broadcast = await Sender.Send(new GetMarketingBroadcastDetailsQuery(id));
        if (broadcast is null)
            return NotFound();

        ViewData["Title"] = "Broadcast";
        ViewData["Heading"] = broadcast.Broadcast.Subject;
        ViewData["ActiveNav"] = "marketing";
        return View(broadcast);
    }

    /// <summary>Server-rendered preview of the exact email customers will receive.</summary>
    [HttpGet("{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id)
    {
        var broadcast = await Sender.Send(new GetMarketingBroadcastDetailsQuery(id));
        if (broadcast is null)
            return NotFound();

        var rendered = await Sender.Send(new PreviewMarketingBroadcastQuery(id));
        return Content(rendered, "text/html");
    }

    [HttpPost("{id:guid}/send")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(Guid id)
    {
        try
        {
            await Sender.Send(new SendMarketingBroadcastCommand(id));
            TempData["Success"] = "Broadcast queued - the background worker is delivering it now. Track progress below.";
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = Permissions.Marketing.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(Guid id)
    {
        try
        {
            await Sender.Send(new RetryFailedRecipientsCommand(id));
            TempData["Success"] = "Failed recipients requeued for delivery.";
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private void AddValidationErrors(ValidationAppException ex)
    {
        foreach (var (key, messages) in ex.Errors)
        {
            var field = key.StartsWith("Request.") ? key["Request.".Length..] : key;
            foreach (var message in messages)
                ModelState.AddModelError(field, message);
        }
    }
}
