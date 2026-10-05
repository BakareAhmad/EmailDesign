namespace DeChimex.Contracts.Invoices;

public sealed record InvoiceLineDto(
    Guid Id, Guid? ProductId, Guid? ServiceId, string Name, decimal UnitPrice,
    int Quantity, string? Notes, decimal LineTotal);

public sealed record InvoiceDocumentDto(
    Guid Id, string InvoiceNumber, string Status, DateTime IssuedAtUtc, DateTime? PaidAtUtc,
    Guid CustomerId, string CustomerName, string? CustomerEmail, string? CustomerPhone,
    Guid? OrderId, string? OrderNumber,
    decimal Subtotal, decimal Discount, decimal DeliveryFee, decimal Tax,
    decimal GrandTotal, string Currency, string? Notes, bool IsQuote,
    string ShareToken, List<InvoiceLineDto> Lines);

public sealed record ReceiptDocumentDto(
    Guid Id, string ReceiptNumber, Guid OrderId, string OrderNumber,
    Guid CustomerId, string CustomerName,
    decimal Amount, string Currency, string Method, string PaymentReference,
    DateTime PaidAtUtc, bool IsRefunded, string ShareToken,
    decimal OrderTotal = 0m, decimal ServiceFee = 0m);
