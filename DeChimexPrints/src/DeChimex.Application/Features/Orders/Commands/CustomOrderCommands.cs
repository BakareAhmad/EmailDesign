using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Orders.Commands;

// ------------------------------------------------------------------------------------
// Submit an order request (custom path entry). Creates the order in AwaitingReview with
// the option responses and files snapshotted. No invoice/payment yet - staff review first.
// ------------------------------------------------------------------------------------

public sealed record SubmitOrderRequestCommand(SubmitOrderRequest Request) : ICommand<OrderWorkspaceDto>;

public sealed class SubmitOrderRequestValidator : AbstractValidator<SubmitOrderRequestCommand>
{
    public SubmitOrderRequestValidator()
    {
        RuleFor(x => x.Request.ProductId).NotEmpty();
        RuleFor(x => x.Request.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
        RuleFor(x => x.Request.Notes).MaximumLength(2000);
        RuleForEach(x => x.Request.Responses).ChildRules(r =>
        {
            r.RuleFor(x => x.Question).NotEmpty().MaximumLength(300);
            r.RuleFor(x => x.Type).NotEmpty();
            r.RuleFor(x => x.Value).MaximumLength(10_000);
        });
    }
}

public sealed class SubmitOrderRequestHandler(
    IOrderRepository orders,
    IProductRepository products,
    ICategoryOptionsQueries categoryOptions,
    ICustomerRepository customers,
    ICurrentUser currentUser,
    IReferenceGenerator references,
    IOrderWorkspaceQueries workspace,
    ISalesAssignmentService salesAssignment,
    IUnitOfWork uow) : ICommandHandler<SubmitOrderRequestCommand, OrderWorkspaceDto>
{
    public async Task<OrderWorkspaceDto> Handle(SubmitOrderRequestCommand command, CancellationToken ct)
    {
        var r = command.Request;

        if (currentUser.UserId is null)
            throw new UnauthorizedAppException("Please sign in to submit an order request.");
        var customer = await customers.GetByUserIdAsync(currentUser.UserId.Value, ct)
            ?? throw new NotFoundException("A customer profile is required to submit an order request.");

        var product = await products.GetByIdAsync(r.ProductId, ct)
            ?? throw new NotFoundException("Product", r.ProductId);

        // Guard the custom path: it only exists when the product or its category is customizable.
        var options = product.CategoryId != Guid.Empty
            ? (await categoryOptions.GetForCategoryAsync(product.CategoryId, ct)).ToList()
            : new List<CategoryOptionDto>();
        if (!product.IsCustomizable && options.Count == 0)
            throw new ConflictException("order.custom.required",
                "This product is not configurable as a custom order.");

        // Authoritative required-answer enforcement (the browser check is advisory only).
        var missing = CustomOrderValidation.MissingRequiredQuestions(options, r.Responses);
        if (missing.Count > 0)
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["Required"] = missing.Select(q => $"Please answer: {q}").ToArray()
            });

        var orderNumber = await references.NextOrderNumberAsync(ct);
        var order = Order.CreateDraft(orderNumber, customer.Id, OrderKind.Custom);
        // Snapshot the stored catalog price onto the line (same as the cart path), so the
        // customer's order summary, the admin finalization prefill, totals and the payment
        // amount all carry the real product price. Staff can still reprice the line during
        // finalization (UpdateProductLine).
        order.AddProduct(product.Id, product.Name, product.Sku, product.SellingPrice.Amount, r.Quantity);
        order.SetCustomerNotes(r.Notes);

        foreach (var resp in r.Responses)
        {
            if (!Enum.TryParse<CategoryOptionType>(resp.Type, true, out var type))
                throw new ValidationAppException(new Dictionary<string, string[]>
                {
                    ["Type"] = new[] { $"Unknown option type '{resp.Type}'." }
                });
            order.AddResponse(resp.CategoryOptionId, resp.Question, type, resp.Value);
        }

        foreach (var f in r.Files)
            order.AddFile(f.StoredFileName, f.OriginalFileName, f.Url, f.ContentType,
                f.SizeBytes, "customer-upload", currentUser.UserId, "customer");

