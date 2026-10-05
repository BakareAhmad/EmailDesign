namespace DeChimex.Host.Controllers;

// These read-model DTOs were originally declared inside the API InvoicesController and
// QuotationsController. Those API controllers were removed as superseded (an MVC equivalent
// exists), but the MVC views/controllers still consume the same DTO shapes, so they are kept
// here in the same namespace the MVC code already imports.
//
// The QuotationDto / QuotationLineDto read-models were removed (2026-09-01) along with the
// Quotation feature; quoting is now served by the Invoice "quote invoice" (Draft) path.

/// <summary>Invoice list read-model shared by the admin + portal invoice pages.</summary>
public sealed record InvoiceListDto(
    Guid Id, string InvoiceNumber, Guid? OrderId, string? OrderNumber, string CustomerName,
    decimal Subtotal, decimal Discount, decimal DeliveryFee, decimal Tax, decimal GrandTotal,
    string Currency, string Status, DateTime IssuedAtUtc, DateTime? PaidAtUtc, string? DocumentUrl,
    string? ShareToken, bool IsQuote);
