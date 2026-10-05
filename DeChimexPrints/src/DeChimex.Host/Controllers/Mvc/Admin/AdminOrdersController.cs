using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Application.Features.Customers.Commands;
using DeChimex.Application.Features.Customers.Queries;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Orders.Queries;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Application.Features.Products.Queries;
using DeChimex.Application.Features.Services.Queries;
using DeChimex.Contracts.Customers;
using DeChimex.Contracts.Invoices;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin order management (list, details, full lifecycle: pay, produce, deliver, cancel, refund).</summary>
[Authorize(Policy = Permissions.Orders.View)]
[Route("admin/orders")]
public sealed class AdminOrdersController(
    IApplicationSettingRepository settings, 
    IPaymentRepository payments, 
    IInvoiceQueries invoices,
    ILogger<AdminOrdersController> logger) : MvcControllerBase
{
    private readonly IApplicationSettingRepository _settings = settings;
    private readonly IPaymentRepository _payments = payments;
    private readonly IInvoiceQueries _invoices = invoices;
    private readonly ILogger<AdminOrdersController> _logger = logger;

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, string? status = null, string? search = null)
    {
        ViewData["Title"] = "Orders";
        ViewData["Heading"] = "Orders";
        ViewData["ActiveNav"] = "orders";

        var filter = new OrderFilter { Page = page, PageSize = 20, Status = status, Search = search };
        var result = await Sender.Send(new SearchOrdersQuery(filter));

        ViewBag.Status = status;
        ViewBag.Search = search;
        ViewBag.Page = result.Page;
        ViewBag.PageSize = result.PageSize;
        ViewBag.TotalRecords = result.TotalRecords;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        ViewBag.Statuses = Enum.GetNames<OrderStatus>();
        ViewBag.CanUpdateStatus = User.HasClaim(Permissions.ClaimType, Permissions.Orders.UpdateStatus);
        ViewBag.CanCancel = User.HasClaim(Permissions.ClaimType, Permissions.Orders.Cancel);
        ViewBag.CanRefund = User.HasClaim(Permissions.ClaimType, Permissions.Orders.Refund);
        return View(result.Items);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var order = await Sender.Send(new GetOrderByIdQuery(id));
        if (order is null) return NotFound();

        ViewData["Title"] = order.OrderNumber;
        ViewData["Heading"] = "Orders";
        ViewData["ActiveNav"] = "orders";
        ViewBag.CanUpdateStatus = User.HasClaim(Permissions.ClaimType, Permissions.Orders.UpdateStatus);
        ViewBag.CanCancel = User.HasClaim(Permissions.ClaimType, Permissions.Orders.Cancel);
        ViewBag.CanRefund = User.HasClaim(Permissions.ClaimType, Permissions.Orders.Refund);
        ViewBag.AllowedActions = AllowedActionsFor(order.Status);

        var payments = await _payments.GetByOrderIdAsync(order.Id);
        ViewBag.Payment = payments
            .OrderByDescending(p => p.PaidAtUtc ?? p.CreatedDateUtc)
            .FirstOrDefault(p => p.Status == PaymentStatus.Succeeded)
            ?? payments.OrderByDescending(p => p.CreatedDateUtc).FirstOrDefault();
        ViewBag.InvoiceDocument = await _invoices.GetDocumentByOrderIdAsync(order.Id, CancellationToken.None);
        ViewBag.ReceiptDocument = await _invoices.GetReceiptByOrderIdAsync(order.Id, CancellationToken.None);
        return View(order);
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = Permissions.Orders.UpdateStatus)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(Guid id, string status)
    {
        if (Enum.TryParse<OrderStatus>(status, true, out var parsed))
        {
            try
            {
                await Sender.Send(new UpdateOrderStatusCommand(id, parsed.ToString()));
                TempData["Success"] = $"Order moved to {parsed.ToString().Replace("Paid", "Paid")}.";
            }
            catch (BusinessRuleValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (ConflictException ex)
            {
                TempData["Error"] = ex.Message;
            }
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Manual payment confirmation (bank transfer, cash, or a shared Paystack
    /// reference). Runs the same fulfilment pipeline as gateway payment verification.
    /// </summary>
    [HttpPost("{id:guid}/mark-paid")]
    [Authorize(Policy = Permissions.Orders.UpdateStatus)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(Guid id, MarkPaidFormViewModel model)
    {
        try
        {
            var payment = await Sender.Send(new ConfirmOrderPaymentCommand(
                id, model.Method, string.IsNullOrWhiteSpace(model.Note) ? null : model.Note));
            TempData["Success"] = $"Payment of {payment.Amount:0.00} confirmed as {payment.Method}. Order is now paid.";
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (ConflictException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (ValidationAppException ex)
        {
            TempData["Error"] = ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "Payment could not be confirmed.";
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Permissions.Orders.Cancel)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, OrderCancelFormViewModel model)
    {
        try
        {
            // AUD-018: an explicit resolution for any held payment travels with the cancel,
            // so a cancelled paid order can never silently keep the customer's money.
            await Sender.Send(new CancelOrderCommand(id, new CancelOrderRequest(
                string.IsNullOrWhiteSpace(model.Reason) ? null : model.Reason,
                model.RefundOutcome)));
            TempData["Success"] = "Order cancelled.";
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (ConflictException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/refund")]
    [Authorize(Policy = Permissions.Orders.Refund)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(Guid id)
    {
        try
        {
            await Sender.Send(new RefundOrderCommand(id));
            TempData["Success"] = "Refund recorded on the order and linked documents. Gateway requests, if applicable, were accepted; final settlement is not confirmed here.";
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (ConflictException ex)
        {
            // This captures Paystack refund failures propagated from the coordinator
            TempData["Error"] = "Not all refunds were accepted. Check activity for any accepted payment before retrying: " + ex.Message;
        }
        catch (Exception ex)
        {
            // Catch any other unexpected errors (network, etc.)
            TempData["Error"] = "Refund outcome needs reconciliation. Check the provider and activity before retrying to avoid repeating an accepted request.";
            _logger.LogError(ex, "Unexpected error during refund for order {OrderId}", id);
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Manual order entry (counter/phone/WhatsApp orders): pick or create a customer,
    /// add product and service lines, set pricing, place the order as PendingPayment.
    /// </summary>
    [HttpGet("new")]
    [Authorize(Policy = Permissions.StaffCounterPolicy)]
    public async Task<IActionResult> New(Guid? invoice)
    {
        ViewData["Title"] = "New order";
        ViewData["Heading"] = "Orders";
        ViewData["ActiveNav"] = "orders";

        var model = new OrderBuilderViewModel();
        if (invoice is not null)
        {
            var quote = await _invoices.GetDocumentByIdAsync(invoice.Value, CancellationToken.None);
            if (quote is null || quote.Status != "Draft")
            {
                TempData["Error"] = "That quote invoice can no longer be converted into an order.";
                await PopulateCatalogAsync();
                return View(model);
            }
            model.InvoiceId = quote.Id;
            model.CustomerId = quote.CustomerId;
            model.CustomerMode = "existing";
            model.Notes = quote.Notes;
            ViewBag.QuoteInvoice = quote;
            await PopulateCatalogAsync(quote.CustomerId);
            return View(model);
        }
        await PopulateCatalogAsync();
        return View(model);
    }

    [HttpPost("new")]
    [Authorize(Policy = Permissions.StaffCounterPolicy)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(OrderBuilderViewModel model)
    {
        ViewData["Title"] = "New order";
        ViewData["Heading"] = "Orders";
        ViewData["ActiveNav"] = "orders";

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
                ModelState.AddModelError(string.Empty, "Provide the new customer's first and last name.");
                await PopulateCatalogAsync(model.CustomerId);
                return View(model);
            }
            // Email is optional: a blank email creates a walk-in guest customer.
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

        var items = model.Products
            .Where(p => p.ProductId is Guid pid && pid != Guid.Empty && p.Quantity > 0)
            .Select(p => new CreateOrderItemRequest(p.ProductId!.Value, p.Quantity))
            .ToList();
        var services = model.Services
            .Where(s => s.ServiceId is Guid sid && sid != Guid.Empty && s.Quantity > 0)
            .Select(s => new CreateOrderServiceRequest(s.ServiceId!.Value, s.Quantity,
                string.IsNullOrWhiteSpace(s.Notes) ? null : s.Notes))
            .ToList();

        if (items.Count == 0 && services.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Add at least one product or service line.");
            await PopulateCatalogAsync(model.CustomerId);
            return View(model);
        }

        var isDelivery = string.Equals(model.Fulfilment, "delivery", StringComparison.OrdinalIgnoreCase);
        var deliveryFee = 0m;
        AddressRequest? shipping = null;
        if (isDelivery)
        {
            if (string.IsNullOrWhiteSpace(model.Street) || string.IsNullOrWhiteSpace(model.City)
                || string.IsNullOrWhiteSpace(model.State) || string.IsNullOrWhiteSpace(model.Country))
            {
                ModelState.AddModelError(string.Empty, "Please complete the delivery address.");
                await PopulateCatalogAsync(model.CustomerId);
                return View(model);
            }
            shipping = new AddressRequest(model.Street!, model.City!, model.State!, model.Country!, model.PostalCode);
            deliveryFee = await LoadDefaultDeliveryFeeAsync();
        }

        var request = new CreateOrderRequest(
            customerId, items, services, shipping, null,
            string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes,
            Math.Max(0m, model.Discount), deliveryFee, Math.Max(0m, model.Tax));

        try
        {
            var order = await Sender.Send(new CreateOrderCommand(request,
                string.Equals(model.InvoiceId?.ToString(), Guid.Empty.ToString()) ? null : model.InvoiceId,
                DeChimex.Domain.Enums.OrderKind.Custom));

            // Optional: take the payment in the same step (walk-in cash sales).
            // Reuses the same confirmation pipeline as "Mark as paid", so the
            // order goes straight to Paid with its invoice issued.
            if (!string.IsNullOrWhiteSpace(model.PaymentMethod)
                && Enum.TryParse<PaymentMethod>(model.PaymentMethod, ignoreCase: true, out var payMethod))
            {
                await Sender.Send(new ConfirmOrderPaymentCommand(order.Id, payMethod.ToString(),
                    string.IsNullOrWhiteSpace(model.PaymentNote) ? null : model.PaymentNote));
                TempData["Success"] = $"Order {order.OrderNumber} created and paid ({payMethod}). Invoice marked as paid; receipt generated.";
            }
            else
            {
                var invoiceNote = model.InvoiceId is not null ? "converted from the quote." : "issued.";
                TempData["Success"] = $"Order {order.OrderNumber} created for the customer. Invoice {invoiceNote}";
            }
            return RedirectToAction(nameof(Details), new { id = order.Id });
        }
        catch (BusinessRuleValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateCatalogAsync(model.CustomerId);
            return View(model);
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateCatalogAsync(model.CustomerId);
            return View(model);
        }
    }

    private async Task<decimal> LoadDefaultDeliveryFeeAsync()
    {
        var setting = await _settings.GetByKeyAsync("delivery.default_fee");
        return decimal.TryParse(setting?.Value, out var fee) && fee >= 0 ? fee : 1500m;
    }

    private async Task PopulateCatalogAsync(Guid? selectedCustomerId = null)
    {
        var products = await Sender.Send(new SearchProductsQuery(new ProductFilter
        {
            Page = 1, PageSize = 200, ActiveOnly = true
        }));
        var services = await Sender.Send(new GetServicesQuery(ActiveOnly: true));
        // The customer picker searches on demand via /admin/customers/picker,
        // so no capped customer list is rendered here anymore. When the form is
        // re-rendered after a validation error, show the already-selected name.
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
            .Select(s => new SelectListItem($"{s.Name} - {s.BasePrice:N2}", s.Id.ToString()))
            .ToList();
        ViewBag.DeliveryFee = await LoadDefaultDeliveryFeeAsync();
    }

    /// <summary>
    /// Legal next actions per status. AUD-008: this used to be a second, hand-maintained
    /// copy of the domain's transition table and had already drifted (it offered "Refund"
    /// for AwaitingProcurement, QualityCheck and OutForDelivery — states the aggregate's
    /// EnsureCanRefund rejects). The list is now DERIVED from the domain itself
    /// (Order.DomainActions), so the UI can never offer an illegal action again.
    /// </summary>
    internal static string[] AllowedActionsFor(string status)
        => Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed)
            ? Domain.Entities.Order.DomainActions(parsed).ToArray()
            : Array.Empty<string>();
}
