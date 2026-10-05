using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Order aggregate. The transactional core of sales. Encapsulates product items,
/// service lines, totals and the full lifecycle state machine.
///
/// Key business rules (from the specification):
///  - Orders can ALWAYS be created, even when stock is unavailable (no stock gate).
///  - Inventory is deducted ONLY after payment is confirmed (see MarkPaid + fulfilment).
///  - Service-only orders never affect inventory.
///  - Mixed orders deduct inventory only for their product items.
///  - Order type is derived automatically from the lines present.
/// </summary>
public sealed class Order : AggregateRoot
{
    private readonly List<OrderItem> _items = new();
    private readonly List<OrderServiceLine> _services = new();
    private readonly List<OrderRequirementResponse> _responses = new();
    private readonly List<OrderFile> _files = new();
    private readonly List<OrderMessage> _messages = new();
    private readonly List<OrderTimelineEntry> _timeline = new();
    private readonly List<OrderDesignProof> _proofs = new();
    private readonly List<OrderCustomLine> _customLines = new();

    private Order() { } // EF

    private Order(string orderNumber, Guid customerId)
    {
        OrderNumber = orderNumber;
        CustomerId = customerId;
        Status = OrderStatus.Draft;
        DiscountAmount = Money.Zero();
        DeliveryFee = Money.Zero();
        TaxAmount = Money.Zero();
        RequiresProcurement = false;
    }

    public string OrderNumber { get; private set; } = default!;

    /// <summary>Persistence-only escape hatch (AUD-004): when the unique OrderNumber index
    /// rejects a count-generated number under concurrency, the save boundary renumbers the
    /// conflicting aggregate with a fresh random suffix and retries — checkout never fails
    /// on a number collision. Never call during normal business flow.</summary>
    public void RenumberAfterReferenceConflict(string newOrderNumber) => OrderNumber = newOrderNumber;

    public Guid CustomerId { get; private set; }
    public Customer? Customer { get; private set; }
    public OrderStatus Status { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token (AUD-002). The database (SQL Server rowversion)
    /// updates it on every write, so two concurrent confirmations of the same order
    /// (Paystack webhook + browser return-verify) cannot both commit their post-payment
    /// changes silently — the loser gets a DbUpdateConcurrencyException and re-checks
    /// idempotency. The domain never mutates it.
    /// </summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    /// <summary>
    /// Direct purchase vs custom sales workflow — fixed at creation (see OrderKind).
    /// Not a status: both paths converge on PendingPayment and share every later state.
    /// </summary>
    public OrderKind Kind { get; private set; }

    public Money DiscountAmount { get; private set; } = default!;
    public Money DeliveryFee { get; private set; } = default!;
    public Money TaxAmount { get; private set; } = default!;

    public Address? ShippingAddress { get; private set; }
    public Address? BillingAddress { get; private set; }
    public string? CustomerNotes { get; private set; }
    public bool RequiresProcurement { get; private set; }
    public DateTime? PaidAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    /// <summary>
    /// The sales staff member currently responsible for this order (per-order ownership;
    /// a customer is never permanently tied to one employee). Null = unassigned.
    /// </summary>
    public Guid? AssignedSalesUserId { get; private set; }

    /// <summary>Denormalized display name of the assigned sales staff (see AssignedSalesUserId).</summary>
    public string? AssignedSalesName { get; private set; }

    /// <summary>
    /// Who-needs-to-act-next state of the sales conversation. Orthogonal to
    /// <see cref="Status"/>: it tracks communication, never the business lifecycle.
    /// </summary>
    public OrderConversationState ConversationState { get; private set; } = OrderConversationState.Active;

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();
    public IReadOnlyCollection<OrderServiceLine> Services => _services.AsReadOnly();

    // Custom-order workflow children (see CUSTOM_ORDER_FLOW_PLAN.md §4).
    public IReadOnlyCollection<OrderRequirementResponse> Responses => _responses.AsReadOnly();
    public IReadOnlyCollection<OrderFile> Files => _files.AsReadOnly();
    public IReadOnlyCollection<OrderMessage> Messages => _messages.AsReadOnly();
    public IReadOnlyCollection<OrderTimelineEntry> Timeline => _timeline.AsReadOnly();
    public IReadOnlyCollection<OrderDesignProof> Proofs => _proofs.AsReadOnly();
    public IReadOnlyCollection<OrderCustomLine> CustomLines => _customLines.AsReadOnly();

    // Design Review workflow: customer/staff can upload design files for review.
    // Unlike DesignProofs (staff-only, superseding), DesignReviews maintain independent
    // history with their own approval/rejection workflow.
    private readonly List<OrderDesignReview> _designReviews = new();
    public IReadOnlyCollection<OrderDesignReview> DesignReviews => _designReviews.AsReadOnly();

    public OrderType Type
    {
        get
        {
            var hasProducts = _items.Count > 0;
            var hasServices = _services.Count > 0;
            if (hasProducts && hasServices) return OrderType.Mixed;
            return hasServices ? OrderType.Service : OrderType.Inventory;
        }
    }

    public Money ItemsSubtotal =>
        _items.Aggregate(Money.Zero(), (sum, i) => sum.Add(i.LineTotal))
              .Add(_services.Aggregate(Money.Zero(), (sum, s) => sum.Add(s.LineTotal)))
              .Add(_customLines.Aggregate(Money.Zero(), (sum, c) => sum.Add(c.LineTotal)));

    public Money GrandTotal
    {
        get
        {
            var total = ItemsSubtotal.Add(DeliveryFee).Add(TaxAmount);
            return DiscountAmount.Amount >= total.Amount ? Money.Zero() : total.Subtract(DiscountAmount);
        }
    }

    public static Order CreateDraft(string orderNumber, Guid customerId, OrderKind kind = OrderKind.Direct)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
            throw new BusinessRuleValidationException("order.number", "Order number is required.");
        if (customerId == Guid.Empty)
            throw new BusinessRuleValidationException("order.customer", "A valid customer is required.");
        return new Order(orderNumber, customerId) { Kind = kind };
    }

