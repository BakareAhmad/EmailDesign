namespace DeChimex.Domain.Enums;

/// <summary>
/// Customer-facing presentation of the (staff-internal) order lifecycle.
/// Internal statuses are deliberately finer-grained than what customers see
/// (CUSTOM_ORDER_FLOW_PLAN.md §5.3). This helper is the single source of truth
/// for the storefront/portal projection and the status labels used in emails.
/// </summary>
public static class OrderStatusPresentation
{
    /// <summary>The short label shown to customers for an internal status.</summary>
    public static string CustomerLabel(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingReview => "Order Received",
        OrderStatus.InDiscussion or OrderStatus.RequirementsConfirmed => "Being Reviewed",
        OrderStatus.AwaitingDesignApproval => "Awaiting Your Approval",
        OrderStatus.Draft or OrderStatus.PendingPayment => "Payment Required",
        OrderStatus.Paid or OrderStatus.AwaitingProcurement
            or OrderStatus.InProduction or OrderStatus.QualityCheck => "In Production",
        OrderStatus.Ready => "Ready",
        OrderStatus.OutForDelivery => "Out for Delivery",
        OrderStatus.Completed or OrderStatus.Delivered => "Completed",
        OrderStatus.Cancelled => "Cancelled",
        OrderStatus.Refunded => "Refunded",
        _ => status.ToString()
    };

    /// <summary>
    /// Whether the customer is expected to act right now (approve a design, pay, etc.).
    /// Drives the "Action required" vs "We're working on it" callout (§23).
    /// </summary>
    public static bool CustomerActionRequired(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingDesignApproval or OrderStatus.Draft or OrderStatus.PendingPayment => true,
        _ => false
    };

    /// <summary>A short, imperative description of what the customer should do next.</summary>
    public static string CustomerNextStep(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingReview => "We've received your request and the team is reviewing it.",
        OrderStatus.InDiscussion => "Please respond in the conversation so we can finalize your requirements.",
        OrderStatus.RequirementsConfirmed => "The team is preparing your final order and price.",
        OrderStatus.AwaitingDesignApproval => "Please review the design and approve it or request changes.",
        OrderStatus.Draft or OrderStatus.PendingPayment => "Your order is ready — please complete payment to start production.",
        OrderStatus.Paid or OrderStatus.AwaitingProcurement or OrderStatus.InProduction
            or OrderStatus.QualityCheck => "We're working on your order. We'll notify you when there's an update.",
        OrderStatus.Ready => "Your order is ready — we'll be in touch about pickup or delivery.",
        OrderStatus.OutForDelivery => "Your order is on its way.",
        OrderStatus.Completed or OrderStatus.Delivered => "Your order is complete. Thank you!",
        OrderStatus.Cancelled => "This order was cancelled.",
        OrderStatus.Refunded => "This order was refunded.",
        _ => "We'll notify you when there's an update."
    };
}
