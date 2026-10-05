using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A customer's answer to one category option on a submitted order request. Question,
/// type and value are SNAPSHOTTED so editing the category's options later never changes
/// what the customer actually asked for.
/// </summary>
public sealed class OrderRequirementResponse : Entity
{
    private OrderRequirementResponse() { } // EF

    private OrderRequirementResponse(Guid orderId, Guid? categoryOptionId, string question,
        CategoryOptionType type, string value, int displayOrder)
    {
        OrderId = orderId;
        CategoryOptionId = categoryOptionId;
        Question = question;
        Type = type;
        Value = value;
        DisplayOrder = displayOrder;
    }

    public Guid OrderId { get; private set; }
    public Guid? CategoryOptionId { get; private set; }
    public string Question { get; private set; } = default!;
    public CategoryOptionType Type { get; private set; }

    /// <summary>JSON string array for single/multiple choice and file answers; a plain
    /// string for text/long-text.</summary>
    public string Value { get; private set; } = default!;
    public int DisplayOrder { get; private set; }

    public static OrderRequirementResponse Create(Guid orderId, Guid? categoryOptionId, string question,
        CategoryOptionType type, string value, int displayOrder)
    {
        if (orderId == Guid.Empty)
            throw new BusinessRuleValidationException("order.response.order", "An order is required.");
        if (string.IsNullOrWhiteSpace(question))
            throw new BusinessRuleValidationException("order.response.question", "The question is required.");
        return new OrderRequirementResponse(orderId, categoryOptionId, question.Trim(), type,
            value ?? string.Empty, displayOrder);
    }
}
