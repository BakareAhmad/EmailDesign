using System.Security.Cryptography;
using DeChimex.Domain.Common;
using DeChimex.Domain.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Context;

/// <summary>
/// Commits all tracked changes in one transaction and then dispatches the domain events
/// collected on aggregates (after a successful save, so handlers observe persisted state).
/// </summary>
public sealed class UnitOfWork(DeChimexDbContext context, IPublisher publisher) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var aggregatesWithEvents = context.ChangeTracker
            .Entries<AggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count != 0)
            .Select(e => e.Entity)
            .ToList();

        var domainEvents = aggregatesWithEvents.SelectMany(a => a.DomainEvents).ToList();

        int result;
        try
        {
            // AUD-004: reference numbers (DCX-/INV-/RCT-/PRC-) are generated from a row
            // count, so two concurrent orders can draw the same number; the unique indexes
            // then reject the loser at save time. Instead of surfacing that as a failed
            // checkout, renumber the conflicting aggregate with a random suffix and retry.
            var referenceRetries = 0;
            while (true)
            {
                try
                {
                    result = await context.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateException ex) when (++referenceRetries <= 2 && TryRenumberReferenceConflicts(ex))
                {
                    // Reference numbers regenerated with a fresh suffix; retry the save.
                }
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
            && (sql.Number == 2601 || sql.Number == 2627)
            && ex.Entries.Any(e => e.Entity is DeChimex.Domain.Entities.MajorCategory or DeChimex.Domain.Entities.Category))
        {
            throw new DeChimex.Application.Common.Exceptions.ConflictException("category.duplicate", "A category with this name or URL already exists. Choose a different name.");
        }
        catch (DbUpdateConcurrencyException)
        {
            // AUD-002: translate into an application-level exception so handlers can
            // retry-and-recheck without referencing EF Core.
            throw new DeChimex.Application.Common.Exceptions.ConcurrencyConflictException();
        }

        foreach (var aggregate in aggregatesWithEvents)
            aggregate.ClearDomainEvents();

        foreach (var domainEvent in domainEvents)
            await publisher.Publish(domainEvent, cancellationToken);

        return result;
    }

    /// <summary>
    /// True when the failed save is a UNIQUE-constraint violation on one of the count-based
    /// reference-number properties (Order.OrderNumber, Invoice.InvoiceNumber,
    /// Receipt.ReceiptNumber, Procurement.Reference) AND a fresh number was assigned to the
    /// conflicting instance(s). Detected for SQL Server (error 2601/2627) and, best-effort,
    /// for providers that report unique-index violations in the message (e.g. InMemory).
    /// </summary>
    private static bool TryRenumberReferenceConflicts(DbUpdateException ex)
    {
        var isSqlUnique = ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
            && (sql.Number == 2601 || sql.Number == 2627);
        var message = ex.InnerException?.Message ?? ex.Message;
        var isGenericUnique = message.Contains("unique index", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase);
        if (!isSqlUnique && !isGenericUnique)
            return false;

        var regenerated = false;
        foreach (var entry in ex.Entries)
        {
            switch (entry.Entity)
            {
                case DeChimex.Domain.Entities.Order o:
                    o.RenumberAfterReferenceConflict(WithFallbackSuffix(o.OrderNumber));
                    regenerated = true;
                    break;
                case DeChimex.Domain.Entities.Invoice i:
                    i.RenumberAfterReferenceConflict(WithFallbackSuffix(i.InvoiceNumber));
                    regenerated = true;
                    break;
                case DeChimex.Domain.Entities.Receipt r:
                    r.RenumberAfterReferenceConflict(WithFallbackSuffix(r.ReceiptNumber));
                    regenerated = true;
                    break;
                case DeChimex.Domain.Entities.Procurement p:
                    p.RenumberAfterReferenceConflict(WithFallbackSuffix(p.Reference));
                    regenerated = true;
                    break;
            }
        }
        return regenerated;
    }

    /// <summary>Keeps the friendly prefix and appends an unambiguous random suffix
    /// (e.g. DCX-2026-000123-R7QK). 4 characters of a 32-symbol alphabet keeps the
    /// collision probability negligible while staying well inside the 40-char columns.</summary>
    private static string WithFallbackSuffix(string original)
    {
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        Span<char> suffix = stackalloc char[4];
        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        var candidate = $"{original}-R{new string(suffix)}";
        return candidate.Length <= 40 ? candidate : candidate[..40];
    }
}
