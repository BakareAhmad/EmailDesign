using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A payment attempt against an order. Idempotent verification is enforced by the
/// unique provider reference so duplicate webhook calls cannot double-confirm.
/// </summary>
public sealed class Payment : AggregateRoot
{
    private Payment() { } // EF

    private Payment(Guid orderId, Guid customerId, Money amount, PaymentMethod method, string reference, decimal customerBorneCharge)
    {
        OrderId = orderId;
        CustomerId = customerId;
        Amount = amount;
        Method = method;
        Reference = reference;
        CustomerBorneCharge = customerBorneCharge;
        Status = PaymentStatus.Initialized;
    }

    public Guid OrderId { get; private set; }
    public Guid CustomerId { get; private set; }

    /// <summary>Final customer-payable amount charged at the gateway (order amount plus
    /// any customer-borne gateway charge).</summary>
    public Money Amount { get; private set; } = default!;

    /// <summary>The service fee charged to the customer — the configured portion of the
    /// Paystack transaction charge passed on at initialization, included in
    /// <see cref="Amount"/>. Zero for non-gateway methods. The business order value is
    /// always the order's GrandTotal; this portion of the payment goes to the gateway.</summary>
    public decimal CustomerBorneCharge { get; private set; }

    // --- Service-fee snapshot (brief §5): the configuration and fee facts as they applied
    // to THIS attempt, so later global configuration changes can never rewrite history.

    /// <summary>The order (business) value this payment settles. Amount = OrderAmount + CustomerBorneCharge.</summary>
    public decimal OrderAmount { get; private set; }

    /// <summary>The service-fee percentage configured when this attempt was initialized.</summary>
    public decimal ServiceFeePercent { get; private set; }

    /// <summary>Snapshot: Paystack's transaction fee (published schedule) computed for this attempt's charge.</summary>
    public decimal PaystackFeeExpected { get; private set; }

    /// <summary>The actual fee Paystack charged, captured from the verify response (`fees`) on success.</summary>
    public decimal? PaystackFeeActual { get; private set; }

    public PaymentMethod Method { get; private set; }
    public PaymentStatus Status { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token (AUD-002): database-generated rowversion so two
    /// concurrent confirmations of the same payment (webhook vs browser verify) cannot
    /// both commit "Succeeded" and both run the post-payment pipeline.
    /// </summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    /// <summary>Settlement lifecycle of a refund (AUD-005): Requested when the provider
    /// accepts the refund request, Processed/Failed when Paystack reports the refund.*
    /// settlement event. Manual (offline) refunds are Processed immediately.</summary>
    public RefundStatus RefundStatus { get; private set; }

    /// <summary>Our internal, unique transaction reference sent to the gateway.</summary>
    public string Reference { get; private set; } = default!;

    /// <summary>Gateway-side identifier / reference returned on verification.</summary>
    public string? GatewayReference { get; private set; }
    public string? AuthorizationUrl { get; private set; }
    public string? RawResponse { get; private set; }
    public DateTime? PaidAtUtc { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>Paystack's refund reference for this payment (from /refund endpoint response).</summary>
    public string? RefundReference { get; private set; }
    /// <summary>When the refund was processed by Paystack.</summary>
    public DateTime? RefundedAtUtc { get; private set; }

    public static Payment Initialize(Guid orderId, Guid customerId, decimal amount, PaymentMethod method, string reference,
        decimal customerBorneCharge = 0m, decimal? orderAmount = null, decimal serviceFeePercent = 0m, decimal paystackFeeExpected = 0m)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new BusinessRuleValidationException("payment.reference", "Payment reference is required.");
        if (customerBorneCharge < 0)
            throw new BusinessRuleValidationException("payment.charge", "The customer-borne charge cannot be negative.");
        if (serviceFeePercent is < 0 or > 100)
            throw new BusinessRuleValidationException("payment.service_fee_percent", "The service-fee percentage must be between 0 and 100.");
        var payment = new Payment(orderId, customerId, Money.Of(amount), method, reference, customerBorneCharge);
        payment.OrderAmount = orderAmount ?? amount - customerBorneCharge;
        payment.ServiceFeePercent = serviceFeePercent;
        payment.PaystackFeeExpected = paystackFeeExpected;
        return payment;
    }

    public void SetAuthorizationUrl(string url) => AuthorizationUrl = url;

    /// <summary>Idempotent success confirmation. Returns false if already succeeded.
    /// <paramref name="paystackFeeActual"/> captures the authoritative fee Paystack
    /// reported for this transaction (verify response `fees`) into the snapshot.</summary>
    public bool MarkSucceeded(string gatewayReference, string? rawResponse, decimal? paystackFeeActual = null)
    {
        if (Status is PaymentStatus.Succeeded or PaymentStatus.Refunded) return false; // duplicate webhook; ignore
        Status = PaymentStatus.Succeeded;
        GatewayReference = gatewayReference;
        RawResponse = rawResponse;
        PaystackFeeActual = paystackFeeActual ?? PaystackFeeActual;
        PaidAtUtc = DateTime.UtcNow;
        FailureReason = null;
        return true;
    }

    public void MarkFailed(string reason, string? rawResponse)
    {
        if (Status is PaymentStatus.Succeeded or PaymentStatus.Refunded) return;
        // Idempotent: a repeat verification of an already-failed attempt updates the
        // recorded reason but must NOT raise another PaymentFailedEvent (no duplicate
        // failure emails / notifications for the same attempt).
        var firstFailure = Status != PaymentStatus.Failed;
        Status = PaymentStatus.Failed;
        FailureReason = reason;
        RawResponse = rawResponse;
        if (firstFailure)
            RaiseDomainEvent(new PaymentFailedEvent(OrderId, Reference, Amount.Amount, reason));
    }

    /// <summary>
    /// Marks this payment as refunded. Only call after Paystack confirms the refund.
    /// </summary>
    /// <param name="refundReference">The refund reference returned by Paystack's /refund endpoint.</param>
    public void MarkRefunded(string? refundReference = null)
    {
        Status = PaymentStatus.Refunded;
        RefundReference = refundReference;
        RefundedAtUtc = DateTime.UtcNow;
        // Provider ACCEPTED the request — settlement is confirmed separately by the
        // refund.processed webhook (AUD-005). Manual refunds (no provider) are marked
        // settled right away via MarkRefundProcessed by the caller.
        if (RefundStatus is RefundStatus.None or RefundStatus.Failed)
            RefundStatus = RefundStatus.Requested;
    }

    /// <summary>Paystack reports the refund settled (refund.processed webhook, AUD-005). Idempotent.</summary>
    public void MarkRefundProcessed() { if (Status == PaymentStatus.Refunded) RefundStatus = RefundStatus.Processed; }

    /// <summary>Paystack reports the refund failed at settlement (refund.failed webhook, AUD-005).
    /// The payment keeps Status=Refunded (the books were already reversed); operations must
    /// reconcile manually — the handler surfaces this as an attention alert.</summary>
    public void MarkRefundFailed() { if (Status == PaymentStatus.Refunded) RefundStatus = RefundStatus.Failed; }

    /// <summary>Manual (bank transfer / cash) refunds settle offline: mark them settled immediately.</summary>
    public void MarkRefundProcessedLocally() { if (Status == PaymentStatus.Refunded) RefundStatus = RefundStatus.Processed; }
}
