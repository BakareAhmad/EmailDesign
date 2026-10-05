namespace DeChimex.Application.Common.Interfaces;

/// <summary>Generates human-friendly, unique business reference numbers.</summary>
public interface IReferenceGenerator
{
    Task<string> NextOrderNumberAsync(CancellationToken ct = default);
    Task<string> NextInvoiceNumberAsync(CancellationToken ct = default);
    Task<string> NextQuoteNumberAsync(CancellationToken ct = default);
    Task<string> NextProcurementReferenceAsync(CancellationToken ct = default);
    Task<string> NextReceiptNumberAsync(CancellationToken ct = default);
    string NewPaymentReference();

    /// <summary>Opaque, unguessable token for public invoice/receipt share links.</summary>
    string NewShareToken();
}
