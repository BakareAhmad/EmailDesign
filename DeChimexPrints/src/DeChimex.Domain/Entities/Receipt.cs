using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Proof of payment, generated AFTER the order is paid (the invoice, in contrast, is
/// issued before payment). One receipt per paid order; immutable apart from refund state.
/// </summary>
public sealed class Receipt : AggregateRoot
{
    private Receipt() { } // EF

    private Receipt(string receiptNumber, Order order, Payment payment, string customerName)
    {
        ReceiptNumber = receiptNumber;
        OrderId = order.Id;
        OrderNumber = order.OrderNumber;
        CustomerId = order.CustomerId;
        CustomerName = customerName;
        Amount = payment.Amount.Copy(); // owned type: never share the instance with Payment
        // Service-fee snapshot (brief §5/§11): the document shows the split that was
        // actually applied to this payment, never today's global configuration.
        OrderTotal = Money.Of(payment.OrderAmount);
        ServiceFee = Money.Of(payment.CustomerBorneCharge);
        Method = payment.Method;
        PaymentReference = payment.Reference;
        PaidAtUtc = payment.PaidAtUtc ?? DateTime.UtcNow;
    }

    public string ReceiptNumber { get; private set; } = default!;
    /// <summary>Persistence-only escape hatch (AUD-004): when the unique reference index
    /// rejects a count-generated number under concurrency, the save boundary renumbers the
    /// conflicting document with a fresh random suffix and retries. Never call during normal
    /// business flow.</summary>
    public void RenumberAfterReferenceConflict(string newNumber) => ReceiptNumber = newNumber;

    public Guid OrderId { get; private set; }
    public string OrderNumber { get; private set; } = default!;
    public Guid CustomerId { get; private set; }

    /// <summary>Customer name snapshot so the document stays correct if the record is renamed.</summary>
    public string CustomerName { get; private set; } = default!;
    public Money Amount { get; private set; } = default!;

    /// <summary>Snapshot: the order (business) value this receipt settles.</summary>
    public Money OrderTotal { get; private set; } = default!;
    /// <summary>Snapshot: the service fee included in <see cref="Amount"/> (0 for fee-free payments).</summary>
    public Money ServiceFee { get; private set; } = default!;

    public PaymentMethod Method { get; private set; }
    public string PaymentReference { get; private set; } = default!;
    public DateTime PaidAtUtc { get; private set; }

    /// <summary>Opaque token for the public share/download link (/i/{token}).</summary>
    public string ShareToken { get; private set; } = default!;

    public bool IsRefunded { get; private set; }

    public static Receipt CreateForPayment(string receiptNumber, Order order, Payment payment, string customerName)
    {
        if (string.IsNullOrWhiteSpace(receiptNumber))
            throw new BusinessRuleValidationException("receipt.number", "Receipt number is required.");
        if (payment.Status != PaymentStatus.Succeeded)
            throw new BusinessRuleValidationException("receipt.payment", "A receipt can only be generated for a succeeded payment.");
        return new Receipt(receiptNumber, order, payment, customerName);
    }

    public void SetShareToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new BusinessRuleValidationException("receipt.share", "A share token is required.");
        ShareToken = token;
    }

    public void MarkRefunded() => IsRefunded = true;
}