    private void EnsureMutable()
    {
        // Lines, responses, files and pricing can change only while the order is still
        // being assembled or negotiated. Paid and later states are immutable. A proof
        // can be replaced while it is still awaiting the customer's approval.
        if (Status is not (OrderStatus.Draft or OrderStatus.PendingPayment
            or OrderStatus.AwaitingReview or OrderStatus.InDiscussion or OrderStatus.RequirementsConfirmed
            or OrderStatus.AwaitingDesignApproval))
            throw new BusinessRuleValidationException("order.immutable", "Order can no longer be modified.");
    }

    public OrderItem AddProduct(Guid productId, string productName, string sku, decimal unitPrice, int quantity)
    {
        EnsureMutable();
        var existing = _items.FirstOrDefault(i => i.ProductId == productId);
        if (existing is not null)
        {
            _items.Remove(existing);
            quantity += existing.Quantity;
        }
        var item = OrderItem.Create(Id, productId, productName, sku, Money.Of(unitPrice), quantity);
        _items.Add(item);
        return item;
    }

    public OrderServiceLine AddService(Guid serviceId, string serviceName, decimal unitPrice, int quantity, string? notes)
    {
        EnsureMutable();
        var line = OrderServiceLine.Create(Id, serviceId, serviceName, Money.Of(unitPrice), quantity, notes);
        _services.Add(line);
        return line;
    }

    public void SetPricing(decimal discount, decimal deliveryFee, decimal tax)
    {
        DiscountAmount = Money.Of(discount);
        DeliveryFee = Money.Of(deliveryFee);
        TaxAmount = Money.Of(tax);
    }

    public void SetAddresses(Address? shipping, Address? billing) { ShippingAddress = shipping; BillingAddress = billing; }
    public void SetCustomerNotes(string? notes) => CustomerNotes = notes?.Trim();

    /// <summary>
    /// True while the order represents live sales work: the pre-payment review states
    /// where a sales conversation is active. Finalized (PendingPayment+), cancelled,
    /// refunded and historical orders are NOT active sales work. Single source of truth
    /// for the workload calculation (read-side queries mirror this set in LINQ).
    /// </summary>
    public static bool IsActiveSalesWork(OrderStatus status) => status is
        OrderStatus.AwaitingReview or OrderStatus.InDiscussion or
        OrderStatus.RequirementsConfirmed or OrderStatus.AwaitingDesignApproval;

