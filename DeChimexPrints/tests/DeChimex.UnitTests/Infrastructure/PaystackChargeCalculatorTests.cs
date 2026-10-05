using DeChimex.Infrastructure.Options;
using DeChimex.Infrastructure.Payments;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure;

/// <summary>
/// Customer-borne Paystack charge (owner directive 2026-09-08). The formulas follow
/// Paystack's published NGN pricing: 1.5% + NGN100, capped at NGN2,000, NGN100 waived for
/// charges under NGN2,500, with the official gross-up so the business nets the exact
/// order value. The business order value must never absorb the gateway fee.
/// </summary>
public class PaystackChargeCalculatorTests
{
    private static PaystackChargeCalculator Calc(bool customerBorne = true) =>
        new(Options.Create(new PaystackOptions
        {
            SecretKey = "sk_test_x",
            PublicKey = "pk_test_x",
            CustomerBorneCharge = customerBorne
        }));

    /// <summary>Net to the business after Paystack deducts its published fee from the charge.</summary>
    private static decimal BusinessNet(decimal finalPayable)
    {
        const decimal f = 0.015m;
        var fee = f * finalPayable + (finalPayable < 2500m ? 0m : 100m);
        fee = Math.Min(fee, 2000m);
        return finalPayable - fee;
    }

    [Theory]
    [InlineData(1000)]     // waiver branch (charge < 2500)
    [InlineData(2450)]     // waiver branch near the threshold
    [InlineData(2470)]     // flat-fee branch just above the threshold
    [InlineData(5000)]     // flat-fee branch
    [InlineData(50000)]    // the brief's example order size
    [InlineData(150000)]   // larger flat-fee orders
    [InlineData(133000)]   // near the cap crossover
    [InlineData(200000)]   // capped branch (fee at NGN 2,000)
    [InlineData(500000)]   // deep in the capped branch
    public void Business_nets_at_least_the_order_value_in_every_branch(decimal orderAmount)
    {
        var calculator = Calc();
        var charge = calculator.ComputeCustomerBorneCharge(orderAmount);
        var payable = calculator.ComputeCustomerPayable(orderAmount);

        charge.Should().BeGreaterThan(0, "the customer must bear the gateway fee");
        payable.Should().Be(orderAmount + charge);
        BusinessNet(payable).Should().BeGreaterThanOrEqualTo(orderAmount,
            "the business order value must never absorb the Paystack charge");
        // No material over-collection: at most one kobo of rounding headroom.
        BusinessNet(payable).Should().BeLessThanOrEqualTo(orderAmount + 0.01m);
    }

    [Fact]
    public void Small_order_uses_the_waived_flat_fee()
    {
        // NGN 1,000 -> fee is 1.5% only (NGN100 waived under NGN2,500): payable ~= 1015.23
        var payable = Calc().ComputeCustomerPayable(1000m);
        payable.Should().BeInRange(1015.22m, 1015.24m);
    }

    [Fact]
    public void Large_order_is_capped_at_two_thousand()
    {
        // NGN 500,000 -> the percentage fee alone exceeds the cap, so the customer pays +2000.
        var payable = Calc().ComputeCustomerPayable(500000m);
        payable.Should().Be(502000m);
    }

    [Fact]
    public void Brief_example_order_shows_customer_borne_charge_on_top()
    {
        var calculator = Calc();
        var payable = calculator.ComputeCustomerPayable(50000m);
        var charge = payable - 50000m;

        // 1.5% + 100 grossed up: (50000+100)/0.985 ~= 50,862.95
        payable.Should().BeInRange(50862.94m, 50862.96m);
        charge.Should().BeInRange(862.94m, 862.96m);
    }

    [Fact]
    public void Charge_is_zero_when_disabled_via_configuration()
    {
        // The dashboard "Pass fees to customers" toggle scenario: the app must not add it twice.
        var calculator = Calc(customerBorne: false);
        calculator.ComputeCustomerBorneCharge(50000m).Should().Be(0m);
        calculator.ComputeCustomerPayable(50000m).Should().Be(50000m);
    }

    [Fact]
    public void Non_positive_amounts_carry_no_charge()
    {
        Calc().ComputeCustomerPayable(0m).Should().Be(0m);
        Calc().ComputeCustomerBorneCharge(-5m).Should().Be(0m);
    }
}
