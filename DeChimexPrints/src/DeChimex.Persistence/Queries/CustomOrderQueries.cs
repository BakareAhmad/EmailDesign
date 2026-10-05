using DeChimex.Application.Common.Interfaces;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

/// <summary>Read side for a category's ordering options (active only, display-ordered).</summary>
public sealed class CategoryOptionsQueries(DeChimexDbContext db) : ICategoryOptionsQueries
{
    public async Task<IReadOnlyList<CategoryOptionDto>> GetForCategoryAsync(Guid categoryId, CancellationToken ct)
    {
        var options = await db.CategoryOptions.AsNoTracking()
            .Where(o => o.CategoryId == categoryId && o.IsActive)
            .Include(o => o.Choices)
            .OrderBy(o => o.DisplayOrder)
            .ToListAsync(ct);

        return options.Select(o => new CategoryOptionDto(
            o.Id, o.Question, o.Type.ToString(), o.IsRequired, o.DisplayOrder,
            o.ParentOptionId, o.ShowWhenValue,
            o.Choices.OrderBy(c => c.DisplayOrder)
                .Select(c => new CategoryOptionChoiceDto(c.Id, c.Label, c.DisplayOrder)).ToList())).ToList();
    }

    public Task<bool> CategoryHasActiveOptionsAsync(Guid categoryId, CancellationToken ct)
        => db.CategoryOptions.AsNoTracking().AnyAsync(o => o.CategoryId == categoryId && o.IsActive, ct);
}

/// <summary>Read side for the order workspace (order + responses/files/chat/timeline/proofs/lines).</summary>
public sealed class OrderWorkspaceQueries(DeChimexDbContext db) : IOrderWorkspaceQueries
{
    public async Task<OrderWorkspaceDto?> GetAsync(Guid orderId, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Items).Include(x => x.Services).Include(x => x.CustomLines)
            .Include(x => x.Responses).Include(x => x.Files).Include(x => x.Messages).ThenInclude(m => m.Attachments)
            .Include(x => x.Timeline).Include(x => x.Proofs).Include(x => x.DesignReviews)
            .FirstOrDefaultAsync(x => x.Id == orderId, ct);

        if (o is null) return null;

        return new OrderWorkspaceDto(
            o.Id, o.OrderNumber, o.CustomerId, o.Customer?.FullName ?? "", o.Type.ToString(), o.Status.ToString(),
            OrderStatusPresentation.CustomerLabel(o.Status),
            OrderStatusPresentation.CustomerNextStep(o.Status),
            OrderStatusPresentation.CustomerActionRequired(o.Status),
            o.ItemsSubtotal.Amount, o.DiscountAmount.Amount, o.DeliveryFee.Amount, o.TaxAmount.Amount,
            o.GrandTotal.Amount, o.GrandTotal.Currency, o.CreatedDateUtc,
            o.Items.Select(i => new OrderItemDto(i.Id, i.ProductId, i.ProductName, i.Sku, i.UnitPrice.Amount, i.Quantity, i.LineTotal.Amount)).ToList(),
            o.Services.Select(s => new OrderServiceDto(s.Id, s.ServiceId, s.ServiceName, s.UnitPrice.Amount, s.Quantity, s.Notes, s.LineTotal.Amount)).ToList(),
            o.CustomLines.Select(c => new OrderCustomLineDto(c.Id, c.Description, c.UnitPrice.Amount, c.Quantity, c.LineTotal.Amount)).ToList(),
            o.Responses.OrderBy(r => r.DisplayOrder).Select(r => new OrderRequirementResponseDto(r.Id, r.Question, r.Type.ToString(), r.Value, r.DisplayOrder)).ToList(),
            o.Files.OrderByDescending(f => f.CreatedDateUtc).Select(f => new OrderFileDto(f.Id, f.OriginalFileName, f.Url, f.ContentType, f.SizeBytes, f.Purpose, f.UploadedByRole, f.CreatedDateUtc)).ToList(),
            ProjectMessages(o.Messages),
            o.Timeline.OrderByDescending(t => t.CreatedDateUtc).Select(t => new OrderTimelineEntryDto(t.Id, t.Type, t.Description, t.ActorName, t.CreatedDateUtc)).ToList(),
            o.Proofs.OrderByDescending(p => p.Version).Select(p => new OrderDesignProofDto(p.Id, p.Version, p.Url, p.OriginalFileName, p.ContentType, p.Status.ToString(), p.StaffNotes, p.CustomerFeedback, p.ApprovedAtUtc, p.CreatedDateUtc)).ToList(),
            o.DesignReviews.OrderByDescending(dr => dr.CreatedDateUtc).Select(dr => new OrderDesignReviewDto(
                dr.Id, dr.OriginalFileName, dr.Url, dr.ContentType, dr.SizeBytes,
                dr.PreviewUrl, dr.UploadedByRole, dr.Status, dr.RejectionReason,
                dr.ApprovedByUserId, dr.ApprovedAtUtc, dr.RejectedByUserId, dr.RejectedAtUtc,
                dr.CreatedDateUtc, dr.Notes)).ToList(),
            o.CustomerNotes,
            o.AssignedSalesName, o.AssignedSalesUserId, o.ConversationState.ToString(),
            o.Kind.ToString());
    }

    /// <summary>Projects chat history with attachments, delivery/read state and the
    /// quoted parent's snippet for replies. A reply whose parent is missing (never
    /// happens through the UI — replies are validated at send time) projects with a
    /// null snippet, which the UI renders as "unavailable".</summary>
    private static IReadOnlyList<OrderMessageDto> ProjectMessages(IEnumerable<OrderMessage> messages)
    {
        var ordered = messages.OrderBy(m => m.CreatedDateUtc).ToList();
        var byId = ordered.ToDictionary(m => m.Id);
        return ordered.Select(m =>
        {
            byId.TryGetValue(m.ReplyToMessageId ?? Guid.Empty, out var parent);
            return new OrderMessageDto(
                m.Id, m.AuthorRole, m.AuthorName, m.Body, m.CreatedDateUtc,
                m.ReplyToMessageId,
                parent?.AuthorName,
                parent is null ? null : OrderMessageDto.QuoteSnippet(parent.Body,
                    parent.Attachments.Select(a => a.Kind).ToList()),
                m.Attachments.Select(a => new OrderMessageAttachmentDto(a.Id, a.Kind,
                    a.OriginalFileName, a.Url, a.ContentType, a.SizeBytes, a.DurationSeconds)).ToList(),
                m.DeliveredAtUtc, m.ReadAtUtc);
        }).ToList();
    }
}
