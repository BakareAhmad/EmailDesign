using DeChimex.Domain.Enums;

namespace DeChimex.Host;

/// <summary>
/// View helpers for customer-facing pages. Wording ALWAYS goes through
/// <see cref="OrderStatusPresentation"/> (the single source of truth for what a
/// customer may see); this class only adds colour tones, tracking steps and
/// money formatting.
/// </summary>
public static class CustomerPresentation
{
    public static string Money(decimal amount) => "\u20A6" + amount.ToString("N2");

    public static bool TryStatus(string raw, out OrderStatus status) => Enum.TryParse(raw, out status);

    /// <summary>Customer label for a raw order status string (falls back to the raw value).</summary>
    public static string StatusLabel(string raw)
        => TryStatus(raw, out var s) ? OrderStatusPresentation.CustomerLabel(s) : raw;

    public static string NextStep(string raw)
        => TryStatus(raw, out var s) ? OrderStatusPresentation.CustomerNextStep(s) : "We'll notify you when there's an update.";

    public static bool ActionRequired(string raw)
        => TryStatus(raw, out var s) && OrderStatusPresentation.CustomerActionRequired(s);

    /// <summary>CSS tone class for the <c>.cs-status</c> pill.</summary>
    public static string StatusTone(string raw)
    {
        if (!TryStatus(raw, out var s)) return "is-muted";
        return s switch
        {
            OrderStatus.Draft or OrderStatus.PendingPayment or OrderStatus.AwaitingDesignApproval => "is-warn",
            OrderStatus.Completed or OrderStatus.Delivered or OrderStatus.Ready => "is-ok",
            OrderStatus.Cancelled or OrderStatus.Refunded => "is-bad",
            OrderStatus.AwaitingReview or OrderStatus.InDiscussion or OrderStatus.RequirementsConfirmed
                or OrderStatus.Paid or OrderStatus.AwaitingProcurement or OrderStatus.InProduction
                or OrderStatus.QualityCheck or OrderStatus.OutForDelivery => "is-info",
            _ => "is-muted"
        };
    }

    /// <summary>True when the order goes through the custom (workspace) workflow.</summary>
    public static bool IsCustomStatus(string raw)
        => raw is "AwaitingReview" or "InDiscussion" or "RequirementsConfirmed" or "AwaitingDesignApproval";

    public static bool IsClosed(string raw) => raw is "Cancelled" or "Refunded";

    /// <summary>
    /// 4-step tracker used on order pages: 1 Placed · 2 Paid · 3 In production · 4 Ready/Delivered.
    /// Returns 0 for statuses before payment is possible (custom review) and for closed orders.
    /// </summary>
    public static int TrackStep(string raw)
    {
        if (!TryStatus(raw, out var s)) return 0;
        return s switch
        {
            OrderStatus.AwaitingReview or OrderStatus.InDiscussion or OrderStatus.RequirementsConfirmed
                or OrderStatus.AwaitingDesignApproval or OrderStatus.Draft or OrderStatus.PendingPayment => 1,
            // Paid/AwaitingProcurement are labelled "In Production" for customers (OrderStatusPresentation),
            // so the tracker shows the same step to stay coherent with the status pill.
            OrderStatus.Paid or OrderStatus.AwaitingProcurement or OrderStatus.InProduction or OrderStatus.QualityCheck => 3,
            OrderStatus.Ready or OrderStatus.OutForDelivery or OrderStatus.Completed or OrderStatus.Delivered => 4,
            _ => 0
        };
    }

    public static readonly string[] TrackLabels = { "Order placed", "Payment confirmed", "In production", "Ready / delivered" };

    public static string PaymentTone(string status) => status switch
    {
        "Succeeded" => "is-ok",
        "Refunded" => "is-muted",
        "Initialized" or "Pending" => "is-warn",
        "Failed" or "Abandoned" or "Cancelled" => "is-bad",
        _ => "is-muted"
    };

    public static string PaymentLabel(string status) => status switch
    {
        "Succeeded" => "Paid",
        "Initialized" => "Not completed",
        "Pending" => "Pending confirmation",
        "Failed" => "Failed",
        "Refunded" => "Refunded",
        _ => status
    };

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant() : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }
}
