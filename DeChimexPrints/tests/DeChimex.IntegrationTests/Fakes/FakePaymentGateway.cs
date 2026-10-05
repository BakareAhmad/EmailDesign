using System.Collections.Concurrent;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;

namespace DeChimex.IntegrationTests.Fakes;

/// <summary>
/// LOW-008: deterministic in-memory payment gateway that replaces the real Paystack HTTP gateway
/// in integration tests, so payment flows can be exercised end-to-end without any network calls.
/// Behaviour is toggled via instance flags so tests can drive success, failure and pending paths.
/// Mirrors the real gateway's amount behaviour: verification reports exactly the amount the
/// transaction was initialized for (tests of the amount-mismatch guard override VerifyAmountPaid).
/// </summary>
public sealed class FakePaymentGateway : IPaymentGateway
{
    public bool SignatureValid { get; set; } = true;
    public bool RefundSucceeds { get; set; } = true;
    public string? RefundFailureMessage { get; set; }

    /// <summary>Terminal failure when false (kept for existing tests); see <see cref="VerifyOutcome"/>.</summary>
    public bool VerifySucceeds
    {
        get => VerifyOutcome == PaymentVerificationOutcome.Succeeded;
        set => VerifyOutcome = value
            ? PaymentVerificationOutcome.Succeeded
            : PaymentVerificationOutcome.Failed;
    }

    /// <summary>Drives the verification outcome (Succeeded / Failed / Pending).</summary>
    public PaymentVerificationOutcome VerifyOutcome { get; set; } = PaymentVerificationOutcome.Succeeded;

    /// <summary>When set, verification reports this amount instead of the initialized one.</summary>
    public decimal? VerifyAmountPaid { get; set; }

    /// <summary>When set, verification reports this currency instead of the initialized one.</summary>
    public string? VerifyCurrency { get; set; }

    /// <summary>When set, verification reports this Paystack transaction fee (naira) on success.</summary>
    public decimal? VerifyFees { get; set; }

    public PaymentInitializationRequest? LastInitialization { get; private set; }
    public string? LastRefundReference { get; private set; }
    public decimal? LastRefundAmount { get; private set; }
    public List<string> VerifyCalls { get; } = new();

    private readonly ConcurrentDictionary<string, decimal> _initializedAmounts = new();
    private readonly ConcurrentDictionary<string, string> _initializedCurrencies = new();

    public Task<PaymentInitializationResult> InitializeAsync(PaymentInitializationRequest request, CancellationToken ct = default)
    {
        LastInitialization = request;
        _initializedAmounts[request.Reference] = request.Amount;
        _initializedCurrencies[request.Reference] = request.Currency;
        return Task.FromResult(new PaymentInitializationResult(true, "https://fake.paystack/authorize", "fake-gw-ref", null));
    }

    public Task<PaymentVerificationResult> VerifyAsync(string reference, CancellationToken ct = default)
    {
        VerifyCalls.Add(reference);
        var amount = VerifyAmountPaid ?? _initializedAmounts.GetValueOrDefault(reference);
        var currency = VerifyCurrency ?? _initializedCurrencies.GetValueOrDefault(reference, "NGN");
        var result = VerifyOutcome switch
        {
            PaymentVerificationOutcome.Succeeded => new PaymentVerificationResult(
                PaymentVerificationOutcome.Succeeded, "fake-gw-ref", amount, currency, "{}", null, VerifyFees),
            PaymentVerificationOutcome.Failed => new PaymentVerificationResult(
                PaymentVerificationOutcome.Failed, null, 0, currency, "{}", "Card declined"),
            _ => new PaymentVerificationResult(
                PaymentVerificationOutcome.Pending, null, 0, currency, "{}", "The payment is still being confirmed.")
        };
        return Task.FromResult(result);
    }

    public bool VerifyWebhookSignature(string requestBody, string signatureHeader) => SignatureValid;

    public Dictionary<string,bool> RefundSuccessByReference { get; } = new();
    public List<string> RefundCalls { get; } = new();

    public Task<RefundResult> RefundAsync(string reference, decimal? amount, CancellationToken ct = default)
    {
        RefundCalls.Add(reference);
        LastRefundReference = reference;
        LastRefundAmount = amount;
        
        if (!RefundSuccessByReference.GetValueOrDefault(reference, RefundSucceeds))
        {
            return Task.FromResult(new RefundResult(false, RefundFailureMessage ?? "Refund failed", null));
        }
        
        return Task.FromResult(new RefundResult(true, null, "fake-refund-ref-" + reference));
    }
}