        order.AddTimelineEntry("submitted", "Order request submitted by the customer.",
            currentUser.UserId, customer.FullName);
        order.SubmitOrderRequest();

        // Automatic sales assignment (workload-based, server-side). Fails safe: with no
        // eligible sales staff the order stays unassigned and visible for manual assignment.
        var pick = await salesAssignment.PickForNewOrderAsync(ct);
        if (pick is not null)
        {
            order.AssignSalesStaff(pick.UserId, pick.Name);
            order.AddTimelineEntry("assigned",
                $"Automatically assigned to sales ({pick.Name}).", null, "System");
        }
        else
        {
            order.AddTimelineEntry("unassigned",
                "No eligible sales staff — awaiting manual assignment.", null, "System");
        }

        await orders.AddAsync(order, ct);
        await uow.SaveChangesAsync(ct);

        return await workspace.GetAsync(order.Id, ct)
            ?? throw new NotFoundException("Order", order.Id);
    }
}

// ------------------------------------------------------------------------------------
// Order chat: persist a message and broadcast to the order's SignalR group.
// ------------------------------------------------------------------------------------

public sealed record SendOrderMessageCommand(Guid OrderId, SendOrderMessageRequest Request, string AuthorRole) : ICommand<OrderMessageDto>;

public sealed class SendOrderMessageValidator : AbstractValidator<SendOrderMessageCommand>
{
    public SendOrderMessageValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        // Text is optional (media-only messages); "body or ≥1 attachment" is a
        // domain rule enforced by the handler, which sees both halves.
        RuleFor(x => x.Request.Body).MaximumLength(4000);
        RuleFor(x => x.AuthorRole).Must(r => r is "customer" or "staff")
            .WithMessage("Author role must be customer or staff.");
        RuleFor(x => x.Request.Attachments).Must(a => a is null || a.Count <= 6)
            .WithMessage("A message carries at most 6 attachments.");
        RuleForEach(x => x.Request.Attachments).ChildRules(a =>
        {
            a.RuleFor(x => x.Kind).Must(k => k is "image" or "video" or "audio" or "voice" or "file")
                .WithMessage("Unknown attachment kind.");
            a.RuleFor(x => x.StoredFileName).NotEmpty().MaximumLength(500);
            a.RuleFor(x => x.Url).NotEmpty().MaximumLength(1000);
            a.RuleFor(x => x.ContentType).MaximumLength(120);
            a.RuleFor(x => x.SizeBytes).GreaterThan(0);
            a.RuleFor(x => x.DurationSeconds).Must(d => d is null or >= 1 and <= 3600)
                .WithMessage("Invalid attachment duration.");
        });
    }
}

