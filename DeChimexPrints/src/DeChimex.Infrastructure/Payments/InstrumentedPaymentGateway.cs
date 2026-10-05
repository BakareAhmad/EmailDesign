using System.Diagnostics;
using DeChimex.Application.Common.Interfaces;

namespace DeChimex.Infrastructure.Payments;

/// <summary>
/// Decorator that records every Paystack call (initialize / verify / refund) as an
/// integration-call row for Developer Portal payment diagnostics: outcome, duration, the
/// payment reference and a short safe message. Never records request/response bodies,
/// customer email or card data. Behaviour of the wrapped gateway is unchanged: results and
/// exceptions pass through untouched.
/// </summary>
public sealed class InstrumentedPaymentGateway(IPaymentGateway inner, IIntegrationCallRecorder recorder) : IPaymentGateway
{
    public async Task<PaymentInitializationResult> InitializeAsync(PaymentInitializationRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await inner.InitializeAsync(request, ct);
            recorder.Record(new IntegrationCallEntry(IntegrationNames.Paystack, "initialize",
                result.Success ? "Success" : "Failure", result.Success, sw.ElapsedMilliseconds,
                Reference: request.Reference, Detail: result.Success ? $"Amount {request.Amount:0.00} {request.Currency}" : result.Message));
            return result;
        }
        catch (Exception ex)
        {
            recorder.Record(new IntegrationCallEntry(IntegrationNames.Paystack, "initialize", "Exception", false,
                sw.ElapsedMilliseconds, Reference: request.Reference, Detail: $"{ex.GetType().Name}: {ex.Message}"));
            throw;
        }
    }

    public async Task<PaymentVerificationResult> VerifyAsync(string reference, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await inner.VerifyAsync(reference, ct);
            // Pending is a legitimate provider state, not a failure; the call itself succeeded
            // unless the gateway could not be reached (message says so).
            var unreachable = result.Outcome == PaymentVerificationOutcome.Pending
                && (result.RawResponse is null
                    || (result.Message?.StartsWith("The payment could not be verified", StringComparison.Ordinal) ?? false));
            recorder.Record(new IntegrationCallEntry(IntegrationNames.Paystack, "verify",
                unreachable ? "Unreachable" : result.Outcome.ToString(), !unreachable, sw.ElapsedMilliseconds,
                Reference: reference,
                Detail: result.Outcome == PaymentVerificationOutcome.Succeeded
                    ? $"Paid {result.AmountPaid:0.00} {result.Currency}" + (result.Fees is { } f ? $", Paystack fee {f:0.00}" : string.Empty)
                    : result.Message));
            return result;
        }
        catch (Exception ex)
        {
            recorder.Record(new IntegrationCallEntry(IntegrationNames.Paystack, "verify", "Exception", false,
                sw.ElapsedMilliseconds, Reference: reference, Detail: $"{ex.GetType().Name}: {ex.Message}"));
            throw;
        }
    }

    public bool VerifyWebhookSignature(string requestBody, string signatureHeader)
        => inner.VerifyWebhookSignature(requestBody, signatureHeader);

    public async Task<RefundResult> RefundAsync(string reference, decimal? amount, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await inner.RefundAsync(reference, amount, ct);
            recorder.Record(new IntegrationCallEntry(IntegrationNames.Paystack, "refund",
                result.Success ? "Success" : "Failure", result.Success, sw.ElapsedMilliseconds,
                Reference: reference, Detail: result.Message));
            return result;
        }
        catch (Exception ex)
        {
            recorder.Record(new IntegrationCallEntry(IntegrationNames.Paystack, "refund", "Exception", false,
                sw.ElapsedMilliseconds, Reference: reference, Detail: $"{ex.GetType().Name}: {ex.Message}"));
            throw;
        }
    }
}