    /// <summary>
    /// Assigns (or reassigns) the responsible sales staff member. Raises
    /// <see cref="SalesAssignedEvent"/> so the new owner is notified; the caller
    /// records the human-readable history via <see cref="AddTimelineEntry"/>.
    /// </summary>
    public void AssignSalesStaff(Guid salesUserId, string salesName, Guid? actorUserId = null, string? actorName = null)
    {
        if (salesUserId == Guid.Empty)
            throw new BusinessRuleValidationException("order.sales.user", "A valid sales staff member is required.");
        if (string.IsNullOrWhiteSpace(salesName))
            throw new BusinessRuleValidationException("order.sales.name", "The sales staff name is required.");
        var previous = AssignedSalesUserId;
        AssignedSalesUserId = salesUserId;
        AssignedSalesName = salesName.Trim();
        RaiseDomainEvent(new SalesAssignedEvent(Id, OrderNumber, previous, salesUserId,
            AssignedSalesName, actorUserId, actorName, previous.HasValue && previous.Value != salesUserId));
    }

    /// <summary>Staff replied: the next action is expected from the customer. No-op once resolved.</summary>
    public void MarkConversationWaitingForCustomer()
    {
        if (ConversationState == OrderConversationState.Resolved) return;
        ConversationState = OrderConversationState.WaitingForCustomer;
    }

    /// <summary>Customer replied: the sales team needs to act. No-op once resolved.</summary>
    public void MarkConversationWaitingForStaff()
    {
        if (ConversationState == OrderConversationState.Resolved) return;
        ConversationState = OrderConversationState.WaitingForStaff;
    }

    /// <summary>
    /// Closes the sales conversation as done (sales work complete). Messages and the
    /// timeline are retained as historical context; messaging stays possible for
    /// follow-ups but no longer flips the state.
    /// </summary>
    public void ResolveConversation() => ConversationState = OrderConversationState.Resolved;

    // ----------------------------------------------------------------------------------
    // Custom-order workflow (order request). See CUSTOM_ORDER_FLOW_PLAN.md §5.
    // ----------------------------------------------------------------------------------

    /// <summary>Replaces the order's ad-hoc charge lines (staff-composed at finalization).</summary>
    public void SetCustomLines(IReadOnlyList<(string Description, decimal UnitPrice, int Quantity)> lines)
    {
        EnsureMutable();
        _customLines.Clear();
        foreach (var (description, unitPrice, quantity) in lines)
            _customLines.Add(OrderCustomLine.Create(Id, description, unitPrice, quantity));
    }

    /// <summary>Adjusts a product line's quantity and unit price during finalization.</summary>
    public void UpdateProductLine(Guid productId, int quantity, decimal unitPrice)
    {
        EnsureMutable();
        var item = _items.FirstOrDefault(i => i.ProductId == productId)
            ?? throw new BusinessRuleValidationException("order.line", "Product line not found on this order.");
        item.SetPricing(unitPrice);
        item.SetQuantity(quantity);
    }

    /// <summary>Snapshots a customer's answer to a category option.</summary>
    public void AddResponse(Guid? categoryOptionId, string question, CategoryOptionType type, string value)
    {
        EnsureMutable();
        _responses.Add(OrderRequirementResponse.Create(Id, categoryOptionId, question, type, value, _responses.Count));
    }

    /// <summary>Attaches a file (design/reference/clarification) to the order.</summary>
    public OrderFile AddFile(string storedFileName, string originalFileName, string url,
        string contentType, long sizeBytes, string? purpose, Guid? uploadedByUserId, string uploadedByRole)
    {
        EnsureMutable();
        var file = OrderFile.Create(Id, storedFileName, originalFileName, url, contentType,
            sizeBytes, purpose, uploadedByUserId, uploadedByRole);
        _files.Add(file);
        RaiseDomainEvent(new OrderFileAddedEvent(Id, OrderNumber, file.Url, file.Purpose, uploadedByRole));
        return file;
    }

