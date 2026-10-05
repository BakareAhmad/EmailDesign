namespace DeChimex.Domain.Enums;

/// <summary>Lifecycle statuses for an order (see procurement &amp; fulfilment workflow).</summary>
public enum OrderStatus
{
    Draft = 0,
    PendingPayment = 1,
    Paid = 2,
    AwaitingProcurement = 3,
    InProduction = 4,
    Ready = 5,
    Completed = 6,
    Delivered = 7,
    Cancelled = 8,
    Refunded = 9,

    // Custom-order (order-request) pre-payment states, added for the category-driven
    // ordering workflow (see CUSTOM_ORDER_FLOW_PLAN.md §5). Values 10+ are additive so
    // legacy rows keep their meaning. The customer-facing progression is derived from
    // these via OrderStatusPresentation (internal statuses are never shown verbatim).
    /// <summary>Order Request submitted by a customer; awaiting staff review.</summary>
    AwaitingReview = 10,
    /// <summary>Staff and customer are clarifying requirements via order chat/files.</summary>
    InDiscussion = 11,
    /// <summary>Staff have confirmed the requirements; ready to be finalized.</summary>
    RequirementsConfirmed = 12,
    /// <summary>A design proof is awaiting the customer's approval or change request.</summary>
    AwaitingDesignApproval = 13,
    /// <summary>Production finished; quality check in progress.</summary>
    QualityCheck = 14,
    /// <summary>Dispatched for delivery (or ready for pickup handover).</summary>
    OutForDelivery = 15
}

/// <summary>
/// How an order entered the system (creation entry point) — NOT a lifecycle status.
/// Direct = self-serve direct purchase (Add to cart -> checkout -> PlaceOrder);
/// Custom = the custom sales workflow (order request via SubmitOrderRequest, or a
/// staff-built/quote order). Lets admin, audit and review surfaces tell the two paths
/// apart after both converge on PendingPayment. Stored as a string column.
/// </summary>
public enum OrderKind
{
    Direct = 0,
    Custom = 1
}

/// <summary>
/// Order composition. Only products deduct inventory; services never do.
/// </summary>
public enum OrderType
{
    Inventory = 0, // products only
    Service = 1,   // services only (customer supplies items)
    Mixed = 2      // products + services
}

/// <summary>Every inventory movement is recorded as one of these transaction types.</summary>
public enum InventoryTransactionType
{
    StockIn = 0,
    StockOut = 1,
    Adjustment = 2,
    Reservation = 3,
    ReleaseReservation = 4
}

public enum PaymentStatus
{
    Pending = 0,
    Initialized = 1,
    Succeeded = 2,
    Failed = 3,
    Refunded = 4,
    // Abandoned is never set by current code (the MarkAbandoned transition was removed);
    // the value stays so rows persisted by older builds still map when read back.
    Abandoned = 5
}

/// <summary>
/// Settlement state of a refund (AUD-005). <see cref="PaymentStatus.Refunded"/> records
/// that the provider ACCEPTED the refund request; <see cref="RefundStatus"/> tracks the
/// settlement lifecycle Paystack reports via the refund.* webhook events:
/// Requested (accepted, settlement pending) -> Processed (money returned) | Failed.
/// Manual (offline) refunds skip the provider and are Processed immediately.
/// </summary>
public enum RefundStatus
{
    None = 0,
    Requested = 1,
    Processed = 2,
    Failed = 3
}

/// <summary>
/// Accepted payment methods. Online checkout uses Paystack only; BankTransfer and
/// Cash exist for staff-confirmed counter payments and shared transfer references.
/// There is deliberately no pay-on-delivery method (the removed value 2 is left as a
/// gap so existing database rows keep their stored values).
/// </summary>
public enum PaymentMethod
{
    BankTransfer = 0,
    Paystack = 1,
    // 2 = legacy pay-on-delivery, removed by business rule; value kept as a gap.
    Cash = 3
}

