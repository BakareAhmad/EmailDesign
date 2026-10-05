using DeChimex.Application.Common;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Orders.Queries;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>Customer portal: own orders list + order details/tracking + custom-order workspace.</summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal/orders")]
public sealed class PortalOrdersController(
    ICustomerRepository customers,
    IOrderWorkspaceQueries workspace,
    IFileStorageService storage,
    IPaymentChargeCalculator chargeCalculator,
    IApplicationSettingRepository settings,
    IInvoiceQueries invoices) : PortalControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "My orders";
        ViewData["Heading"] = "My orders";
        ViewData["ActiveNav"] = "orders";

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var orders = await Sender.Send(new GetMyOrdersQuery(customer.Id));
        return View(orders);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await Sender.Send(new GetOrderByIdQuery(id));

        // Customers may only view their own orders.
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        ViewData["Title"] = order.OrderNumber;
        ViewData["Heading"] = "My orders";
        ViewData["ActiveNav"] = "orders";
        return View(order);
    }

    /// <summary>
    /// Track an order by its order number. The lookup is scoped to the signed-in
    /// customer's own orders: an unknown or foreign number simply reports "not found".
    /// </summary>
    [HttpGet("track")]
    public async Task<IActionResult> Track(string? orderNumber)
    {
        ViewData["Title"] = "Track order";
        ViewData["Heading"] = "My orders";
        ViewData["ActiveNav"] = "orders";

        if (!string.IsNullOrWhiteSpace(orderNumber))
        {
            var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
            var order = await Sender.Send(new GetOrderByNumberQuery(orderNumber.Trim()));
            if (order is not null && order.CustomerId == customer.Id)
            {
                ViewBag.Order = order;
                ViewBag.Found = true;
            }
            else
            {
                ViewBag.NotFoundNumber = orderNumber.Trim();
            }
        }
        return View();
    }

    // ----------------------------------------------------------------------------------
    // Custom-order workspace (customer side): details, responses, files, chat, proofs,
    // approval, payment. Route/flow per CUSTOM_ORDER_FLOW_PLAN.md §21.
    // ----------------------------------------------------------------------------------

    [HttpGet("{id:guid}/workspace")]
    public async Task<IActionResult> Workspace(Guid id)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await workspace.GetAsync(id, HttpContext.RequestAborted);
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        ViewData["Title"] = $"{order.OrderNumber} · {order.CustomerStatus}";
        ViewData["Heading"] = "My orders";
        ViewData["ActiveNav"] = "orders";
        ViewBag.CanPay = order.Status is "PendingPayment" or "Draft";
        var serviceFeePercent = ServiceFeeSettings.Parse((await settings.GetByKeyAsync(ServiceFeeSettings.Key, HttpContext.RequestAborted))?.Value);
        ViewBag.PaystackCharge = ViewBag.CanPay ? chargeCalculator.Compute(order.GrandTotal, serviceFeePercent).ServiceFeeAmount : 0m;
        ViewBag.InvoiceDocument = await invoices.GetDocumentByOrderIdAsync(id, HttpContext.RequestAborted);
        ViewBag.ReceiptDocument = await invoices.GetReceiptByOrderIdAsync(id, HttpContext.RequestAborted);
        // The customer fetched the thread, so their received messages are delivered
        // (idempotent; pushes only when something actually changed).
        await Sender.Send(new MarkMessagesDeliveredCommand(id, "customer"), HttpContext.RequestAborted);
        // Named sales staff must never leak to customer-facing views: the portal gets
        // the redacted projection (generic handled-by note + generic timeline text).
        return View(order.RedactForCustomer());
    }

    /// <summary>
    /// Live-state feed for the open workspace: the same redacted projection as
    /// <see cref="Workspace"/>, as JSON. The workspace script re-fetches this after
    /// reconnects and structural pushes (quote finalized, paid) and patches only the
    /// affected cards, so scroll position and typed-but-unsent input survive.
    /// </summary>
    [HttpGet("{id:guid}/workspace/state")]
    public async Task<IActionResult> WorkspaceState(Guid id)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await workspace.GetAsync(id, HttpContext.RequestAborted);
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        // Same redacted projection as the view, plus the pay-block inputs so the
        // client can render the payment button live after the quote lands.
        await Sender.Send(new MarkMessagesDeliveredCommand(id, "customer"), HttpContext.RequestAborted);
        var canPay = order.Status is "PendingPayment" or "Draft";
        var serviceFeePercent = ServiceFeeSettings.Parse((await settings.GetByKeyAsync(ServiceFeeSettings.Key, HttpContext.RequestAborted))?.Value);
        var invoice = await invoices.GetDocumentByOrderIdAsync(id, HttpContext.RequestAborted);
        var receipt = await invoices.GetReceiptByOrderIdAsync(id, HttpContext.RequestAborted);
        return Json(new
        {
            workspace = order.RedactForCustomer(),
            canPay,
            charge = canPay ? chargeCalculator.Compute(order.GrandTotal, serviceFeePercent).ServiceFeeAmount : 0m,
            invoiceToken = invoice?.ShareToken,
            receiptToken = receipt?.ShareToken
        });
    }

    [HttpPost("{id:guid}/workspace/message")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(Guid id, string? body, Guid? replyToMessageId,
        List<IFormFile>? files, List<string>? kinds, List<string>? durations)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await workspace.GetAsync(id, HttpContext.RequestAborted);
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        // Fetch submits get the persisted message as JSON (rendered in place, no
        // reload); plain form posts keep the PRG redirect for no-JS clients.
        var wantsJson = ChatSendSupport.WantsJson(Request);
        try
        {
            var attachments = await ChatSendSupport.UploadAttachmentsAsync(
                storage, files, kinds, durations, HttpContext.RequestAborted);
            var dto = await Sender.Send(new SendOrderMessageCommand(id,
                new SendOrderMessageRequest(body ?? string.Empty, replyToMessageId, attachments), "customer"));
            if (wantsJson)
                return Json(new { message = dto });
        }
        catch (ValidationAppException ex)
        {
            var error = ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "Message could not be sent.";
            if (wantsJson)
                return UnprocessableEntity(new { error });
            TempData["Error"] = error;
        }
        catch (Domain.Exceptions.BusinessRuleValidationException ex)
        {
            if (wantsJson)
                return UnprocessableEntity(new { error = ex.Message });
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Workspace), new { id });
    }

    /// <summary>Batched read beacon: ids the customer's client actually rendered
    /// while the page was visible. Unknown/own ids are ignored (idempotent).</summary>
    [HttpPost("{id:guid}/workspace/messages/mark-read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id, [FromBody] ChatMarkReadRequest? body)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await workspace.GetAsync(id, HttpContext.RequestAborted);
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        var read = body?.Ids is { Count: > 0 }
            ? await Sender.Send(new MarkMessagesReadCommand(id, body.Ids, "customer"))
            : (IReadOnlyList<Guid>)[];
        return Json(new { read });
    }

    [HttpPost("{id:guid}/workspace/files")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadFile(Guid id, IFormFile file, string? purpose)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await workspace.GetAsync(id, HttpContext.RequestAborted);
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        if (file is not null && file.Length > 0)
        {
            using var stream = file.OpenReadStream();
            var uploaded = await storage.UploadAsync(new FileUploadRequest(
                stream, file.FileName, file.ContentType, file.Length, FileCategory.OrderAttachment));
            await Sender.Send(new UploadOrderFileCommand(id, new UploadOrderFileRequest(
                uploaded.StoredFileName, file.FileName, uploaded.Url, file.ContentType, uploaded.Size,
                string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim()), "customer"));
        }
        return RedirectToAction(nameof(Workspace), new { id });
    }

    [HttpPost("{id:guid}/workspace/proof/{proofId:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveProof(Guid id, Guid proofId)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await workspace.GetAsync(id, HttpContext.RequestAborted);
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        try
        {
            await Sender.Send(new ApproveDesignProofCommand(id, proofId));
            TempData["Success"] = "Design approved — thank you!";
        }
        catch (ForbiddenException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Workspace), new { id });
    }

    [HttpPost("{id:guid}/workspace/proof/{proofId:guid}/changes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestChanges(Guid id, Guid proofId, string feedback)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await workspace.GetAsync(id, HttpContext.RequestAborted);
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        try
        {
            await Sender.Send(new RequestDesignChangesCommand(id, proofId, feedback ?? string.Empty));
            TempData["Success"] = "Change request sent — the team will upload a new proof.";
        }
        catch (ForbiddenException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Workspace), new { id });
    }

    // ----------------------------------------------------------------------------------
    // Design Review upload (customer side): document/file-based design submissions.
    // ----------------------------------------------------------------------------------

    [HttpPost("{id:guid}/workspace/designreview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadDesignReview(Guid id, IFormFile file, string? notes)
    {
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var order = await workspace.GetAsync(id, HttpContext.RequestAborted);
        if (order is null || order.CustomerId != customer.Id)
            throw new NotFoundException("Order", id);

        if (file is null || file.Length <= 0)
        {
            TempData["Error"] = "Please choose a design file to upload.";
            return RedirectToAction(nameof(Workspace), new { id });
        }

        using var stream = file.OpenReadStream();
        var uploaded = await storage.UploadAsync(new FileUploadRequest(
            stream, file.FileName, file.ContentType, file.Length, FileCategory.DesignReview));
        
        try
        {
            await Sender.Send(new UploadDesignReviewCommand(id, new UploadDesignReviewRequest(
                uploaded.StoredFileName, file.FileName, uploaded.Url, file.ContentType, uploaded.Size,
                string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                uploaded.PreviewUrl, uploaded.PreviewStoredFileName), "customer"));
            TempData["Success"] = "Design review uploaded — the team will review it.";
        }
        catch (ValidationAppException ex)
        {
            TempData["Error"] = ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "Design review could not be uploaded.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Workspace), new { id });
    }
}
