using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Orders.Queries;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Staff side of the custom-order workspace: the full structured request (responses, files,
/// timeline), the real-time chat, design-proof upload and requirements/pricing finalization.
/// Read access is orders.view; the mutating actions (proof, finalize, transitions) require
/// orders.updatestatus, matching the plan's permission map.
/// </summary>
[Authorize(Policy = Permissions.Orders.View)]
[Route("admin/orders/{orderId:guid}/workspace")]
public sealed class AdminOrderWorkspaceController(
    IOrderWorkspaceQueries workspace,
    IFileStorageService storage,
    IPaymentRepository payments,
    IInvoiceQueries invoices) : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(Guid orderId)
    {
        var order = await workspace.GetAsync(orderId, HttpContext.RequestAborted);
        if (order is null) return NotFound();

        // Staff fetched the thread, so their received messages are delivered
        // (idempotent; pushes only when something actually changed).
        await Sender.Send(new MarkMessagesDeliveredCommand(orderId, "staff"), HttpContext.RequestAborted);

        ViewData["Title"] = $"{order.OrderNumber} · Workspace";
        ViewData["Heading"] = "Orders";
        ViewData["ActiveNav"] = "orders";
        ViewBag.CanFinalize = order.Status is "RequirementsConfirmed" or "Draft" or "AwaitingReview" or "InDiscussion";
        var succeeded = (await payments.GetByOrderIdAsync(orderId, HttpContext.RequestAborted))
            .OrderByDescending(p => p.PaidAtUtc ?? p.CreatedDateUtc)
            .FirstOrDefault(p => p.Status == PaymentStatus.Succeeded);
        ViewBag.Payment = succeeded;
        ViewBag.InvoiceDocument = await invoices.GetDocumentByOrderIdAsync(orderId, HttpContext.RequestAborted);
        ViewBag.ReceiptDocument = await invoices.GetReceiptByOrderIdAsync(orderId, HttpContext.RequestAborted);
        ViewBag.CanReassign = User.HasClaim(Permissions.ClaimType, Permissions.Users.AssignRoles);
        ViewBag.EligibleSales = ViewBag.CanReassign
            ? (await Sender.Send(new GetEligibleSalesStaffQuery()))
                .Select(s => new SelectListItem($"{s.Name} ({s.ActiveWorkload} active)", s.UserId.ToString()))
                .ToList()
            : new List<SelectListItem>();
        return View(order);
    }

    /// <summary>
    /// Live-state feed for the open staff workspace: the same projection as
    /// <see cref="Index"/>, as JSON (staff see the unredacted workspace). Used for
    /// reconnect resync and structural pushes; the client patches cards in place.
    /// </summary>
    [HttpGet("state")]
    public async Task<IActionResult> WorkspaceState(Guid orderId)
    {
        var order = await workspace.GetAsync(orderId, HttpContext.RequestAborted);
        if (order is null) return NotFound();

        await Sender.Send(new MarkMessagesDeliveredCommand(orderId, "staff"), HttpContext.RequestAborted);

        // Same projection as the view, plus the payment-card inputs.
        var succeeded = (await payments.GetByOrderIdAsync(orderId, HttpContext.RequestAborted))
            .OrderByDescending(p => p.PaidAtUtc ?? p.CreatedDateUtc)
            .FirstOrDefault(p => p.Status == PaymentStatus.Succeeded);
        var invoice = await invoices.GetDocumentByOrderIdAsync(orderId, HttpContext.RequestAborted);
        var receipt = await invoices.GetReceiptByOrderIdAsync(orderId, HttpContext.RequestAborted);
        return Json(new
        {
            workspace = order,
            payment = succeeded is null ? null : new
            {
                status = succeeded.Status.ToString(),
                amount = succeeded.Amount.Amount,
                reference = succeeded.Reference
            },
            invoiceToken = invoice?.ShareToken,
            receiptToken = receipt?.ShareToken
        });
    }

    [HttpPost("assign")]
    [Authorize(Policy = Permissions.Users.AssignRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reassign(Guid orderId, Guid salesUserId)
    {
        try
        {
            await Sender.Send(new ReassignSalesCommand(orderId, salesUserId));
            TempData["Success"] = "Sales assignment updated — the new owner has been notified.";
        }
        catch (ForbiddenException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index), new { orderId });
    }

    [HttpPost("message")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(Guid orderId, string? body, Guid? replyToMessageId,
        List<IFormFile>? files, List<string>? kinds, List<string>? durations)
    {
        // Fetch submits get the persisted message as JSON (rendered in place, no
        // reload); plain form posts keep the PRG redirect for no-JS clients.
        var wantsJson = ChatSendSupport.WantsJson(Request);
        try
        {
            var attachments = await ChatSendSupport.UploadAttachmentsAsync(
                storage, files, kinds, durations, HttpContext.RequestAborted);
            var dto = await Sender.Send(new SendOrderMessageCommand(orderId,
                new SendOrderMessageRequest(body ?? string.Empty, replyToMessageId, attachments), "staff"));
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
        return RedirectToAction(nameof(Index), new { orderId });
    }

    /// <summary>Batched read beacon: ids the staff client actually rendered while
    /// the page was visible. Unknown/own ids are ignored (idempotent).</summary>
    [HttpPost("messages/mark-read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid orderId, [FromBody] ChatMarkReadRequest? body)
    {
        var order = await workspace.GetAsync(orderId, HttpContext.RequestAborted);
        if (order is null) return NotFound();

        var read = body?.Ids is { Count: > 0 }
            ? await Sender.Send(new MarkMessagesReadCommand(orderId, body.Ids, "staff"))
            : (IReadOnlyList<Guid>)[];
        return Json(new { read });
    }

    [HttpPost("files")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadFile(Guid orderId, IFormFile file, string? purpose)
    {
        if (file is not null && file.Length > 0)
        {
            using var stream = file.OpenReadStream();
            var uploaded = await storage.UploadAsync(new FileUploadRequest(
                stream, file.FileName, file.ContentType, file.Length, FileCategory.OrderAttachment));
            await Sender.Send(new UploadOrderFileCommand(orderId, new UploadOrderFileRequest(
                uploaded.StoredFileName, file.FileName, uploaded.Url, file.ContentType, uploaded.Size,
                string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim()), "staff"));
        }
        return RedirectToAction(nameof(Index), new { orderId });
    }

    [HttpPost("files/{fileId:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFile(Guid orderId, Guid fileId)
    {
        await Sender.Send(new DeleteOrderFileCommand(orderId, fileId, "staff"));
        TempData["Success"] = "File removed.";
        return RedirectToAction(nameof(Index), new { orderId });
    }

    [HttpPost("proof")]
    [Authorize(Policy = Permissions.Orders.UpdateStatus)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadProof(Guid orderId, IFormFile file, string? staffNotes)
    {
        if (file is null || file.Length <= 0)
        {
            TempData["Error"] = "Please choose a proof image to upload.";
            return RedirectToAction(nameof(Index), new { orderId });
        }

        using var stream = file.OpenReadStream();
        var uploaded = await storage.UploadAsync(new FileUploadRequest(
            stream, file.FileName, file.ContentType, file.Length, FileCategory.DesignProof));
        await Sender.Send(new UploadDesignProofCommand(orderId, new UploadDesignProofRequest(
            uploaded.StoredFileName, file.FileName, uploaded.Url, file.ContentType,
            string.IsNullOrWhiteSpace(staffNotes) ? null : staffNotes.Trim())));
        TempData["Success"] = "Design proof uploaded — the customer has been notified.";
        return RedirectToAction(nameof(Index), new { orderId });
    }

    /// <summary>
    /// Uploads a design review file (document-based, not chat media). Design reviews maintain
    /// independent history and have their own approval workflow, separate from design proofs.
    /// </summary>
    [HttpPost("designreview")]
    [Authorize(Policy = Permissions.Orders.UpdateStatus)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadDesignReview(Guid orderId, IFormFile file, string? notes)
    {
        if (file is null || file.Length <= 0)
        {
            TempData["Error"] = "Please choose a design file to upload.";
            return RedirectToAction(nameof(Index), new { orderId });
        }

        using var stream = file.OpenReadStream();
        var uploaded = await storage.UploadAsync(new FileUploadRequest(
            stream, file.FileName, file.ContentType, file.Length, FileCategory.DesignReview));
        
        await Sender.Send(new UploadDesignReviewCommand(orderId, new UploadDesignReviewRequest(
            uploaded.StoredFileName, file.FileName, uploaded.Url, file.ContentType, uploaded.Size,
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            uploaded.PreviewUrl, uploaded.PreviewStoredFileName), "staff"));
        
        TempData["Success"] = "Design review uploaded — the customer has been notified.";
        return RedirectToAction(nameof(Index), new { orderId });
    }

    [HttpPost("designreview/{designReviewId:guid}/approve")]
    [Authorize(Policy = Permissions.Orders.UpdateStatus)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveDesignReview(Guid orderId, Guid designReviewId, string? approvalNotes)
    {
        try
        {
            await Sender.Send(new ApproveDesignReviewCommand(
                orderId, designReviewId, 
                new ApproveDesignReviewRequest(string.IsNullOrWhiteSpace(approvalNotes) ? null : approvalNotes.Trim())));
            TempData["Success"] = "Design review approved.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index), new { orderId });
    }

    [HttpPost("designreview/{designReviewId:guid}/reject")]
    [Authorize(Policy = Permissions.Orders.UpdateStatus)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectDesignReview(Guid orderId, Guid designReviewId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Please provide a reason for rejection.";
            return RedirectToAction(nameof(Index), new { orderId });
        }
        
        try
        {
            await Sender.Send(new RejectDesignReviewCommand(
                orderId, designReviewId,
                new RejectDesignReviewRequest(reason.Trim())));
            TempData["Success"] = "Design review rejected.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index), new { orderId });
    }

    [HttpPost("finalize")]
    [Authorize(Policy = Permissions.Orders.UpdateStatus)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Finalize(Guid orderId, IFormCollection form)
    {
        var order = await workspace.GetAsync(orderId, HttpContext.RequestAborted);
        if (order is null) return NotFound();

        // Product lines: form keys line_{productId}_qty and line_{productId}_price.
        var productLines = new List<OrderLinePricingInput>();
        foreach (var item in order.Items)
        {
            var qty = ParseInt(form[$"line_{item.ProductId}_qty"], item.Quantity);
            var price = ParseDecimal(form[$"line_{item.ProductId}_price"], item.UnitPrice);
            productLines.Add(new OrderLinePricingInput(item.ProductId, qty, price));
        }

        // Ad-hoc charge lines: repeated sets of custom_desc / custom_price / custom_qty.
        var descriptions = form["custom_desc"];
        var prices = form["custom_price"];
        var quantities = form["custom_qty"];
        var customLines = new List<OrderCustomLineInput>();
        for (var i = 0; i < descriptions.Count; i++)
        {
            var desc = descriptions[i]?.Trim();
            if (string.IsNullOrWhiteSpace(desc)) continue;
            var price = ParseDecimal(prices.Count > i ? prices[i] : null, 0m);
            var qty = ParseInt(quantities.Count > i ? quantities[i] : null, 1);
            customLines.Add(new OrderCustomLineInput(desc, price, qty));
        }

        var discount = ParseDecimal(form["discount"], order.Discount);
        var deliveryFee = ParseDecimal(form["deliveryFee"], order.DeliveryFee);
        var tax = ParseDecimal(form["tax"], order.Tax);
        var notes = form["customerNotes"].FirstOrDefault();

        try
        {
            await Sender.Send(new FinalizeOrderCommand(orderId, new FinalizeOrderRequest(
                productLines, customLines, discount, deliveryFee, tax,
                string.IsNullOrWhiteSpace(notes) ? null : notes.Trim())));
            TempData["Success"] = "Order finalized — the customer can now review the quote and pay.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (ValidationAppException ex)
        {
            TempData["Error"] = ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "Please check the pricing lines.";
        }
        return RedirectToAction(nameof(Index), new { orderId });
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var n) && n >= 0 ? n : fallback;

    private static decimal ParseDecimal(string? value, decimal fallback)
        => decimal.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : fallback;
}
