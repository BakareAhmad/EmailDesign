using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Contacts;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

public sealed class ContactMessageQueries(DeChimexDbContext db) : IContactMessageQueries
{
    public async Task<PagedResult<ContactMessageDto>> SearchAsync(PagedRequest request, bool unresolvedOnly, CancellationToken ct)
    {
        var q = db.ContactMessages.AsNoTracking().AsQueryable();
        if (unresolvedOnly)
            q = q.Where(m => !m.IsResolved);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim();
            q = q.Where(m => EF.Functions.Like(m.Name, $"%{s}%")
                          || EF.Functions.Like(m.Email, $"%{s}%")
                          || (m.Phone != null && EF.Functions.Like(m.Phone, $"%{s}%")));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(m => m.CreatedUtc)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .ToListAsync(ct);

        return new PagedResult<ContactMessageDto>(items.Select(Map).ToList(), total, request.Page, request.PageSize);
    }

    public async Task<ContactMessageDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var m = await db.ContactMessages.AsNoTracking()
            .Include(m => m.Replies)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        return m is null ? null : Map(m);
    }

    private static ContactMessageDto Map(Domain.Entities.ContactMessage m) => new(
        m.Id, m.Name, m.Email, m.Phone, m.Service, m.Details, m.CreatedUtc, m.IsResolved, m.ResolvedAtUtc,
        m.Replies.OrderBy(r => r.SentAtUtc)
            .Select(r => new ContactMessageReplyDto(r.Id, r.Subject, r.Body, r.SentAtUtc, r.SentByName)).ToList());
}