    /// <summary>Explicitly removes a file (the only deletion path — files are never silently dropped).</summary>
    public void RemoveFile(Guid fileId)
    {
        EnsureMutable();
        var file = _files.FirstOrDefault(f => f.Id == fileId);
        if (file is not null) _files.Remove(file);
    }

    /// <summary>Adds a message to the order's contextual conversation.</summary>
    public OrderMessage AddMessage(Guid? authorUserId, string authorRole, string authorName, string body)
        => AddMessage(authorUserId, authorRole, authorName, body, null, null);

    /// <summary>Adds a message quoting <paramref name="replyToMessageId"/> and/or
    /// carrying chat-media attachments. The quoted message must belong to this order;
    /// cross-order quotes are rejected rather than silently dropped.</summary>
    public OrderMessage AddMessage(Guid? authorUserId, string authorRole, string authorName, string body,
        Guid? replyToMessageId,
        IReadOnlyList<(string Kind, string OriginalFileName, string StoredFileName, string Url,
            string ContentType, long SizeBytes, int? DurationSeconds)>? attachments)
    {
        if (Status is OrderStatus.Cancelled or OrderStatus.Refunded)
            throw new BusinessRuleValidationException("order.message.closed", "This order is closed.");
        if (replyToMessageId is Guid replyId && _messages.All(m => m.Id != replyId))
            throw new BusinessRuleValidationException("order.message.reply", "The quoted message is not part of this order.");
        if (string.IsNullOrWhiteSpace(body) && (attachments is null || attachments.Count == 0))
            throw new BusinessRuleValidationException("order.message.body", "The message cannot be empty.");
        var message = OrderMessage.Create(Id, authorUserId, authorRole, authorName, body, replyToMessageId);
        if (attachments is not null)
            foreach (var a in attachments)
                message.Attach(a.Kind, a.OriginalFileName, a.StoredFileName, a.Url, a.ContentType, a.SizeBytes, a.DurationSeconds);
        _messages.Add(message);
        RaiseDomainEvent(new OrderMessageAddedEvent(Id, OrderNumber, authorUserId, authorRole, authorName, body));
        return message;
    }

    /// <summary>Records an entry in the order's timeline (all stages).</summary>
    public void AddTimelineEntry(string type, string description, Guid? actorUserId = null, string? actorName = null)
    {
        _timeline.Add(OrderTimelineEntry.Create(Id, type, description, actorUserId, actorName));
    }

    /// <summary>Uploads a new design proof version; previous pending/approved proofs are superseded.</summary>
    public OrderDesignProof AddProof(string storedFileName, string originalFileName, string url,
        string contentType, string? staffNotes)
    {
        EnsureMutable();
        foreach (var proof in _proofs) proof.Supersede();
        var p = OrderDesignProof.Create(Id, storedFileName, originalFileName, url, contentType,
            _proofs.Count + 1, staffNotes);
        _proofs.Add(p);
        RaiseDomainEvent(new DesignProofUploadedEvent(Id, OrderNumber, CustomerId, p.Version));
        return p;
    }

    /// <summary>
    /// Adds a design review submission (customer or staff). Unlike design proofs,
    /// design reviews maintain independent history - each submission is preserved
    /// with its own approval state. Does NOT supersede previous submissions.
    /// </summary>
    public OrderDesignReview AddDesignReview(
        string storedFileName, string originalFileName, string url,
        string contentType, long sizeBytes, Guid uploadedByUserId, string uploadedByRole,
        string? notes = null, string? previewUrl = null, string? previewStoredFileName = null)
    {
        EnsureMutable();
        var review = OrderDesignReview.Create(
            Id, storedFileName, originalFileName, url, contentType, sizeBytes,
            uploadedByUserId, uploadedByRole, notes, previewUrl, previewStoredFileName);
        _designReviews.Add(review);
        RaiseDomainEvent(new DesignReviewUploadedEvent(
            Id, OrderNumber, CustomerId, uploadedByUserId, uploadedByRole, originalFileName));
        return review;
    }