public sealed class SendOrderMessageHandler(
    IOrderRepository orders,
    ICustomerRepository customers,
    IUserRepository users,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IOrderPresenceTracker presence,
    IUnitOfWork uow) : ICommandHandler<SendOrderMessageCommand, OrderMessageDto>
{
    public async Task<OrderMessageDto> Handle(SendOrderMessageCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        // Staff messages are labelled with the author's username — the ONLY staff
        // identity customers may see. Never the name or email (StaffDisplayName
        // falls back to the generic "Staff" label, never internal identity).
        var authorName = command.AuthorRole == "customer"
            ? (await customers.GetByUserIdAsync(currentUser.UserId ?? Guid.Empty, ct))?.FullName ?? "Customer"
            : (await users.GetByIdAsync(currentUser.UserId ?? Guid.Empty, ct))?.StaffDisplayName ?? "Staff";

        var inputs = command.Request.Attachments;
        var message = order.AddMessage(currentUser.UserId, command.AuthorRole, authorName,
            command.Request.Body ?? string.Empty, command.Request.ReplyToMessageId,
            inputs?.Select(a => (a.Kind, a.OriginalFileName, a.StoredFileName, a.Url,
                a.ContentType, a.SizeBytes, a.DurationSeconds)).ToList());
        // Honest delivered call, made once at send time: the recipient's side has the
        // workspace open right now (it will receive the push below), or it does not
        // (delivery backfills when it next opens the workspace / fetches state).
        if (presence.IsRecipientConnected(order.Id, command.AuthorRole))
            message.MarkDelivered();
        order.AddTimelineEntry("message", $"{command.AuthorRole} sent a message.", currentUser.UserId, authorName);

        // Track who needs to act next (a no-op once the conversation is resolved).
        if (command.AuthorRole == "customer")
            order.MarkConversationWaitingForStaff();
        else
            order.MarkConversationWaitingForCustomer();

        // Natural workflow: first message automatically starts discussion.
        // This makes the explicit "Start Discussion" action redundant.
        // Only transition if this is the first message and order is in AwaitingReview.
        if (order.Messages.Count == 1 && order.Status == OrderStatus.AwaitingReview)
        {
            order.StartDiscussion();
            order.AddTimelineEntry("discussion", "Discussion started.", currentUser.UserId, authorName);
        }

        await uow.SaveChangesAsync(ct);

        var dto = ProjectMessage(order, message);

        await chat.PushMessageAsync(order.Id, new
        {
            id = dto.Id,
            orderId = order.Id,
            authorRole = dto.AuthorRole,
            authorName = dto.AuthorName,
            body = dto.Body,
            createdAtUtc = dto.CreatedDateUtc,
            replyToMessageId = dto.ReplyToMessageId,
            replyToAuthorName = dto.ReplyToAuthorName,
            replyToSnippet = dto.ReplyToSnippet,
            attachments = dto.Attachments!.Select(a => new
            {
                id = a.Id,
                kind = a.Kind,
                originalFileName = a.OriginalFileName,
                url = a.Url,
                contentType = a.ContentType,
                sizeBytes = a.SizeBytes,
                durationSeconds = a.DurationSeconds
            }),
            deliveredAtUtc = dto.DeliveredAtUtc
        }, ct);

        return dto;
    }

    private static OrderMessageDto ProjectMessage(Domain.Entities.Order order, Domain.Entities.OrderMessage message)
    {
        var parent = message.ReplyToMessageId is Guid replyId
            ? order.Messages.FirstOrDefault(m => m.Id == replyId)
            : null;
        return new OrderMessageDto(
            message.Id, message.AuthorRole, message.AuthorName, message.Body, message.CreatedDateUtc,
            message.ReplyToMessageId,
            parent?.AuthorName,
            parent is null ? null : OrderMessageDto.QuoteSnippet(parent.Body,
                parent.Attachments.Select(a => a.Kind).ToList()),
            message.Attachments.Select(a => new OrderMessageAttachmentDto(a.Id, a.Kind,
                a.OriginalFileName, a.Url, a.ContentType, a.SizeBytes, a.DurationSeconds)).ToList(),
            message.DeliveredAtUtc, message.ReadAtUtc);
    }
}

// ------------------------------------------------------------------------------------
// Chat delivery/read receipts. One batched command per beacon (not per message, and
// never per keystroke): the client reports the ids it actually rendered while the
// page was visible, and only newly-changed ids are pushed back to the author's side
// so ticks upgrade live without chatter.
// ------------------------------------------------------------------------------------

public sealed record MarkMessagesReadCommand(Guid OrderId, IReadOnlyList<Guid> MessageIds, string ReaderRole)
    : ICommand<IReadOnlyList<Guid>>;

public sealed class MarkMessagesReadValidator : AbstractValidator<MarkMessagesReadCommand>
{
    public MarkMessagesReadValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.MessageIds).NotNull().Must(ids => ids.Count is >= 1 and <= 200)
            .WithMessage("Report between 1 and 200 messages per beacon.");
        RuleFor(x => x.ReaderRole).Must(r => r is "customer" or "staff")
            .WithMessage("Reader role must be customer or staff.");
    }
}

