namespace DeChimex.Contracts.Payments;

public sealed record InitializePaymentRequest(Guid OrderId, string Method);
public sealed record PaymentInitResponse(Guid PaymentId, string Reference, string? AuthorizationUrl, string Status);
public sealed record VerifyPaymentRequest(string Reference);
public sealed record PaymentDto(Guid Id, Guid OrderId, decimal Amount, string Currency, string Method, string Status, string Reference, string? GatewayReference, DateTime? PaidAtUtc, DateTime CreatedDateUtc, string? FailureReason = null,
    decimal OrderAmount = 0m, decimal ServiceFeePercent = 0m, decimal ServiceFeeAmount = 0m, decimal PaystackFeeExpected = 0m, decimal? PaystackFeeActual = null);
public sealed record InvoiceDto(Guid Id, string InvoiceNumber, Guid? OrderId, decimal Subtotal, decimal Discount, decimal DeliveryFee, decimal Tax, decimal GrandTotal, string Currency, string Status, DateTime IssuedAtUtc, DateTime? PaidAtUtc, string? DocumentUrl);
