using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// An immutable entry in an order's timeline/history — the structured replacement for
/// the WhatsApp trail. Every significant action (submission, uploads, messages, proofs,
/// approval, finalization, payment, status changes) appends one.
/// </summary>
public sealed class OrderTimelineEntry : Entity
{
    private OrderTimelineEntry() { } // EF

    private OrderTimelineEntry(Guid orderId, string type, string description,
        Guid? actorUserId, string? actorName)
    {
        OrderId = orderId;
        Type = type;
        Description = description;
        ActorUserId = actorUserId;
        ActorName = actorName;
        CreatedDateUtc = DateTime.UtcNow;
    }

    public Guid OrderId { get; private set; }
    public string Type { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public Guid? ActorUserId { get; private set; }
    public string? ActorName { get; private set; }
    public DateTime CreatedDateUtc { get; private set; }

    public static OrderTimelineEntry Create(Guid orderId, string type, string description,
        Guid? actorUserId = null, string? actorName = null)
    {
        if (orderId == Guid.Empty)
            throw new BusinessRuleValidationException("order.timeline.order", "An order is required.");
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(description))
            throw new BusinessRuleValidationException("order.timeline", "Timeline type and description are required.");
        return new OrderTimelineEntry(orderId, type.Trim().ToLowerInvariant(), description.Trim(),
            actorUserId, string.IsNullOrWhiteSpace(actorName) ? null : actorName.Trim());
    }
}