public sealed class MarkMessagesReadHandler(
    IOrderRepository orders,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<MarkMessagesReadCommand, IReadOnlyList<Guid>>
{
    public async Task<IReadOnlyList<Guid>> Handle(MarkMessagesReadCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        // Only the other side's messages can be marked (a reader never "reads" their
        // own sends); unknown ids are ignored so beacons stay idempotent.
        var changed = order.Messages
            .Where(m => m.AuthorRole != command.ReaderRole && command.MessageIds.Contains(m.Id))
            .Where(m => m.MarkRead(currentUser.UserId))
            .Select(m => m.Id)
            .ToList();

        if (changed.Count == 0)
            return [];

        await uow.SaveChangesAsync(ct);
        await chat.PushOrderEventAsync(order.Id, "orderMessagesRead",
            new { ids = changed, readByRole = command.ReaderRole }, ct);
        return changed;
    }
}

public sealed record MarkMessagesDeliveredCommand(Guid OrderId, string ReaderRole)
    : ICommand<IReadOnlyList<Guid>>;

public sealed class MarkMessagesDeliveredHandler(
    IOrderRepository orders,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<MarkMessagesDeliveredCommand, IReadOnlyList<Guid>>
{
    public async Task<IReadOnlyList<Guid>> Handle(MarkMessagesDeliveredCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        var changed = order.Messages
            .Where(m => m.AuthorRole != command.ReaderRole)
            .Where(m => m.MarkDelivered())
            .Select(m => m.Id)
            .ToList();

        if (changed.Count == 0)
            return [];

        await uow.SaveChangesAsync(ct);
        await chat.PushOrderEventAsync(order.Id, "orderMessagesDelivered", new { ids = changed }, ct);
        return changed;
    }
}

// ------------------------------------------------------------------------------------
// Upload an order file (design reference / clarification). Both sides use this.
// ------------------------------------------------------------------------------------

public sealed record UploadOrderFileCommand(Guid OrderId, UploadOrderFileRequest Request, string UploadedByRole) : ICommand<OrderFileDto>;

public sealed class UploadOrderFileValidator : AbstractValidator<UploadOrderFileCommand>
{
    public UploadOrderFileValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Request.StoredFileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Request.Url).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.Request.ContentType).MaximumLength(120);
        RuleFor(x => x.Request.Purpose).MaximumLength(120);
        RuleFor(x => x.UploadedByRole).Must(r => r is "customer" or "staff")
            .WithMessage("Uploader role must be customer or staff.");
    }
}

public sealed class UploadOrderFileHandler(
    IOrderRepository orders,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<UploadOrderFileCommand, OrderFileDto>
{
    public async Task<OrderFileDto> Handle(UploadOrderFileCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        var file = order.AddFile(command.Request.StoredFileName, command.Request.OriginalFileName,
            command.Request.Url, command.Request.ContentType, command.Request.SizeBytes,
            command.Request.Purpose, currentUser.UserId, command.UploadedByRole);
        // The removal path already writes a timeline entry; the upload side mirrors it so
        // the activity history shows both halves of an attachment's life.
        order.AddTimelineEntry("file_uploaded", $"File '{file.OriginalFileName}' uploaded.",
            currentUser.UserId, command.UploadedByRole);

        await uow.SaveChangesAsync(ct);

        var dto = new OrderFileDto(file.Id, file.OriginalFileName, file.Url, file.ContentType,
            file.SizeBytes, file.Purpose, file.UploadedByRole, file.CreatedDateUtc);
        // Live: viewers with the workspace open see the attachment appear (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderFile", new { file = dto }, ct);
        return dto;
    }
}

// ------------------------------------------------------------------------------------
// Explicitly remove a file (the only deletion path — files are never silently dropped).
// ------------------------------------------------------------------------------------

public sealed record DeleteOrderFileCommand(Guid OrderId, Guid FileId, string ActorRole) : ICommand<Unit>;

public sealed class DeleteOrderFileValidator : AbstractValidator<DeleteOrderFileCommand>
{
    public DeleteOrderFileValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.FileId).NotEmpty();
        RuleFor(x => x.ActorRole).Must(r => r is "customer" or "staff")
            .WithMessage("Actor role must be customer or staff.");
    }
}

