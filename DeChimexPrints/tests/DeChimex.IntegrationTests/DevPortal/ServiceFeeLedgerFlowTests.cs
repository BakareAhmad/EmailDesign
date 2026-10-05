using System.Net;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;
using DeChimex.Host.DevPortal;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>
/// End-to-end: real checkouts (direct + custom), percentage change, failure, retry, refund and a
/// failed refund, then the Developer Portal overview / ledger / detail / CSV must agree to the kobo.
/// Own factory = isolated payments table.
/// </summary>
public class ServiceFeeLedgerFlowTests : IClassFixture<DevPortalFactory>
{
    private readonly DevPortalFactory _f;
    public ServiceFeeLedgerFlowTests(DevPortalFactory f) => _f = f;

    [Fact]
    public async Task Portal_totals_ledger_detail_and_csv_reconcile_across_all_payment_scenarios()
    {
        var gw = _f.Gateway;
        gw.VerifyOutcome = PaymentVerificationOutcome.Succeeded; gw.RefundSucceeds = true; gw.VerifyFees = null;

        // A: direct order at 100 %, succeeds.
        await FeeFlow.SetPercentAsync(_f, 100m);
        var (orderA, _) = await FeeFlow.PlaceOrderAsync(_f, 10000m, custom: false);
        var (payA, refA) = await FeeFlow.InitializeAsync(_f, orderA);
        gw.VerifyFees = 250m;
        await FeeFlow.VerifyAsync(_f, refA);
        gw.VerifyFees = null;

        // Configuration changes to 50 %: A must keep 100 %.
        await FeeFlow.SetPercentAsync(_f, 50m);

        // B: custom order at 50 %, succeeds.
        var (orderB, _) = await FeeFlow.PlaceOrderAsync(_f, 12000m, custom: true);
        var (payB, refB) = await FeeFlow.InitializeAsync(_f, orderB);
        await FeeFlow.VerifyAsync(_f, refB);

        // C: first attempt fails, retry succeeds (fee counted once).
        var (orderC, _) = await FeeFlow.PlaceOrderAsync(_f, 8000m, custom: false);
        var (payC1, refC1) = await FeeFlow.InitializeAsync(_f, orderC);
        gw.VerifyOutcome = PaymentVerificationOutcome.Failed;
        await FeeFlow.VerifyAsync(_f, refC1);
        gw.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        var (payC2, refC2) = await FeeFlow.InitializeAsync(_f, orderC);
        await FeeFlow.VerifyAsync(_f, refC2);

        // D: abandoned (initialized, never completed).
        var (orderD, _) = await FeeFlow.PlaceOrderAsync(_f, 3000m, custom: false);
        var (payD, _) = await FeeFlow.InitializeAsync(_f, orderD);

        // E: succeeds then fully refunded (fee reversed).
        var (orderE, _) = await FeeFlow.PlaceOrderAsync(_f, 5000m, custom: false);
        var (payE, refE) = await FeeFlow.InitializeAsync(_f, orderE);
        await FeeFlow.VerifyAsync(_f, refE);
        await FeeFlow.RefundAsync(_f, orderE);

        // F: succeeds, refund attempt FAILS at Paystack (fee stays collected).
        var (orderF, _) = await FeeFlow.PlaceOrderAsync(_f, 4000m, custom: false);
        var (payF, refF) = await FeeFlow.InitializeAsync(_f, orderF);
        await FeeFlow.VerifyAsync(_f, refF);
        gw.RefundSucceeds = false;
        try { await FeeFlow.RefundAsync(_f, orderF); } catch { /* the app surfaces the failed refund */ }
        gw.RefundSucceeds = true;

        var p = await _f.WithDbAsync(db => db.Payments.AsNoTracking().ToDictionaryAsync(x => x.Id));
        p[payA].ServiceFeePercent.Should().Be(100m, "historical percentage is never rewritten");
        p[payB].ServiceFeePercent.Should().Be(50m);
        p[payC1].Status.Should().Be(PaymentStatus.Failed);
        p[payD].Status.Should().Be(PaymentStatus.Initialized);
        p[payE].Status.Should().Be(PaymentStatus.Refunded);
        p[payF].Status.Should().Be(PaymentStatus.Succeeded, "a failed refund leaves the payment succeeded");

        decimal Fee(Guid id) => p[id].CustomerBorneCharge;
        var expectedGross = Fee(payA) + Fee(payB) + Fee(payC2) + Fee(payE) + Fee(payF);
        var expectedReversed = Fee(payE);
        var expectedNet = expectedGross - expectedReversed;
        Fee(payA).Should().BeGreaterThan(0); Fee(payB).Should().BeGreaterThan(0);

        using var scope = _f.Services.CreateScope();
        var fees = scope.ServiceProvider.GetRequiredService<ServiceFeeReportService>();
        fees.Invalidate();
        var overview = await fees.OverviewAsync(null, null);
        var all = overview.Periods.Single(x => x.Period.Key == "all").Current;
        all.Transactions.Should().Be(5);
        all.GrossFees.Should().Be(expectedGross);
        all.ReversedFees.Should().Be(expectedReversed);
        all.NetFees.Should().Be(expectedNet);
        all.RefundCount.Should().Be(1);
        all.NotCollectedAttempts.Should().Be(2, "the failed first attempt of C and abandoned D");
        overview.Periods.Single(x => x.Period.Key == "today").Current.NetFees.Should().Be(expectedNet);
        all.Percentages.Select(x => x.Percent).Should().BeEquivalentTo(new[] { 100m, 50m });
        overview.CurrentPercent.Should().Be(50m);

        var ledger = await fees.LedgerAsync(new FeeLedgerFilter(null, null, null, null));
        ledger.Total.Should().Be(7, "every Paystack attempt is in the ledger (A, B, C1, C2, D, E, F)");
        ledger.Reconciliation!.Matches.Should().BeTrue();
        ledger.Items.Sum(r => r.NetFee).Should().Be(expectedNet);
        ledger.Items.Should().Contain(r => r.OrderKind == "Custom").And.Contain(r => r.OrderKind == "Direct");
        (await fees.LedgerAsync(new FeeLedgerFilter(null, null, "refunded", null))).Total.Should().Be(1);
        (await fees.LedgerAsync(new FeeLedgerFilter(null, null, "notcollected", null))).Total.Should().Be(2);
        (await fees.LedgerAsync(new FeeLedgerFilter(null, null, "collected", null))).Total.Should().Be(4, "A, B, C2 and F (E is reversed)");
        (await fees.LedgerAsync(new FeeLedgerFilter(null, null, null, refB))).Items.Single().Percent.Should().Be(50m);

        // Through the portal UI.
        var c = await _f.DeveloperAsync();
        var ov = WebUtility.HtmlDecode(await (await c.GetAsync("/dev/service-fees")).Content.ReadAsStringAsync());
        ov.Should().Contain(DevUi.Naira(expectedNet)).And.Contain("refunds in this app are always <em>full</em>");
        var led = WebUtility.HtmlDecode(await (await c.GetAsync("/dev/service-fees/ledger")).Content.ReadAsStringAsync());
        led.Should().Contain("Reconciliation ✓ matches").And.Contain(refA).And.Contain(refE);
        var detail = await (await c.GetAsync($"/dev/service-fees/{payC2}")).Content.ReadAsStringAsync();
        detail.Should().Contain(refC1, "the detail lists every attempt for the order").And.Contain(refC2);
        (await c.GetAsync($"/dev/service-fees/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        var bad = await (await c.GetAsync("/dev/service-fees?from=2026-02-30&to=nope")).Content.ReadAsStringAsync();
        bad.Should().Contain("dv-flash-bad", "invalid custom dates show a validation message");
        var rev = await (await c.GetAsync("/dev/service-fees?from=2026-09-30&to=2026-09-01")).Content.ReadAsStringAsync();
        rev.Should().Contain("dv-flash-bad", "from after to is rejected");

        // CSV export: reconciles, no customer PII, audited.
        var csvResp = await c.GetAsync("/dev/service-fees/export");
        csvResp.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var csv = await csvResp.Content.ReadAsStringAsync();
        var lines = csv.TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("PaymentId,OrderNumber,OrderKind,PaymentReference");
        lines.Length.Should().Be(8, "header + one row per Paystack attempt");
        csv.Should().NotContain("@yopmail.com").And.NotContain("Tester");
        var header = lines[0].Trim().Split(',');
        int Col(string n) => Array.IndexOf(header, n);
        var rows = lines.Skip(1).Select(l => l.Trim().Split(',')).ToList();
        rows.Sum(r => decimal.Parse(r[Col("NetFee")], System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(expectedNet, "summing the exported NetFee column reproduces the portal's net total");
        rows.Single(r => r[Col("PaymentReference")] == refA)[Col("ServiceFeePercent")].Should().Be("100.00");
        rows.Single(r => r[Col("PaymentReference")] == refE)[Col("FeeStatus")].Should().Contain("Reversed");
        await _f.FlushDiagnosticsAsync();
        (await _f.WithDbAsync(db => db.TechnicalEvents.CountAsync(e => e.EventType == "ServiceFeeExport"))).Should().BeGreaterThan(0);
    }
}
