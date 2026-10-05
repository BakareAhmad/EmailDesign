using DeChimex.Domain.Enums;
using DeChimex.Host.DevPortal;
using FluentAssertions;
using Xunit;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>Pure aggregation rules of the service-fee report (ServiceFeeReportService.Compute / Reconcile).</summary>
public class ServiceFeeCalculationTests
{
    private static readonly DateTime D1 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D2 = new(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Oct = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SepStart = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), OctStart = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), NovStart = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FeeRow Row(PaymentStatus s, decimal fee, decimal pct = 100m, DateTime? created = null, DateTime? paid = null, DateTime? refunded = null,
        decimal order = 10000m, decimal? psActual = null, Guid? orderId = null)
        => new(Guid.NewGuid(), orderId ?? Guid.NewGuid(), "REF-" + Guid.NewGuid().ToString("N")[..8], s, created ?? paid ?? D1, paid, refunded,
            order, order + fee, pct, fee, fee * 100m / Math.Max(pct, 1m), psActual);

    [Fact]
    public void Successful_payments_are_counted_with_value_average_and_paystack_fee()
    {
        var t = ServiceFeeReportService.Compute(new[]
        {
            Row(PaymentStatus.Succeeded, 250m, paid: D1, psActual: 250m),
            Row(PaymentStatus.Succeeded, 150m, paid: D2, order: 6000m)
        }, SepStart, OctStart);
        t.Transactions.Should().Be(2);
        t.GrossFees.Should().Be(400m);
        t.NetFees.Should().Be(400m);
        t.AverageFee.Should().Be(200m);
        t.TransactionValue.Should().Be(10250m + 6150m);
        t.OrderValue.Should().Be(16000m);
    }

    [Fact]
    public void Each_payment_keeps_its_own_historical_percentage()
    {
        var t = ServiceFeeReportService.Compute(new[]
        {
            Row(PaymentStatus.Succeeded, 250m, pct: 100m, paid: D1),
            Row(PaymentStatus.Succeeded, 125m, pct: 50m, paid: D2),
            Row(PaymentStatus.Succeeded, 120m, pct: 50m, paid: D2)
        }, SepStart, OctStart);
        t.Percentages.Should().HaveCount(2);
        t.Percentages.Single(p => p.Percent == 50m).Should().BeEquivalentTo(new { Transactions = 2, Fees = 245m });
        t.Percentages.Single(p => p.Percent == 100m).Fees.Should().Be(250m);
    }

    [Theory]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Abandoned)]
    [InlineData(PaymentStatus.Initialized)]
    [InlineData(PaymentStatus.Pending)]
    public void Failed_abandoned_and_unfinished_attempts_are_never_collected(PaymentStatus status)
    {
        var t = ServiceFeeReportService.Compute(new[] { Row(status, 300m, created: D1) }, SepStart, OctStart);
        t.Transactions.Should().Be(0);
        t.GrossFees.Should().Be(0m);
        t.NetFees.Should().Be(0m);
        t.NotCollectedAttempts.Should().Be(1);
        t.NotCollectedFees.Should().Be(300m);
    }

    [Fact]
    public void A_retry_after_a_failure_counts_the_fee_once()
    {
        var order = Guid.NewGuid();
        var t = ServiceFeeReportService.Compute(new[]
        {
            Row(PaymentStatus.Failed, 200m, created: D1, orderId: order),
            Row(PaymentStatus.Succeeded, 250m, paid: D1.AddMinutes(5), orderId: order)
        }, SepStart, OctStart);
        t.Transactions.Should().Be(1);
        t.GrossFees.Should().Be(250m);
        t.NotCollectedAttempts.Should().Be(1);
    }

    [Fact]
    public void A_refund_in_the_same_period_nets_to_zero()
    {
        var t = ServiceFeeReportService.Compute(new[] { Row(PaymentStatus.Refunded, 250m, paid: D1, refunded: D2) }, SepStart, OctStart);
        t.GrossFees.Should().Be(250m);
        t.ReversedFees.Should().Be(250m);
        t.RefundCount.Should().Be(1);
        t.NetFees.Should().Be(0m);
    }

    [Fact]
    public void A_refund_in_a_later_period_is_a_reversal_of_that_later_period_only()
    {
        var rows = new[] { Row(PaymentStatus.Refunded, 250m, paid: D1, refunded: Oct), Row(PaymentStatus.Succeeded, 100m, paid: Oct) };
        var sep = ServiceFeeReportService.Compute(rows, SepStart, OctStart);
        var oct = ServiceFeeReportService.Compute(rows, OctStart, NovStart);
        sep.NetFees.Should().Be(250m, "September keeps the fee collected in September");
        oct.GrossFees.Should().Be(100m);
        oct.ReversedFees.Should().Be(250m);
        oct.NetFees.Should().Be(-150m, "the reversal lands in the refund month, so a month can be net negative");
        ServiceFeeReportService.Compute(rows, DateTime.MinValue, DateTime.MaxValue).NetFees.Should().Be(100m, "all-time = only the unrefunded payment");
    }

    [Fact]
    public void A_failed_refund_leaves_the_payment_succeeded_and_the_fee_collected()
    {
        // RefundOrderCommand only marks the payment Refunded after Paystack confirms; a failed refund leaves it Succeeded.
        var t = ServiceFeeReportService.Compute(new[] { Row(PaymentStatus.Succeeded, 250m, paid: D1) }, SepStart, OctStart);
        t.NetFees.Should().Be(250m);
        t.RefundCount.Should().Be(0);
    }

    [Fact]
    public void Zero_percent_payments_are_transactions_with_no_fee()
    {
        var t = ServiceFeeReportService.Compute(new[] { Row(PaymentStatus.Succeeded, 0m, pct: 0m, paid: D1) }, SepStart, OctStart);
        t.Transactions.Should().Be(1);
        t.GrossFees.Should().Be(0m);
        t.Percentages.Single().Percent.Should().Be(0m);
    }

    [Fact]
    public void Ledger_reconciliation_matches_the_headline_aggregation()
    {
        var rows = new[]
        {
            Row(PaymentStatus.Succeeded, 250m, paid: D1), Row(PaymentStatus.Refunded, 180m, paid: D1, refunded: D2),
            Row(PaymentStatus.Refunded, 90m, paid: D2, refunded: Oct), Row(PaymentStatus.Failed, 300m, created: D2),
            Row(PaymentStatus.Succeeded, 75.55m, paid: Oct)
        };
        var ledger = rows.Select(r => new FeeLedgerRow(r.Id, r.OrderId, "DCX-1", "Direct", r.Reference, r.Status.ToString(), "x",
            r.CreatedUtc, r.PaidAtUtc, r.RefundedAtUtc, null, r.OrderAmount, r.Amount, r.Percent, r.Fee, r.PaystackFeeExpected, null,
            r.Status == PaymentStatus.Succeeded ? r.Fee : 0m)).ToList();
        foreach (var (from, to) in new[] { (SepStart, OctStart), (OctStart, NovStart), (DateTime.MinValue, DateTime.MaxValue) })
        {
            var h = ServiceFeeReportService.Compute(rows, from, to);
            ServiceFeeReportService.Reconcile(ledger, from, to).Should().Be((h.GrossFees, h.ReversedFees, h.NetFees));
        }
        ledger.Sum(r => r.NetFee).Should().Be(ServiceFeeReportService.Compute(rows, DateTime.MinValue, DateTime.MaxValue).NetFees,
            "summing the per-row net fee column equals the all-time net");
    }
}
