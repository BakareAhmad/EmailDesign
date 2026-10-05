using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Application.Features.Customers.Commands;
using DeChimex.Application.Features.Customers.Queries;
using DeChimex.Contracts.Customers;
using DeChimex.Application.Features.Invoices;
using DeChimex.Application.Features.Products.Queries;
using DeChimex.Application.Features.Services.Queries;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Controllers;
using DeChimex.Host.Controllers.Mvc;
using DeChimex.Host.Models.Admin;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Admin invoice listing, quote creation, and document viewing.
/// Quote invoices (no order yet) are created here by staff and can later be
/// converted into an order from /admin/orders/new?invoice={id}.
/// </summary>
[Authorize(Policy = Permissions.Orders.View)]
[Route("admin/invoices")]
public sealed class AdminInvoicesController(DeChimexDbContext db, IInvoiceQueries invoices, IInvoiceRepository invoicesRepo,
    IApplicationSettingRepository settings) : MvcControllerBase
{
    private readonly DeChimexDbContext _db = db;
    private readonly IInvoiceQueries _invoices = invoices;
    private readonly IInvoiceRepository _invoicesRepo = invoicesRepo;
    private readonly IApplicationSettingRepository _settings = settings;

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, int pageSize = 10, string? search = null, string? status = null)
    {
        ViewData["Title"] = "Invoices";
        ViewData["Heading"] = "Invoices";
        ViewData["ActiveNav"] = "invoices";

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 100 ? 10 : pageSize;

        // LEFT JOIN so quote invoices (OrderId null) appear alongside order invoices.
        var query = _db.Invoices.AsNoTracking()
            .SelectMany(i => _db.Orders.Where(o => o.Id == i.OrderId).DefaultIfEmpty(), (i, o) => new { i, o })
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(x => EF.Functions.Like(x.i.InvoiceNumber, $"%{s}%")
                                  || EF.Functions.Like(x.i.CustomerName, $"%{s}%")
                                  || (x.o != null && EF.Functions.Like(x.o.OrderNumber, $"%{s}%")));
        }
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<InvoiceStatus>(status, true, out var st))
            query = query.Where(x => x.i.Status == st);

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(x => x.i.IssuedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.i,
                OrderNumber = x.o != null ? x.o.OrderNumber : (string?)null,
                OrderCustomerFirst = x.o != null && x.o.Customer != null ? x.o.Customer.FirstName : (string?)null,
                OrderCustomerLast = x.o != null && x.o.Customer != null ? x.o.Customer.LastName : (string?)null
            })
            .ToListAsync();

        var items = rows.Select(x => new InvoiceListDto(
            x.i.Id, x.i.InvoiceNumber, x.i.OrderId, x.OrderNumber,
            x.OrderCustomerFirst is not null
                ? ((x.OrderCustomerFirst + " " + x.OrderCustomerLast).Trim())
                : x.i.CustomerName,
            x.i.Subtotal.Amount, x.i.Discount.Amount, x.i.DeliveryFee.Amount, x.i.Tax.Amount,
            x.i.GrandTotal.Amount, x.i.GrandTotal.Currency, x.i.Status.ToString(),
            x.i.IssuedAtUtc, x.i.PaidAtUtc, x.i.DocumentUrl, x.i.ShareToken, x.i.OrderId == null)).ToList();

        ViewBag.Search = search;
        ViewBag.Status = status;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.TotalRecords = total;
        ViewBag.TotalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        ViewBag.Statuses = Enum.GetNames<InvoiceStatus>();
        return View(items);
    }

    /// <summary>Quote invoice form (staff only): products/services selection, no order placed.</summary>
    [HttpGet("new")]
    [Authorize(Policy = Permissions.StaffCounterPolicy)]
    public async Task<IActionResult> New()
    {
        ViewData["Title"] = "New quote invoice";
        ViewData["Heading"] = "Invoices";
        ViewData["ActiveNav"] = "invoices";
        await PopulateCatalogAsync();
        // Seed one blank line so the form renders an editable row and the
        // "+ Add line" cloner has a template with the real product/service options.
        return View(new QuoteInvoiceFormViewModel { Lines = { new QuoteLineViewModel() } });
    }

    [HttpPost("new")]
    [Authorize(Policy = Permissions.StaffCounterPolicy)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(QuoteInvoiceFormViewModel model)
    {
        ViewData["Title"] = "New quote invoice";
        ViewData["Heading"] = "Invoices";
        ViewData["ActiveNav"] = "invoices";

        if (!ModelState.IsValid)
        {
            await PopulateCatalogAsync(model.CustomerId);
            return View(model);
        }

        Guid customerId;
        var isExisting = string.Equals(model.CustomerMode, "existing", StringComparison.OrdinalIgnoreCase);

        if (isExisting)
        {
            if (model.CustomerId is not Guid cid)
            {
                ModelState.AddModelError("CustomerId", "Choose a customer.");
                await PopulateCatalogAsync(model.CustomerId);
                return View(model);
            }
            var existing = await Sender.Send(new GetCustomerByIdQuery(cid));
            if (existing is null)
            {
                ModelState.AddModelError("CustomerId", "That customer no longer exists.");
                await PopulateCatalogAsync(model.CustomerId);
                return View(model);
            }
            customerId = existing.Id;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(model.NewFirstName) || string.IsNullOrWhiteSpace(model.NewLastName))
            {
                ModelState.AddModelError(string.Empty, "Provide the customer's first and last name.");
                await PopulateCatalogAsync(model.CustomerId);
                return View(model);
            }
            try
            {
                var created = await Sender.Send(new CreateCustomerCommand(new CreateCustomerRequest(
                    model.NewFirstName, model.NewLastName,
                    string.IsNullOrWhiteSpace(model.NewEmail) ? null : model.NewEmail,
                    string.IsNullOrWhiteSpace(model.NewPhone) ? null : model.NewPhone, null)));
                customerId = created.Id;
            }
            catch (ConflictException ex)
            {
                ModelState.AddModelError("NewEmail", ex.Message);
                await PopulateCatalogAsync(model.CustomerId);
                return View(model);
            }
        }

        var lines = new List<QuoteLineRequest>();
        foreach (var l in model.Lines)
        {
            var hasProduct = Guid.TryParse(l.ProductId, out var pid) && pid != Guid.Empty;
            var hasService = Guid.TryParse(l.ServiceId, out var sid) && sid != Guid.Empty;
            if (!hasProduct && !hasService) continue;
            if (hasProduct && hasService)
            {
                ModelState.AddModelError(string.Empty, "Each line must be either a product or a service, not both.");
                await PopulateCatalogAsync(model.CustomerId);
                return View(model);
            }
            lines.Add(new QuoteLineRequest(hasProduct ? pid : null, hasService ? sid : null, l.Quantity,
                string.IsNullOrWhiteSpace(l.Notes) ? null : l.Notes));
        }

        if (lines.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Add at least one product or service line.");
            await PopulateCatalogAsync(model.CustomerId);
            return View(model);
        }

        try
        {
            var invoice = await Sender.Send(new CreateQuoteInvoiceCommand(new CreateQuoteInvoiceRequest(
                customerId, lines, Math.Max(0m, model.Discount), Math.Max(0m, model.Tax),
                string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes)));
            TempData["Success"] = $"Quote invoice {invoice.InvoiceNumber} created. You can share it or create an order from it.";
            return RedirectToAction(nameof(View), new { id = invoice.Id });
        }
        catch (BusinessRuleValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateCatalogAsync(model.CustomerId);
            return View(model);
        }
    }

    /// <summary>Full invoice document (admin view): line items, totals, share link, actions.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> View(Guid id)
    {
        var doc = await _invoices.GetDocumentByIdAsync(id, CancellationToken.None);
        if (doc is null) return NotFound();

        ViewData["Title"] = doc.InvoiceNumber;
        ViewData["Heading"] = "Invoices";
        ViewData["ActiveNav"] = "invoices";
        await LoadBusinessHeaderAsync();
        return View(doc);
    }

    private async Task LoadBusinessHeaderAsync()
    {
        ViewBag.BusinessName = (await _settings.GetByKeyAsync("business.name"))?.Value ?? "DeChimex";
        ViewBag.BusinessMotto = (await _settings.GetByKeyAsync("business.motto"))?.Value;
        ViewBag.BusinessPhone = (await _settings.GetByKeyAsync("business.phone"))?.Value;
        ViewBag.BusinessEmail = (await _settings.GetByKeyAsync("business.email"))?.Value;
        ViewBag.BusinessAddress = (await _settings.GetByKeyAsync("business.address"))?.Value;
    }

    /// <summary>Generate a public share link for the invoice (no login needed on the link).</summary>
    [HttpPost("{id:guid}/share")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Share(Guid id)
    {
        var doc = await _invoices.GetDocumentByIdAsync(id, CancellationToken.None);
        if (doc is null) return NotFound();

        var invoice = await _invoicesRepo.GetByIdAsync(id, CancellationToken.None);
        if (invoice is null) return NotFound();
        if (string.IsNullOrEmpty(invoice.ShareToken))
        {
            invoice.SetShareToken(Guid.NewGuid().ToString("N"));
            _invoicesRepo.Update(invoice);
        }
        doc = await _invoices.GetDocumentByIdAsync(id, CancellationToken.None);
        if (doc is null) return NotFound();

        TempData["Success"] = "Share link generated. Copy it and send it to your customer.";
        return RedirectToAction(nameof(View), new { id });
    }

    private async Task PopulateCatalogAsync(Guid? selectedCustomerId = null)
    {
        var products = await Sender.Send(new SearchProductsQuery(new ProductFilter
        {
            Page = 1, PageSize = 200, ActiveOnly = true
        }));
        var services = await Sender.Send(new GetServicesQuery(ActiveOnly: true));
        if (selectedCustomerId is not null)
        {
            var selected = await Sender.Send(new GetCustomerByIdQuery(selectedCustomerId.Value));
            if (selected is not null)
                ViewBag.SelectedCustomer = selected.FullName + (selected.IsGuest ? " (guest)" : "");
        }
        ViewBag.Products = products.Items
            .Select(p => new SelectListItem($"{p.Name} ({p.Sku}) - {p.SellingPrice:N2}", p.Id.ToString()))
            .ToList();
        ViewBag.Services = services
            .Select(sv => new SelectListItem($"{sv.Name} - {sv.BasePrice:N2}", sv.Id.ToString()))
            .ToList();
    }
}