public enum InvoiceStatus
{
    Draft = 0,
    Issued = 1,
    Paid = 2,
    PartiallyPaid = 3,
    Cancelled = 4,
    Refunded = 5
}

public enum ProcurementStatus
{
    Requested = 0,
    Approved = 1,
    Purchased = 2,
    Received = 3,
    Cancelled = 4
}

public enum GalleryMediaType
{
    Image = 0,
    Video = 1
}

public enum NotificationType
{
    LowStock = 0,
    NewOrder = 1,
    PaymentReceived = 2,
    ProcurementRequired = 3,
    OrderCompleted = 4,
    UserActivity = 5,
    System = 6,
    NewMessage = 7,

    // Custom-order workflow notifications (stored as string, so adding members needs
    // no migration). See CUSTOM_ORDER_FLOW_PLAN.md §11.
    OrderRequestReceived = 8,
    OrderMessageReceived = 9,
    OrderInfoRequested = 10,
    OrderQuoteReady = 11,
    DesignProofReady = 12,
    DesignProofApproved = 13,
    OrderReadyForPickup = 14,
    OrderOutForDelivery = 15,

    // Sales-assignment workflow notifications (stored as string, so adding members
    // needs no migration). Sent directly to the assigned sales staff member.
    SalesAssigned = 16,
    SalesReassigned = 17,

    // Staff confirmed the customer's requirements: the customer is waiting on this
    // decision, so it notifies them directly (stored as string, no migration).
    RequirementsConfirmed = 18,

    // The customer requested changes on a design proof: staff must revise and
    // re-upload (stored as string, no migration).
    DesignProofChangesRequested = 19,

    // Design-review workflow (independent-history file submissions with their own
    // approve/reject decision — separate from versioned design proofs; stored as
    // string, no migration).
    DesignReviewUploaded = 20,
    DesignReviewApproved = 21,
    DesignReviewRejected = 22
}

/// <summary>
/// The kind of question an admin configures on a Category to drive product ordering
/// (single/multiple choice, short text, long text, or a file upload). Rendered
/// dynamically on the customer order form; see CUSTOM_ORDER_FLOW_PLAN.md §4.1.
/// </summary>
public enum CategoryOptionType
{
    SingleChoice = 0,
    MultipleChoice = 1,
    Text = 2,
    LongText = 3,
    FileUpload = 4
}

/// <summary>Lifecycle of a versioned design proof attached to an order (§11).</summary>
public enum DesignProofStatus
{
    PendingApproval = 0,
    Approved = 1,
    ChangesRequested = 2,
    Superseded = 3
}

/// <summary>
/// Who-needs-to-act-next state of an order's sales conversation. Orthogonal to
/// <see cref="OrderStatus"/> (the business lifecycle): e.g. InDiscussion +
/// WaitingForCustomer means requirements are still open but DeChimex already
/// replied and is waiting for the customer's answer. Stored as a string column.
/// </summary>
public enum OrderConversationState
{
    /// <summary>Live discussion, no one explicitly waited on (e.g. a fresh request).</summary>
    Active = 0,
    /// <summary>DeChimex responded; the next action is expected from the customer.</summary>
    WaitingForCustomer = 1,
    /// <summary>The customer responded; the sales team needs to act.</summary>
    WaitingForStaff = 2,
    /// <summary>Sales work is done (finalized/cancelled/completed); history retained.</summary>
    Resolved = 3
}

public enum OrderLineType
{
    Product = 0,
    Service = 1
}

/// <summary>Lifecycle of an admin-composed marketing broadcast.</summary>
public enum MarketingBroadcastStatus
{
    Draft = 0,
    Queued = 1,
    Sending = 2,
    Completed = 3
}

/// <summary>Per-recipient delivery state of a marketing broadcast (duplicate-send guard).</summary>
public enum MarketingRecipientStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2
}
