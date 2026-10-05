using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Contacts.Commands;
using DeChimex.Application.Features.Contacts.Queries;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Contact-form submissions from the public site. Staff read and resolve them here;
/// the messages are persisted, so this works even when no email provider is configured.
/// </summary>
[Authorize(Policy = Permissions.Messages.View)]
[Route("admin/messages")]
public sealed class AdminMessagesController : MvcControllerBase
{
    private readonly IContactMessageQueries _queries;

    public AdminMessagesController(IContactMessageQueries queries)
    {
        _queries = queries;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, string? search = null, bool? unresolvedOnly = null)
    {
        ViewData["Title"] = "Messages";
        ViewData["Heading"] = "Messages";
        ViewData["ActiveNav"] = "messages";

        var result = await Sender.Send(new GetContactMessagesQuery(
            new DeChimex.Application.Common.Models.PagedRequest { Page = page, PageSize = 20, Search = search },
            unresolvedOnly == true));

        ViewBag.Search = search;
        ViewBag.Page = result.Page;
        ViewBag.PageSize = result.PageSize;
        ViewBag.TotalRecords = result.TotalRecords;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        ViewBag.UnresolvedOnly = unresolvedOnly == true;
        return View(result.Items);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        ViewData["Title"] = "Message";
        ViewData["Heading"] = "Messages";
        ViewData["ActiveNav"] = "messages";

        var item = await _queries.GetAsync(id, HttpContext.RequestAborted);
        if (item is null) return NotFound();

        ViewData["Reply"] = new ReplyContactMessageFormViewModel();
        return View(item);
    }

    /// <summary>Staff reply: persists the reply on the message thread and emails it to the enquirer.</summary>
    [HttpPost("{id:guid}/reply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(Guid id, ReplyContactMessageFormViewModel model)
    {
        ViewData["Title"] = "Message";
        ViewData["Heading"] = "Messages";
        ViewData["ActiveNav"] = "messages";

        var item = await _queries.GetAsync(id, HttpContext.RequestAborted);
        if (item is null) return NotFound();

        if (!ModelState.IsValid)
        {
            ViewData["Reply"] = model;
            ViewData["ReplyErrors"] = ModelState.Values
                .SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
            return View("Details", item);
        }

        var actor = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        try
        {
            var outcome = await Sender.Send(new SendContactMessageReplyCommand(
                id, model.Subject, model.Body,
                Guid.TryParse(actor, out var uid) ? uid : null,
                User.Identity?.Name));

            // Never claim a delivery that did not happen: the reply is persisted either
            // way, but the flash tells the truth about the email itself.
            TempData[outcome.Emailed ? "Success" : "Error"] = outcome.Emailed
                ? $"Reply emailed to {item.Email}."
                : $"Reply saved on the thread, but the email to {item.Email} did NOT go out. {outcome.Failure}";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (ValidationAppException ex)
        {
            foreach (var (field, messages) in ex.Errors)
                foreach (var message in messages)
                    ModelState.AddModelError(field, message);
            ViewData["Reply"] = model;
            ViewData["ReplyErrors"] = ModelState.Values
                .SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
            return View("Details", item);
        }

    }

    [HttpPost("{id:guid}/resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(Guid id, bool resolve = true)
    {
        var actor = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        await Sender.Send(new ResolveContactMessageCommand(
            id, resolve, Guid.TryParse(actor, out var uid) ? uid : null));
        TempData["Success"] = resolve ? "Message marked as resolved." : "Message reopened.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
