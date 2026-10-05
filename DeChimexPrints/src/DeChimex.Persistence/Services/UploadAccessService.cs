using DeChimex.Application.Common.Interfaces;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Services;

/// <summary>
/// Read-side authorization for confidential uploads (AUD-021). Order attachments, chat
/// media, design proofs and design-review files belong to ONE order; this service resolves
/// a private upload URL back to its owning order and answers whether the current user may
/// see it: the order's own customer (linked login) or staff holding the orders.view
/// permission. Unknown URLs are denied — the caller answers 404 so existence is not leaked.
/// </summary>
public sealed class UploadAccessService(DeChimexDbContext db, ICurrentUser currentUser) : IUploadAccessService
{
    public async Task<bool> CanViewAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !currentUser.IsAuthenticated)
            return false;

        // Staff with order visibility can open any order-bound file.
        if (currentUser.HasPermission(Permissions.Orders.View))
            return true;

        var orderId = await ResolveOrderIdAsync(url.Trim(), ct);
        if (orderId is null)
            return false; // not an order-bound file (or unknown): deny

        // The order's own customer.
        return await db.Orders.AsNoTracking()
            .Where(o => o.Id == orderId && o.Customer != null && o.Customer.UserId == currentUser.UserId)
            .AnyAsync(ct);
    }

    /// <summary>Finds the order a private upload belongs to (any order-bound file table).</summary>
    private async Task<Guid?> ResolveOrderIdAsync(string url, CancellationToken ct)
    {
        var fromFile = await db.OrderFiles.AsNoTracking()
            .Where(f => f.Url == url).Select(f => (Guid?)f.OrderId).FirstOrDefaultAsync(ct);
        if (fromFile is not null) return fromFile;

        // Chat media hangs off the message; the message carries the order id.
        var fromAttachment = await db.OrderMessageAttachments.AsNoTracking()
            .Join(db.OrderMessages.AsNoTracking(), a => a.MessageId, m => m.Id,
                (a, m) => new { a.Url, m.OrderId })
            .Where(x => x.Url == url)
            .Select(x => (Guid?)x.OrderId)
            .FirstOrDefaultAsync(ct);
        if (fromAttachment is not null) return fromAttachment;

        var fromProof = await db.OrderDesignProofs.AsNoTracking()
            .Where(p => p.Url == url).Select(p => (Guid?)p.OrderId).FirstOrDefaultAsync(ct);
        if (fromProof is not null) return fromProof;

        var fromReview = await db.OrderDesignReviews.AsNoTracking()
            .Where(r => r.Url == url || r.PreviewUrl == url).Select(r => (Guid?)r.OrderId).FirstOrDefaultAsync(ct);
        if (fromReview is not null) return fromReview;

        return null;
    }
}