public sealed class DeleteOrderFileHandler(
    IOrderRepository orders,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<DeleteOrderFileCommand, Unit>
{
    public async Task<Unit> Handle(DeleteOrderFileCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        order.RemoveFile(command.FileId);
        order.AddTimelineEntry("file_removed", $"{command.ActorRole} removed a file.",
            currentUser.UserId, command.ActorRole);
        await uow.SaveChangesAsync(ct);

        // Live: viewers with the workspace open see the row vanish (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderFileRemoved", new { fileId = command.FileId }, ct);
        return Unit.Value;
    }
}

// ------------------------------------------------------------------------------------
// Staff design-proof upload (a new version supersedes the pending one).
// ------------------------------------------------------------------------------------

public sealed record UploadDesignProofCommand(Guid OrderId, UploadDesignProofRequest Request) : ICommand<OrderDesignProofDto>;

public sealed class UploadDesignProofValidator : AbstractValidator<UploadDesignProofCommand>
{
    public UploadDesignProofValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Request.StoredFileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Request.Url).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.Request.StaffNotes).MaximumLength(1000);
    }
}

public sealed class UploadDesignProofHandler(
    IOrderRepository orders,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<UploadDesignProofCommand, OrderDesignProofDto>
{
    public async Task<OrderDesignProofDto> Handle(UploadDesignProofCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        var proof = order.AddProof(command.Request.StoredFileName, command.Request.OriginalFileName,
            command.Request.Url, command.Request.ContentType, command.Request.StaffNotes);

        // A fresh proof returns the order to "awaiting approval" once the requirements are
        // confirmed (or under discussion). A finalized-but-unpaid order (PendingPayment)
        // reopens design approval the same way — the design must be approved before the
        // customer pays (§82 lifecycle). While still under initial review, the proof is
        // simply attached — staff confirm the requirements separately.
        if (order.Status is OrderStatus.InDiscussion or OrderStatus.RequirementsConfirmed
            or OrderStatus.PendingPayment)
            order.AwaitDesignApproval();

        order.MarkConversationWaitingForCustomer(); // staff acted: customer must approve
        order.AddTimelineEntry("proof-uploaded", $"Design proof v{proof.Version} uploaded.",
            currentUser.UserId, currentUser.Email);
        await uow.SaveChangesAsync(ct);

        var dto = new OrderDesignProofDto(proof.Id, proof.Version, proof.Url, proof.OriginalFileName,
            proof.ContentType, proof.Status.ToString(), proof.StaffNotes, proof.CustomerFeedback,
            proof.ApprovedAtUtc, proof.CreatedDateUtc);
        // Live: the customer sees the new proof appear without reloading (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderProof", new { proof = dto }, ct);
        return dto;
    }
}

// ------------------------------------------------------------------------------------
// Customer approves a design proof (or requests changes).
// ------------------------------------------------------------------------------------

public sealed record ApproveDesignProofCommand(Guid OrderId, Guid ProofId) : ICommand<Unit>;

public sealed class ApproveDesignProofValidator : AbstractValidator<ApproveDesignProofCommand>
{
    public ApproveDesignProofValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ProofId).NotEmpty();
    }
}

public sealed class ApproveDesignProofHandler(
    IOrderRepository orders,
    ICustomerRepository customers,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<ApproveDesignProofCommand, Unit>
{
    public async Task<Unit> Handle(ApproveDesignProofCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        // Ownership: the customer can only act on their own order.
        if (currentUser.UserId is null ||
            (await customers.GetByUserIdAsync(currentUser.UserId.Value, ct))?.Id != order.CustomerId)
            throw new ForbiddenException("You can only approve designs on your own orders.");

        var proof = order.ApproveProof(command.ProofId, currentUser.UserId.Value);
        order.MarkConversationWaitingForStaff(); // customer acted: staff finalize next
        order.AddTimelineEntry("proof-approved", $"Design proof v{proof.Version} approved by the customer.",
            currentUser.UserId, "customer");
        await uow.SaveChangesAsync(ct);

        // Live: staff watching the workspace see the approval land (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderProofStatus",
            new { proofId = proof.Id, version = proof.Version, status = proof.Status.ToString(), approvedAtUtc = proof.ApprovedAtUtc }, ct);
        return Unit.Value;
    }
}

public sealed record RequestDesignChangesCommand(Guid OrderId, Guid ProofId, string Feedback) : ICommand<Unit>;

public sealed class RequestDesignChangesValidator : AbstractValidator<RequestDesignChangesCommand>
{
    public RequestDesignChangesValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ProofId).NotEmpty();
        RuleFor(x => x.Feedback).NotEmpty().MaximumLength(2000);
    }
}