    /// <summary>
    /// Approves a specific design review submission. The design review must be in Pending status.
    /// </summary>
    public OrderDesignReview ApproveDesignReview(Guid designReviewId, Guid approvedByUserId, string? approvalNotes = null)
    {
        EnsureMutable();
        var review = _designReviews.FirstOrDefault(r => r.Id == designReviewId)
            ?? throw new BusinessRuleValidationException("order.designreview.notfound", 
                "Design review not found on this order.");
        review.Approve(approvedByUserId, approvalNotes);
        RaiseDomainEvent(new DesignReviewApprovedEvent(
            Id, OrderNumber, CustomerId, designReviewId, approvedByUserId));
        return review;
    }

    /// <summary>
    /// Rejects a specific design review submission with a reason. The design review must be in Pending status.
    /// </summary>
    public OrderDesignReview RejectDesignReview(Guid designReviewId, Guid rejectedByUserId, string reason)
    {
        EnsureMutable();
        var review = _designReviews.FirstOrDefault(r => r.Id == designReviewId)
            ?? throw new BusinessRuleValidationException("order.designreview.notfound", 
                "Design review not found on this order.");
        review.Reject(rejectedByUserId, reason);
        RaiseDomainEvent(new DesignReviewRejectedEvent(
            Id, OrderNumber, CustomerId, designReviewId, rejectedByUserId, reason));
        return review;
    }

    /// <summary>
    /// Customer approves a proof version for production. The approval event fires for
    /// every approval (staff must hear about the decision however the order got here);
    /// only the AwaitingDesignApproval -&gt; RequirementsConfirmed hop is conditional.
    /// </summary>
    public OrderDesignProof ApproveProof(Guid proofId, Guid userId)
    {
        EnsureMutable();
        var proof = _proofs.FirstOrDefault(p => p.Id == proofId)
            ?? throw new BusinessRuleValidationException("order.proof.notfound", "Proof not found on this order.");
        proof.Approve(userId);
        if (Status == OrderStatus.AwaitingDesignApproval)
            Transition(OrderStatus.RequirementsConfirmed);
        RaiseDomainEvent(new DesignProofApprovedEvent(Id, OrderNumber, CustomerId));
        return proof;
    }

    /// <summary>
    /// Customer rejects a proof and asks for changes; the order returns to discussion.
    /// Every request raises an event so staff are notified of the decision and its reason.
    /// </summary>
    public OrderDesignProof RequestProofChanges(Guid proofId, string feedback)
    {
        EnsureMutable();
        var proof = _proofs.FirstOrDefault(p => p.Id == proofId)
            ?? throw new BusinessRuleValidationException("order.proof.notfound", "Proof not found on this order.");
        proof.RequestChanges(feedback);
        if (Status == OrderStatus.AwaitingDesignApproval)
            Transition(OrderStatus.InDiscussion);
        RaiseDomainEvent(new DesignProofChangesRequestedEvent(
            Id, OrderNumber, CustomerId, proof.Version, proof.CustomerFeedback ?? string.Empty));
        return proof;
    }

    /// <summary>Submits the draft as an ORDER REQUEST (custom path) instead of placing it for payment.</summary>
    public void SubmitOrderRequest()
    {
        if (_items.Count == 0 && _services.Count == 0)
            throw new BusinessRuleValidationException("order.empty", "An order must contain at least one product or service.");
        if (Status != OrderStatus.Draft)
            throw new BusinessRuleValidationException("order.status", "Only a draft order can be submitted as a request.");
        Transition(OrderStatus.AwaitingReview);
        RaiseDomainEvent(new OrderRequestSubmittedEvent(Id, OrderNumber, CustomerId));
    }

    public void StartDiscussion()
    {
        if (Status is not (OrderStatus.AwaitingReview or OrderStatus.RequirementsConfirmed))
            throw new BusinessRuleValidationException("order.status", "Order is not in a state that can enter discussion.");
        Transition(OrderStatus.InDiscussion);
    }

    public void ConfirmRequirements()
    {
        if (Status is not (OrderStatus.AwaitingReview or OrderStatus.InDiscussion))
            throw new BusinessRuleValidationException("order.status", "Requirements can only be confirmed from review or discussion.");
        Transition(OrderStatus.RequirementsConfirmed);
    }

