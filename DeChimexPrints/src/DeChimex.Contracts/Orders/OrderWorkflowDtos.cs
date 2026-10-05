using System.Text.Json.Serialization;
using DeChimex.Domain.Enums;

namespace DeChimex.Contracts.Orders;

// -----------------------------------------------------------------------------------
// Category ordering options (staff-configured; rendered on the customer order form).
// -----------------------------------------------------------------------------------

public sealed record CategoryOptionChoiceDto(Guid Id, string Label, int DisplayOrder);

public sealed record CategoryOptionDto(
    Guid Id, string Question, string Type, bool IsRequired, int DisplayOrder,
    Guid? ParentOptionId, string? ShowWhenValue, IReadOnlyList<CategoryOptionChoiceDto> Choices);

/// <summary>One option in the admin's save payload (parent references use the option Key).
/// The options editor serializes camelCase JSON; `required` maps to IsRequired explicitly.</summary>
public sealed record SaveCategoryOptionDto(
    string Key, string Question, string Type,
    [property: JsonPropertyName("required")] bool IsRequired,
    string? ParentKey, string? ShowWhenValue, IReadOnlyList<string> Choices);

public sealed record SaveCategoryOptionsRequest(IReadOnlyList<SaveCategoryOptionDto> Options);

// -----------------------------------------------------------------------------------
// Order-request submission payloads.
// -----------------------------------------------------------------------------------

/// <summary>One captured answer (single/multi choices are a JSON array; text is a string).</summary>
public sealed record OrderRequirementInput(Guid? CategoryOptionId, string Question, string Type, string Value);

/// <summary>A file already uploaded to storage, to be attached to the order.</summary>
public sealed record OrderFileInput(string StoredFileName, string OriginalFileName, string Url, string ContentType, long SizeBytes);

public sealed record SubmitOrderRequest(
    Guid ProductId,
    int Quantity,
    string? Notes,
    IReadOnlyList<OrderRequirementInput> Responses,
    IReadOnlyList<OrderFileInput> Files);

/// <summary>A chat-media file already uploaded to storage, to be attached to a message.</summary>
public sealed record ChatMessageAttachmentInput(string Kind, string OriginalFileName, string StoredFileName, string Url, string ContentType, long SizeBytes, int? DurationSeconds);

public sealed record SendOrderMessageRequest(
    string Body,
    Guid? ReplyToMessageId = null,
    IReadOnlyList<ChatMessageAttachmentInput>? Attachments = null);

public sealed record UploadOrderFileRequest(string StoredFileName, string OriginalFileName, string Url, string ContentType, long SizeBytes, string? Purpose);

public sealed record UploadDesignProofRequest(string StoredFileName, string OriginalFileName, string Url, string ContentType, string? StaffNotes);

public sealed record RequestDesignChangesRequest(string Feedback);

public sealed record OrderLinePricingInput(Guid ProductId, int Quantity, decimal UnitPrice);
public sealed record OrderCustomLineInput(string Description, decimal UnitPrice, int Quantity);

public sealed record FinalizeOrderRequest(
    IReadOnlyList<OrderLinePricingInput> ProductLines,
    IReadOnlyList<OrderCustomLineInput> CustomLines,
    decimal Discount,
    decimal DeliveryFee,
    decimal Tax,
    string? CustomerNotes);

public sealed record SetOrderRequirementsStatusRequest(string Status);

// -----------------------------------------------------------------------------------
// Workspace read model (order + its custom-order children).
// -----------------------------------------------------------------------------------

public sealed record OrderRequirementResponseDto(Guid Id, string Question, string Type, string Value, int DisplayOrder);

public sealed record OrderFileDto(Guid Id, string OriginalFileName, string Url, string ContentType, long SizeBytes, string? Purpose, string UploadedByRole, DateTime CreatedDateUtc);

public sealed record OrderMessageAttachmentDto(Guid Id, string Kind, string OriginalFileName, string Url, string ContentType, long SizeBytes, int? DurationSeconds);

