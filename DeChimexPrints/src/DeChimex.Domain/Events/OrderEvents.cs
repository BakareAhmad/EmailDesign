using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;

namespace DeChimex.Domain.Events;

public sealed record OrderPlacedEvent(Guid OrderId, string OrderNumber, Guid CustomerId, decimal GrandTotal) : DomainEvent;
public sealed record OrderPaidEvent(Guid OrderId, string OrderNumber, Guid CustomerId) : DomainEvent;
public sealed record OrderStatusChangedEvent(Guid OrderId, string OrderNumber, OrderStatus OldStatus, OrderStatus NewStatus) : DomainEvent;
public sealed record OrderRequiresProcurementEvent(Guid OrderId, string OrderNumber) : DomainEvent;
public sealed record OrderCompletedEvent(Guid OrderId, string OrderNumber, Guid CustomerId) : DomainEvent;
public sealed record PaymentFailedEvent(Guid OrderId, string Reference, decimal Amount, string? Reason) : DomainEvent;

// Custom-order workflow events (see CUSTOM_ORDER_FLOW_PLAN.md §8).
public sealed record OrderRequestSubmittedEvent(Guid OrderId, string OrderNumber, Guid CustomerId) : DomainEvent;
public sealed record OrderFinalizedEvent(Guid OrderId, string OrderNumber, Guid CustomerId, decimal GrandTotal) : DomainEvent;
public sealed record OrderMessageAddedEvent(Guid OrderId, string OrderNumber, Guid? AuthorUserId, string AuthorRole, string AuthorName, string Body) : DomainEvent;
public sealed record OrderFileAddedEvent(Guid OrderId, string OrderNumber, string FileUrl, string? Purpose, string UploadedByRole) : DomainEvent;
public sealed record DesignProofUploadedEvent(Guid OrderId, string OrderNumber, Guid CustomerId, int Version) : DomainEvent;
public sealed record DesignProofApprovedEvent(Guid OrderId, string OrderNumber, Guid CustomerId) : DomainEvent;
public sealed record DesignProofChangesRequestedEvent(
    Guid OrderId, string OrderNumber, Guid CustomerId, int Version, string Feedback) : DomainEvent;

// Design review workflow events (separate from design proofs - these maintain independent history).
public sealed record DesignReviewUploadedEvent(
    Guid OrderId, string OrderNumber, Guid CustomerId, Guid UploadedByUserId, string UploadedByRole, string OriginalFileName) : DomainEvent;
public sealed record DesignReviewApprovedEvent(
    Guid OrderId, string OrderNumber, Guid CustomerId, Guid DesignReviewId, Guid ApprovedByUserId) : DomainEvent;
public sealed record DesignReviewRejectedEvent(
    Guid OrderId, string OrderNumber, Guid CustomerId, Guid DesignReviewId, Guid RejectedByUserId, string Reason) : DomainEvent;

// Sales-assignment workflow events. Raised by Order.AssignSalesStaff for both automatic
// assignment (ActorUserId null, ActorName "System") and manual (re)assignment.
public sealed record SalesAssignedEvent(
    Guid OrderId, string OrderNumber, Guid? PreviousSalesUserId, Guid NewSalesUserId,
    string NewSalesName, Guid? ActorUserId, string? ActorName, bool IsReassignment) : DomainEvent;
