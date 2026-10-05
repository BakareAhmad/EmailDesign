using DeChimex.Infrastructure.Options;
using DeChimex.Infrastructure.Payments;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

/// <summary>
/// Service fee = Paystack fee × Y/100 (brief §7): the fee is computed on the amount actually
/// charged (order + service fee), so the payable is the closed-form fixed point of the
/// gross-up — never a percentage of the order subtotal, never self-referencing.
/// </summary>
public class ServiceFeeChargeCalculatorTests
{
    private static PaystackChargeCalculator Calc(bool customerBorne = true) =>
        new(Options.Create(new PaystackOptions { CustomerBorneCharge = customerBorne }));

    /// <summary>Paystack's published local fee on the charge (1.5% + ₦100, waived under
    /// ₦2,500, capped ₦2,000) — an independent restatement used as the oracle.</summary>
    private static decimal OracleFee(decimal charge)
    {
        if (charge <= 0) return 0;
        var flat = charge < 2500m ? 0m : 100m;
        return Math.Min(0.015m * charge + flat, 2000m);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(9500)]
    [InlineData(50000)]
    [InlineData(150000)]
    public void Zero_percent_charges_no_service_fee(decimal order)
    {
        var d = Calc().Compute(order, 0m);
        d.ServiceFeeAmount.Should().Be(0m);
        d.CustomerPayable.Should().Be(order, "0% must leave the payable exactly at the order total (no kobo drift)");
        d.ServiceFeePercent.Should().Be(0m);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(12.5)]
    [InlineData(33.33)]
    public void Service_fee_is_the_configured_percent_of_the_paystack_fee_on_the_charge(decimal y)
    {
        foreach (var order in new[] { 1000m, 2440m, 9500m, 20000m, 125900m, 150000m, 500000m })
        {
            var d = Calc().Compute(order, y);
            d.CustomerPayable.Should().Be(order + d.ServiceFeeAmount, "the payable is order + service fee");
            var fee = OracleFee(d.CustomerPayable);
            (d.ServiceFeeAmount - (y / 100m) * fee).Should().BeInRange(-0.02m, 0.02m,
                $"at {y}% the service fee must equal {y}% of the Paystack fee {fee} on the charge (kobo rounding only), not a percent of the order");
            if (y < 100)
                d.ServiceFeeAmount.Should().BeLessThan(order, "the service fee is a cut of the fee — never a percent of the subtotal");
        }
    }

    [Fact]
    public void Hundred_percent_matches_the_legacy_full_passthrough_exactly()
    {
        foreach (var order in new[] { 1000m, 2440m, 9500m, 20000m, 150000m, 500000m })
        {
            var d = Calc().Compute(order, 100m);
            d.ServiceFeeAmount.Should().Be(Calc().ComputeCustomerBorneCharge(order));
            d.CustomerPayable.Should().Be(Calc().ComputeCustomerPayable(order));
        }
    }

    [Fact]
    public void Golden_values_match_the_documented_examples()
    {
        // ₦9,500 order at 100%: the proven ₦9,746.20 payable (gross-up (O+100)/0.985).
        var full = Calc().Compute(9500m, 100m);
        full.CustomerPayable.Should().Be(9746.20m);
        full.ServiceFeeAmount.Should().Be(246.20m);

        // ₦9,500 at 50%: half of the fee on the charge.
        var half = Calc().Compute(9500m, 50m);
        half.CustomerPayable.Should().Be(9622.17m);
        half.ServiceFeeAmount.Should().Be(122.17m);

        // Cap zone at 50%: the fee is the ₦2,000 cap, half is passed on exactly.
        var capped = Calc().Compute(150000m, 50m);
        capped.ServiceFeeAmount.Should().Be(1000.00m);
        capped.CustomerPayable.Should().Be(151000.00m);
        capped.PaystackFee.Should().Be(2000.00m);
    }

    [Fact]
    public void Cap_boundary_is_solved_consistently()
    {
        // The fee on the bare order is just under the cap while the fee on the charge
        // exceeds it — the payable is the capped regime (O + y·cap), never the
        // uncapped gross-up (which would over-charge by ~₦116).
        var d = Calc().Compute(125900m, 100m);
        d.CustomerPayable.Should().Be(127900.00m);
        d.PaystackFee.Should().Be(2000.00m);

        var half = Calc().Compute(125900m, 50m);
        half.CustomerPayable.Should().Be(126900.00m);
        half.ServiceFeeAmount.Should().Be(1000.00m);
    }

    [Fact]
    public void Waiver_regime_applies_under_the_threshold()
    {
        // ₦1,000 order: the ₦100 flat is waived, fee is 1.5% of the charge only.
        var d = Calc().Compute(1000m, 100m);
        d.CustomerPayable.Should().Be(1015.23m);
        d.ServiceFeeAmount.Should().Be(15.23m);
    }

    [Fact]
    public void Toggle_off_forces_zero_service_fee_regardless_of_configuration()
    {
        var d = Calc(customerBorne: false).Compute(9500m, 100m);
        d.ServiceFeeAmount.Should().Be(0m);
        d.CustomerPayable.Should().Be(9500m);
    }

    [Fact]
    public void Out_of_range_percentages_are_clamped_not_trusted()
    {
        Calc().Compute(9500m, -5m).ServiceFeeAmount.Should().Be(0m, "negative percentages act as 0%");
        Calc().Compute(9500m, 150m).ServiceFeeAmount.Should().Be(246.20m, "over-maximum percentages act as 100%");
    }
}
