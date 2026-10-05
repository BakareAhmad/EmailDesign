using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Repositories;

namespace DeChimex.Persistence.Repositories;

/// <summary>
/// Generates sequential, human-friendly references per year (e.g. DCX-2026-000042).
/// Payment references are opaque and globally unique.
/// </summary>
public sealed class ReferenceGenerator(
    IOrderRepository orders,
    IInvoiceRepository invoices,
    IProcurementRepository procurements,
    IReceiptRepository receipts) : IReferenceGenerator
{
    public async Task<string> NextOrderNumberAsync(CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var count = await orders.CountForYearAsync(year, ct);
        return $"DCX-{year}-{(count + 1):D6}";
    }

    public async Task<string> NextInvoiceNumberAsync(CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var count = await invoices.CountForYearAsync(year, ct);
        return $"INV-{year}-{(count + 1):D6}";
    }

    /// <summary>Quote-only documents run on their own QTE sequence. When a quote is
    /// adopted by an order it is renumbered onto the INV sequence, so paid documents
    /// always read INV-… while unconverted quotes stay clearly labelled QTE-….</summary>
    public async Task<string> NextQuoteNumberAsync(CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var count = await invoices.CountQuotesForYearAsync(year, ct);
        return $"QTE-{year}-{(count + 1):D6}";
    }

    public async Task<string> NextProcurementReferenceAsync(CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var count = await procurements.CountForYearAsync(year, ct);
        return $"PRC-{year}-{(count + 1):D6}";
    }

    public async Task<string> NextReceiptNumberAsync(CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var count = await receipts.CountForYearAsync(year, ct);
        return $"RCT-{year}-{(count + 1):D6}";
    }

    public string NewPaymentReference()
        => $"PAY-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("n")[..10].ToUpperInvariant()}";

    public string NewShareToken() => Guid.NewGuid().ToString("N");
}