public sealed class RequestDesignChangesHandler(
    IOrderRepository orders,
    ICustomerRepository customers,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<RequestDesignChangesCommand, Unit>
{
    public async Task<Unit> Handle(RequestDesignChangesCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        if (currentUser.UserId is null ||
            (await customers.GetByUserIdAsync(currentUser.UserId.Value, ct))?.Id != order.CustomerId)
            throw new ForbiddenException("You can only review designs on your own orders.");

        var proof = order.RequestProofChanges(command.ProofId, command.Feedback);
        order.MarkConversationWaitingForStaff(); // customer acted: staff must revise
        order.AddTimelineEntry("proof-changes-requested",
            $"Customer requested changes on design proof v{proof.Version}.",
            currentUser.UserId, "customer");
        await uow.SaveChangesAsync(ct);

        // Live: staff watching the workspace see the change request land (best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderProofStatus",
            new { proofId = proof.Id, version = proof.Version, status = proof.Status.ToString(), feedback = proof.CustomerFeedback }, ct);
        return Unit.Value;
    }
}

// ------------------------------------------------------------------------------------
// Staff finalize requirements + pricing -> order enters payment (existing Paystack flow).
// ------------------------------------------------------------------------------------

public sealed record FinalizeOrderCommand(Guid OrderId, FinalizeOrderRequest Request) : ICommand<Unit>;

public sealed class FinalizeOrderValidator : AbstractValidator<FinalizeOrderCommand>
{
    public FinalizeOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Request.Discount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.DeliveryFee).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.Tax).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.CustomerNotes).MaximumLength(2000);
        RuleForEach(x => x.Request.ProductLines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotEmpty();
            l.RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        });
        RuleForEach(x => x.Request.CustomLines).ChildRules(l =>
        {
            l.RuleFor(x => x.Description).NotEmpty().MaximumLength(250);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
        });
    }
}

public sealed class FinalizeOrderHandler(
    IOrderRepository orders,
    ICurrentUser currentUser,
    IOrderChatPusher chat,
    IUnitOfWork uow) : ICommandHandler<FinalizeOrderCommand, Unit>
{
    public async Task<Unit> Handle(FinalizeOrderCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithWorkspaceAsync(command.OrderId, ct)
            ?? throw new NotFoundException("Order", command.OrderId);

        foreach (var line in command.Request.ProductLines)
            order.UpdateProductLine(line.ProductId, line.Quantity, line.UnitPrice);

        order.SetCustomLines(command.Request.CustomLines.Select(l => (l.Description, l.UnitPrice, l.Quantity)).ToList());
        order.SetPricing(command.Request.Discount, command.Request.DeliveryFee, command.Request.Tax);
        order.SetCustomerNotes(command.Request.CustomerNotes);

        // Finalizing implicitly confirms requirements (staff have reviewed and priced everything).
        // This ensures requirements are confirmed before the order moves to payment.
        // If already confirmed, ConfirmRequirements is a no-op due to domain validation.
        if (order.Status != OrderStatus.RequirementsConfirmed)
        {
            try
            {
                order.ConfirmRequirements();
                order.AddTimelineEntry("confirmed", "Requirements confirmed.", currentUser.UserId, currentUser.Email);
            }
            catch (BusinessRuleValidationException)
            {
                // Already in a valid state for finalization (e.g., RequirementsConfirmed)
            }
        }

        order.AddTimelineEntry("finalized",
            $"Requirements and pricing finalized (total {order.GrandTotal.Amount:N2} {order.GrandTotal.Currency}).",
            currentUser.UserId, currentUser.Email);
        order.FinalizeOrder();

        await uow.SaveChangesAsync(ct);

        // Live: the customer sees the quote land (totals refresh from state, best-effort).
        await chat.PushOrderEventAsync(order.Id, "orderStatus", new
        {
            status = order.Status.ToString(),
            timelineType = "finalized",
            timelineDescription = $"Requirements and pricing finalized (total {order.GrandTotal.Amount:N2} {order.GrandTotal.Currency})."
        }, ct);
        return Unit.Value;
    }
}


