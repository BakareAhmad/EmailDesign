using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Controllers.Mvc;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>
/// Public, unauthenticated document links (share tokens): /i/{token} for invoices,
/// /r/{token} for receipts. No login required; unknown tokens return 404.
/// </summary>
public sealed class PublicInvoiceController(IInvoiceQueries invoices, IApplicationSettingRepository settings) : MvcControllerBase
{
    private readonly IInvoiceQueries _invoices = invoices;
    private readonly IApplicationSettingRepository _settings = settings;

    /// <summary>Shared invoice document.</summary>
    [HttpGet("/i/{token}")]
    public async Task<IActionResult> Invoice(string token)
    {
        var doc = await _invoices.GetDocumentByShareTokenAsync(token, CancellationToken.None);
        if (doc is null) return NotFound();
        ViewData["Title"] = doc.InvoiceNumber;
        await LoadBusinessHeaderAsync();
        return View("Invoice", doc);
    }

    /// <summary>Downloadable PDF version of the shared invoice document.</summary>
    [HttpGet("/i/{token}/pdf")]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> InvoicePdf(string token)
    {
        var doc = await _invoices.GetDocumentByShareTokenAsync(token, CancellationToken.None);
        if (doc is null) return NotFound();

        var business = new Dictionary<string, string>();
        foreach (var key in new[] { "business.name", "business.motto", "business.phone", "business.email", "business.address" })
        {
            var s = await _settings.GetByKeyAsync(key);
            if (s is not null && !string.IsNullOrWhiteSpace(s.Value))
                business[key] = s.Value;
        }

        var pdf = DeChimex.Host.Services.InvoicePdfBuilder.Build(doc, business);
        var safeName = doc.InvoiceNumber.Replace(" ", "_");
        return File(pdf, "application/pdf", $"{safeName}.pdf");
    }

    /// <summary>Shared receipt document.</summary>
    [HttpGet("/r/{token}")]
    public async Task<IActionResult> Receipt(string token)
    {
        var doc = await _invoices.GetReceiptByShareTokenAsync(token, CancellationToken.None);
        if (doc is null) return NotFound();
        ViewData["Title"] = doc.ReceiptNumber;
        await LoadBusinessHeaderAsync();
        return View("Receipt", doc);
    }

    private async Task LoadBusinessHeaderAsync()
    {
        ViewBag.BusinessName = (await _settings.GetByKeyAsync("business.name"))?.Value ?? "DeChimex";
        ViewBag.BusinessMotto = (await _settings.GetByKeyAsync("business.motto"))?.Value;
        ViewBag.BusinessPhone = (await _settings.GetByKeyAsync("business.phone"))?.Value;
        ViewBag.BusinessEmail = (await _settings.GetByKeyAsync("business.email"))?.Value;
        ViewBag.BusinessAddress = (await _settings.GetByKeyAsync("business.address"))?.Value;
    }
}