    public void AwaitDesignApproval()
    {
        if (Status is not (OrderStatus.InDiscussion or OrderStatus.RequirementsConfirmed
            or OrderStatus.PendingPayment))
            throw new BusinessRuleValidationException("order.status", "A design can only be sent for approval from discussion or confirmed requirements.");
        Transition(OrderStatus.AwaitingDesignApproval);
    }

    /// <summary>Finalizes the request into a commercial order (custom path) — mirrors PlaceOrder().
    /// Allowed from any pre-payment review state: finalizing IS the confirmation of the
    /// requirements and pricing, so staff do not need a separate "confirm" click first.</summary>
    public void FinalizeOrder()
    {
        if (Status is not (OrderStatus.RequirementsConfirmed or OrderStatus.Draft
            or OrderStatus.AwaitingReview or OrderStatus.InDiscussion))
            throw new BusinessRuleValidationException("order.finalize",
                "Only an order under review (or with confirmed requirements) can be finalized.");
        if (_items.Count == 0 && _services.Count == 0 && _customLines.Count == 0)
            throw new BusinessRuleValidationException("order.empty", "An order must contain at least one line.");
        Transition(OrderStatus.PendingPayment);
        ResolveConversation(); // sales work is done; the conversation stays as history
        RaiseDomainEvent(new OrderFinalizedEvent(Id, OrderNumber, CustomerId, GrandTotal.Amount));
    }

    public void StartQualityCheck()
    {
        if (Status != OrderStatus.InProduction)
            throw new BusinessRuleValidationException("order.status", "Only an order in production can enter quality check.");
        Transition(OrderStatus.QualityCheck);
    }

    public void Dispatch()
    {
        if (Status is not (OrderStatus.Ready or OrderStatus.Completed))
            throw new BusinessRuleValidationException("order.dispatch", "Only a ready order can be dispatched.");
        Transition(OrderStatus.OutForDelivery);
    }

    /// <summary>Submits a draft for payment. Requires at least one line.</summary>
    public void PlaceOrder()
    {
        if (_items.Count == 0 && _services.Count == 0)
            throw new BusinessRuleValidationException("order.empty", "An order must contain at least one product or service.");
        if (Status != OrderStatus.Draft)
            throw new BusinessRuleValidationException("order.status", "Only a draft order can be placed.");

        Transition(OrderStatus.PendingPayment);
        RaiseDomainEvent(new OrderPlacedEvent(Id, OrderNumber, CustomerId, GrandTotal.Amount));
    }

    /// <summary>
    /// Confirms payment. Inventory deduction / reservation is handled by the application layer
    /// against the Inventory aggregates; this method only advances order state.
    /// </summary>
    public void MarkPaid()
    {
        if (Status is not (OrderStatus.PendingPayment or OrderStatus.Draft))
            throw new BusinessRuleValidationException("order.pay", "Order is not awaiting payment.");
        Transition(OrderStatus.Paid);
        PaidAtUtc = DateTime.UtcNow;
        RaiseDomainEvent(new OrderPaidEvent(Id, OrderNumber, CustomerId));
    }

    /// <summary>Flags that some products must be procured (backorder) before fulfilment.</summary>
    public void FlagProcurementRequired()
    {
        RequiresProcurement = true;
        if (Status == OrderStatus.Paid)
        {
            Transition(OrderStatus.AwaitingProcurement);
            RaiseDomainEvent(new OrderRequiresProcurementEvent(Id, OrderNumber));
        }
    }

    public void StartProduction()
    {
        if (Status is not (OrderStatus.Paid or OrderStatus.AwaitingProcurement))
            throw new BusinessRuleValidationException("order.production", "Order must be paid (and stocked) before production.");
        Transition(OrderStatus.InProduction);
    }

    public void MarkReady()
    {
        if (Status is not (OrderStatus.InProduction or OrderStatus.QualityCheck))
            throw new BusinessRuleValidationException("order.status", "Order must be in production (or quality check) to become ready.");
        Transition(OrderStatus.Ready);
    }

