using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Payments;

/// <summary>
/// Customer-borne service fee — a configurable portion of Paystack's transaction charge
/// (service-fee feature 2026-09-23; generalizes the owner directive of 2026-09-08 under
/// which the customer bore 100% of the charge). Uses Paystack's published NGN local
/// pricing (support article "Transactions pricing", re-verified 2026-09-23): 1.5% + NGN 100
/// per transaction, total fee capped at NGN 2,000, NGN 100 waived for charges under NGN 2,500.
///
/// Circularity (service-fee brief §7): the service fee becomes part of the single amount
/// submitted to Paystack, so Paystack's fee is computed on (order + service fee). The
/// payable is therefore the fixed point T = O + y·fee(T), solved analytically from the
/// closed form of the official gross-up: T = (O + y·c) / (1 − y·r) in the uncapped regime
/// (c = flat ₦100, or 0 while the charge stays under the waiver threshold) and
/// T = O + y·K in the capped regime (K = ₦2,000). At y = 100% this reduces to the previous
/// full pass-through behaviour exactly. The regime-consistent solution also closes the
/// boundary gap where fee(order) is just under the cap but fee(charge) exceeds it.
///
/// Paystack's only other supported mechanism is the dashboard "Pass fees to customers"
/// toggle; that setting is manual, per-account and invisible to the app, so the
/// application-side implementation follows the documented pricing formulas instead.
/// If the toggle is ever enabled on the Paystack dashboard, set
/// Paystack:CustomerBorneCharge=false to avoid charging the fee twice.
/// </summary>
public sealed class PaystackChargeCalculator(IOptions<PaystackOptions> options) : IPaymentChargeCalculator
{
    private const decimal PercentFee = 0.015m;   // 1.5%
    private const decimal FlatFee = 100m;        // NGN 100
    private const decimal FeeCap = 2000m;        // NGN 2,000
    private const decimal FlatWaiverBelow = 2500m; // NGN 100 waived for charges under NGN 2,500

    public decimal ComputeCustomerBorneCharge(decimal orderAmount)
        => Compute(orderAmount, 100m).ServiceFeeAmount;

    public decimal ComputeCustomerPayable(decimal orderAmount)
        => Compute(orderAmount, 100m).CustomerPayable;

    public PaymentChargeBreakdown Compute(decimal orderAmount, decimal serviceFeePercent)
    {
        // Master switch: the dashboard-toggle safety override disables any pass-through.
        var percent = options.Value.CustomerBorneCharge ? serviceFeePercent : 0m;
        if (percent < 0m) percent = 0m;
        if (percent > 100m) percent = 100m;

        if (orderAmount <= 0m || percent == 0m)
            return new PaymentChargeBreakdown(orderAmount, FeeOnCharge(orderAmount), percent, 0m, orderAmount);

        var y = percent / 100m;

        // Regime C — flat fee waived (the charge stays under NGN 2,500): T = O / (1 − y·r).
        var waived = RoundUpToKobo(orderAmount / (1m - y * PercentFee));
        if (waived < FlatWaiverBelow && RawFeeOnCharge(waived) < FeeCap)
            return Finish(orderAmount, percent, waived);

        // Regime B — flat fee applies, total fee under the cap: T = (O + y·flat) / (1 − y·r).
        var withFlat = RoundUpToKobo((orderAmount + y * FlatFee) / (1m - y * PercentFee));
        if (withFlat >= FlatWaiverBelow && RawFeeOnCharge(withFlat) <= FeeCap)
            return Finish(orderAmount, percent, withFlat);

        // Regime A — the fee on the charge reaches the NGN 2,000 cap: T = O + y·cap.
        var capped = RoundUpToKobo(orderAmount + y * FeeCap);
        return Finish(orderAmount, percent, capped);
    }

    private static PaymentChargeBreakdown Finish(decimal orderAmount, decimal percent, decimal payable)
    {
        var serviceFee = payable - orderAmount;
        return new PaymentChargeBreakdown(orderAmount, FeeOnCharge(payable), percent, serviceFee, payable);
    }

    /// <summary>Paystack's published NGN local transaction fee for a charge of
    /// <paramref name="charge"/> naira (the amount the gateway actually processes).</summary>
    public static decimal FeeOnCharge(decimal charge)
    {
        var fee = RawFeeOnCharge(charge);
        return fee > FeeCap ? FeeCap : fee;
    }

    /// <summary>The pre-cap schedule fee (1.5% + flat/waived) — used for regime checks;
    /// <see cref="FeeOnCharge"/> caps it at NGN 2,000.</summary>
    private static decimal RawFeeOnCharge(decimal charge)
    {
        if (charge <= 0m) return 0m;
        var flat = charge < FlatWaiverBelow ? 0m : FlatFee;
        return PercentFee * charge + flat;
    }

    private static decimal RoundUpToKobo(decimal naira) => Math.Ceiling(naira * 100m) / 100m;
}