public sealed record OrderMessageDto(
    Guid Id, string AuthorRole, string AuthorName, string Body, DateTime CreatedDateUtc,
    Guid? ReplyToMessageId = null,
    string? ReplyToAuthorName = null,
    string? ReplyToSnippet = null,
    IReadOnlyList<OrderMessageAttachmentDto>? Attachments = null,
    DateTime? DeliveredAtUtc = null,
    DateTime? ReadAtUtc = null)
{
    /// <summary>Compact one-line quote of a message for reply blocks: trimmed text,
    /// or a media label ("Photo", "Voice note", ...) when the message has no text.</summary>
    public static string QuoteSnippet(string body, IReadOnlyList<string>? attachmentKinds)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            var t = body.Trim();
            return t.Length > 140 ? t[..140] + "…" : t;
        }
        return (attachmentKinds is { Count: > 0 } ? attachmentKinds[0] : null) switch
        {
            "image" => "Photo",
            "video" => "Video",
            "voice" => "Voice note",
            "audio" => "Audio",
            "file" => "Attachment",
            _ => "Message"
        };
    }
}

public sealed record OrderTimelineEntryDto(Guid Id, string Type, string Description, string? ActorName, DateTime CreatedDateUtc);

public sealed record OrderDesignProofDto(Guid Id, int Version, string Url, string OriginalFileName, string ContentType, string Status, string? StaffNotes, string? CustomerFeedback, DateTime? ApprovedAtUtc, DateTime CreatedDateUtc);

public sealed record OrderDesignReviewDto(
    Guid Id, string OriginalFileName, string Url, string ContentType, long SizeBytes,
    string? PreviewUrl, string UploadedByRole, DesignReviewStatus Status,
    string? RejectionReason, Guid? ApprovedByUserId, DateTime? ApprovedAtUtc,
    Guid? RejectedByUserId, DateTime? RejectedAtUtc, DateTime CreatedDateUtc,
    string? Notes);

public sealed record UploadDesignReviewRequest(
    string StoredFileName, string OriginalFileName, string Url, 
    string ContentType, long SizeBytes, string? Notes,
    string? PreviewUrl = null, string? PreviewStoredFileName = null);

public sealed record ApproveDesignReviewRequest(string? ApprovalNotes);
public sealed record RejectDesignReviewRequest(string Reason);

public sealed record OrderCustomLineDto(Guid Id, string Description, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record OrderWorkspaceDto(
    Guid Id, string OrderNumber, Guid CustomerId, string CustomerName, string Type, string Status,
    string CustomerStatus, string CustomerNextStep, bool CustomerActionRequired,
    decimal Subtotal, decimal Discount, decimal DeliveryFee, decimal Tax, decimal GrandTotal, string Currency,
    DateTime CreatedDateUtc,
    IReadOnlyList<OrderItemDto> Items,
    IReadOnlyList<OrderServiceDto> Services,
    IReadOnlyList<OrderCustomLineDto> CustomLines,
    IReadOnlyList<OrderRequirementResponseDto> Responses,
    IReadOnlyList<OrderFileDto> Files,
    IReadOnlyList<OrderMessageDto> Messages,
    IReadOnlyList<OrderTimelineEntryDto> Timeline,
    IReadOnlyList<OrderDesignProofDto> Proofs,
    IReadOnlyList<OrderDesignReviewDto> DesignReviews,
    string? CustomerNotes,
    string? AssignedSalesName = null,
    Guid? AssignedSalesUserId = null,
    string ConversationState = "Active",
    string Kind = "Direct")
{
    /// <summary>
    /// Customer-safe copy: named sales staff must never leak to customer-facing views.
    /// Drops the assignee name and rewrites assignment timeline entries to generic
    /// wording, and strips every timeline actor name (customer views render the
    /// description only, so no internal identity needs to travel to the client at
    /// all). Staff projections keep the raw DTO. The assignee id is retained (a
    /// Guid reveals no identity and is never rendered) so views can branch on
    /// <see cref="HasAssignedSales"/>.
    /// </summary>
    public OrderWorkspaceDto RedactForCustomer() => this with
    {
        AssignedSalesName = null,
        Timeline = Timeline.Select(t => t.Type switch
        {
            "assigned" => t with
            {
                Description = "A sales specialist was assigned to your request.",
                ActorName = null
            },
            "reassigned" => t with
            {
                Description = "Your request was handed over to another sales specialist.",
                ActorName = null
            },
            "unassigned" => t with
            {
                Description = "Our team will assign a sales specialist shortly.",
                ActorName = null
            },
            _ => t with { ActorName = null }
        }).ToList()
    };

    /// <summary>Whether a sales owner is assigned (safe for customer views to branch on).</summary>
    public bool HasAssignedSales => AssignedSalesUserId is not null;
}