    public void MarkCompleted()
    {
        if (Status is not (OrderStatus.Ready or OrderStatus.InProduction or OrderStatus.QualityCheck))
            throw new BusinessRuleValidationException("order.complete", "Order must be ready before completion.");
        Transition(OrderStatus.Completed);
        CompletedAtUtc = DateTime.UtcNow;
        ResolveConversation();
        RaiseDomainEvent(new OrderCompletedEvent(Id, OrderNumber, CustomerId));
    }

    public void MarkDelivered()
    {
        if (Status is not (OrderStatus.Completed or OrderStatus.Ready or OrderStatus.OutForDelivery))
            throw new BusinessRuleValidationException("order.deliver", "Order must be completed or out for delivery before it can be marked delivered.");
        Transition(OrderStatus.Delivered);
        ResolveConversation();
    }

    public void Cancel(string? reason)
    {
        if (Status is OrderStatus.Completed or OrderStatus.Delivered or OrderStatus.Refunded)
            throw new BusinessRuleValidationException("order.cancel", "A completed or delivered order cannot be cancelled.");
        CustomerNotes = reason ?? CustomerNotes;
        Transition(OrderStatus.Cancelled);
        ResolveConversation();
    }

    public void EnsureCanRefund()
    {
        if (Status is not (OrderStatus.Paid or OrderStatus.Completed or OrderStatus.Delivered or OrderStatus.InProduction or OrderStatus.Ready))
            throw new BusinessRuleValidationException("order.refund", "Only a paid order can be refunded.");
    }

    /// <summary>
    /// The order operations the DOMAIN accepts in each status — the exact same guards
    /// the transition methods above enforce (AUD-008). UI surfaces must derive their
    /// action buttons from here so the interface can never offer a transition the
    /// aggregate would reject (e.g. "Refund" on AwaitingProcurement/QualityCheck/
    /// OutForDelivery, which <see cref="EnsureCanRefund"/> refuses).
    /// Action names match the admin status-transition endpoint vocabulary.
    /// </summary>
    public static IReadOnlyList<string> DomainActions(OrderStatus status) => status switch
    {
        // Pre-payment: payment can be (manually) confirmed; a draft/pending order can be cancelled.
        OrderStatus.Draft or OrderStatus.PendingPayment => new[] { "MarkPaid", "Cancel" },
        OrderStatus.Paid => new[] { "StartProduction", "FlagProcurement", "Refund", "Cancel" },
        // Refund is NOT offered: EnsureCanRefund accepts only Paid/InProduction/Ready/Completed/Delivered.
        OrderStatus.AwaitingProcurement => new[] { "StartProduction", "Cancel" },
        OrderStatus.InProduction => new[] { "StartQualityCheck", "MarkReady", "MarkCompleted", "Refund", "Cancel" },
        // QualityCheck can still become Ready/Completed/Cancelled, but not refunded.
        OrderStatus.QualityCheck => new[] { "MarkReady", "MarkCompleted", "Cancel" },
        OrderStatus.Ready => new[] { "Dispatch", "MarkCompleted", "MarkDelivered", "Refund", "Cancel" },
        // OutForDelivery can be delivered or cancelled, but not refunded.
        OrderStatus.OutForDelivery => new[] { "MarkDelivered", "Cancel" },
        OrderStatus.Completed => new[] { "Dispatch", "MarkDelivered", "Refund" },
        OrderStatus.Delivered => new[] { "Refund" },
        // Cancel is legal from every remaining pre-completion state, but the custom-order
        // pre-payment states are driven from the order workspace, not the status buttons.
        _ => Array.Empty<string>()
    };

    public void Refund()
    {
        EnsureCanRefund();
        Transition(OrderStatus.Refunded);
        ResolveConversation();
    }

    private void RequireStatus(OrderStatus expected)
    {
        if (Status != expected)
            throw new BusinessRuleValidationException("order.status", $"Order must be in status {expected}.");
    }

    private void Transition(OrderStatus next)
    {
        var old = Status;
        Status = next;
        RaiseDomainEvent(new OrderStatusChangedEvent(Id, OrderNumber, old, next));
    }
}
