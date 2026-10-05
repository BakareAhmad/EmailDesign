using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Contacts.Commands;
using DeChimex.Application.Features.Services.Queries;
using DeChimex.Host.Models.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeChimex.Host.Controllers.Mvc.Store;

/// <summary>Static public content pages (about, contact, faq, privacy, terms).</summary>
[AllowAnonymous]
public sealed class StorePagesController : StoreControllerBase
{
    [HttpGet("about")]
    public async Task<IActionResult> About()
    {
        ViewData["Title"] = "About";
        ViewData["ActiveNav"] = "about";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        return View();
    }

    [HttpGet("contact")]
    public async Task<IActionResult> Contact()
    {
        ViewData["Title"] = "Contact";
        ViewData["ActiveNav"] = "contact";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        ViewBag.Services = await Sender.Send(new GetServicesQuery(ActiveOnly: true));
        return View(new ContactFormViewModel());
    }

    [HttpPost("contact")]
    [EnableRateLimiting("sensitive")] // AUD-024: staff-inbox flooding costs real time and email credits
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ContactPost(ContactFormViewModel model)
    {
        ViewData["Title"] = "Contact";
        ViewData["ActiveNav"] = "contact";
        var business = await LoadBusinessInfoAsync();
        ViewBag.Business = business;
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        ViewBag.Services = await Sender.Send(new GetServicesQuery(ActiveOnly: true));

        if (!ModelState.IsValid)
            return View("Contact", model);

        var serviceName = string.IsNullOrWhiteSpace(model.Service) ? "General enquiry" : model.Service;

        // Persist first so the enquiry is captured for staff even when no email
        // provider is configured; email is a best-effort notification, not a gate.
        await Sender.Send(new SaveContactMessageCommand(
            model.Name, model.Email, model.Phone, model.Service, model.Details));

        try
        {
            var toEmail = business.GetValueOrDefault("business.email", "chimexgraphics@gmail.com");
            // Branded staff notification (same shell as every outgoing email). The visitor's
            // details are HTML-encoded by the renderer's model substitution; the message
            // paragraphs are encoded here and passed as a trusted Raw fragment.
            var paragraphs = model.Details
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => $"<p style='margin:0 0 10px'>{HtmlEncode(p)}</p>");
            var content = HttpContext.RequestServices.GetRequiredService<IEmailTemplateRenderer>()
                .Render(EmailTemplate.ContactMessageReceived, new Dictionary<string, string>
                {
                    ["Name"] = model.Name,
                    ["Email"] = model.Email,
                    ["Phone"] = model.Phone ?? "",
                    ["Service"] = serviceName,
                    ["RawBody"] = string.Concat(paragraphs)
                });

            await HttpContext.RequestServices.GetRequiredService<IEmailQueue>().EnqueueAsync(new EmailMessage(
                toEmail, model.Name, content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.ContactMessageReceived), Category: "staff"));
        }
        catch (Exception)
        {
            // The message is already saved for staff; a failed notification is not an error
            // for the visitor.
        }

        TempData["Success"] = "Message sent! We will get back to you shortly.";
        return RedirectToAction(nameof(Contact));
    }

    private static string HtmlEncode(string s) =>
        System.Net.WebUtility.HtmlEncode(s);

    [HttpGet("faq")]
    public async Task<IActionResult> Faq()
    {
        ViewData["Title"] = "FAQ";
        ViewData["ActiveNav"] = "faq";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        return View();
    }

    [HttpGet("install-help")]
    public async Task<IActionResult> InstallHelp()
    {
        ViewData["Title"] = "Install the app";
        ViewData["ActiveNav"] = "";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        return View();
    }

    [HttpGet("privacy")]
    public async Task<IActionResult> Privacy()
    {
        ViewData["Title"] = "Privacy";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        return View();
    }

    [HttpGet("terms")]
    public async Task<IActionResult> Terms()
    {
        ViewData["Title"] = "Terms";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        return View();
    }
}
