using System.Globalization;
using System.Text;
using DeChimex.Application.Common;
using DeChimex.Domain.Enums;
using DeChimex.Infrastructure.Diagnostics;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.DevPortal;

/// <summary>
/// SERVICE-FEE ACCOUNTING (Developer Portal). The Payments table IS the ledger: every Paystack
/// attempt already snapshots the fee facts at initialization (OrderAmount, ServiceFeePercent,
/// CustomerBorneCharge = the service fee, PaystackFeeExpected) and PaystackFeeActual at
/// verification. Nothing here recomputes a fee from the CURRENT configuration, so changing
/// the configured percentage never rewrites history.
///
/// Rules (derived from the application's actual payment/refund code, see DEV_PORTAL_REPORT.md):
///  * Only Method = Paystack carries a service fee (bank transfer / cash have none).
///  * COLLECTED (gross) = fee of payments that reached Succeeded (current status Succeeded or
///    Refunded), dated by PaidAtUtc.
///  * REVERSED = fee of payments whose status is Refunded, dated by RefundedAtUtc. Refunds in
///    this application are always FULL (the refund coordinator refunds the whole transaction,
///    fee included); partial refunds do not exist, so there is no partial reversal.
///  * A refund that failed at Paystack leaves the payment Succeeded, so its fee still counts.
///  * NET = collected − reversed for the same period.
///  * Failed / Abandoned / Initialized / Pending attempts never collected a fee; they are listed
///    as "not collected" (retries create a new attempt; only the succeeded attempt counts).
///  * Paystack's own fee (actual when reported, else expected) is shown for context only.
/// </summary>
public sealed class ServiceFeeReportService(DeChimexDbContext db, IMemoryCache cache, IOptions<DevPortalOptions> options)
{
    public const int PageSize = 50;

    public TimeZoneInfo Zone { get; } = ResolveZone(options.Value.ReportingTimeZone);

    public static TimeZoneInfo ResolveZone(string? id)
    {
        foreach (var candidate in new[] { id, "Africa/Lagos", "W. Central Africa Standard Time" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); } catch { /* try next */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("UTC+01", TimeSpan.FromHours(1), "UTC+01:00", "UTC+01:00");
    }

    // ---------------- Period helpers ----------------

    public DateTime LocalNow(DateTime utcNow) => TimeZoneInfo.ConvertTimeFromUtc(utcNow, Zone);
    public DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    public IReadOnlyList<FeePeriod> StandardPeriods(DateTime utcNow)
    {
        var local = LocalNow(utcNow);
        var today = local.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);
        return new[]
        {
            new FeePeriod("today", "Today", ToUtc(today), utcNow, "Yesterday", ToUtc(today.AddDays(-1)), ToUtc(today)),
            new FeePeriod("week", "This week", ToUtc(weekStart), utcNow, "Last week", ToUtc(weekStart.AddDays(-7)), ToUtc(weekStart)),
            new FeePeriod("month", "This month", ToUtc(monthStart), utcNow, "Last month", ToUtc(monthStart.AddMonths(-1)), ToUtc(monthStart)),
            new FeePeriod("year", "This year", ToUtc(yearStart), utcNow, "Last year", ToUtc(yearStart.AddYears(-1)), ToUtc(yearStart)),
            new FeePeriod("all", "All time", DateTime.MinValue, utcNow, null, null, null)
        };
    }

    // ---------------- Source rows (cached briefly) ----------------

    /// <summary>Compact projection of every Paystack attempt (no customer data). Cached 60s;
    /// maintenance "clear cache" and new payments show after at most a minute.</summary>
    private async Task<IReadOnlyList<FeeRow>> RowsAsync(CancellationToken ct, bool fresh = false)
    {
        if (!fresh && cache.TryGetValue("devportal:fee-rows", out IReadOnlyList<FeeRow>? rows) && rows is not null)
            return rows;
        rows = await db.Payments.AsNoTracking()
            .Where(p => p.Method == PaymentMethod.Paystack)
            .Select(p => new FeeRow(p.Id, p.OrderId, p.Reference, p.Status, p.CreatedDateUtc, p.PaidAtUtc, p.RefundedAtUtc,
                p.OrderAmount, p.Amount.Amount, p.ServiceFeePercent, p.CustomerBorneCharge, p.PaystackFeeExpected, p.PaystackFeeActual))
            .ToListAsync(ct);
        cache.Set("devportal:fee-rows", rows, TimeSpan.FromSeconds(60));
        return rows;
    }

    public void Invalidate() => cache.Remove("devportal:fee-rows");

    // ---------------- Aggregation ----------------

    public static bool WasCollected(PaymentStatus s) => s is PaymentStatus.Succeeded or PaymentStatus.Refunded;

    public static FeeTotals Compute(IEnumerable<FeeRow> all, DateTime fromUtc, DateTime toUtc)
    {
        var list = all as IReadOnlyCollection<FeeRow> ?? all.ToList();
        var collected = list.Where(r => WasCollected(r.Status) && r.PaidAtUtc is { } paid && paid >= fromUtc && paid < toUtc).ToList();
        var reversed = list.Where(r => r.Status == PaymentStatus.Refunded && r.RefundedAtUtc is { } at && at >= fromUtc && at < toUtc).ToList();
        var notCollected = list.Where(r => !WasCollected(r.Status) && r.CreatedUtc >= fromUtc && r.CreatedUtc < toUtc).ToList();

        var gross = collected.Sum(r => r.Fee);
        var reversal = reversed.Sum(r => r.Fee);
        return new FeeTotals(
            Transactions: collected.Count,
            TransactionValue: collected.Sum(r => r.Amount),
            OrderValue: collected.Sum(r => r.OrderAmount),
            GrossFees: gross,
            AverageFee: collected.Count == 0 ? 0 : Math.Round(gross / collected.Count, 2),
            RefundCount: reversed.Count,
            ReversedFees: reversal,
            NetFees: gross - reversal,
            PaystackFees: collected.Sum(r => r.PaystackFeeActual ?? r.PaystackFeeExpected),
            NotCollectedAttempts: notCollected.Count,
            NotCollectedFees: notCollected.Sum(r => r.Fee),
            Percentages: collected.GroupBy(r => r.Percent).OrderByDescending(g => g.Key)
                .Select(g => new PercentUsage(g.Key, g.Count(), g.Sum(r => r.Fee), g.Sum(r => r.Amount))).ToList());
    }

    public async Task<ServiceFeeOverview> OverviewAsync(DateTime? customFromLocal, DateTime? customToLocal, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await RowsAsync(ct);
        var tiles = StandardPeriods(now).Select(p => new FeePeriodTotals(p, Compute(rows, p.FromUtc, p.ToUtc),
            p.PreviousFromUtc is { } pf && p.PreviousToUtc is { } pt ? Compute(rows, pf, pt) : null)).ToList();

        // 12-month breakdown (local calendar months, newest first).
        var local = LocalNow(now);
        var months = new List<FeeMonth>();
        for (var i = 0; i < 12; i++)
        {
            var start = new DateTime(local.Year, local.Month, 1).AddMonths(-i);
            var fromUtc = ToUtc(start);
            var toUtc = i == 0 ? now : ToUtc(start.AddMonths(1));
            months.Add(new FeeMonth(start.ToString("MMM yyyy", CultureInfo.InvariantCulture), Compute(rows, fromUtc, toUtc)));
        }

        FeeTotals? custom = null;
        DateTime? cFrom = null, cTo = null;
        if (customFromLocal is { } cf && customToLocal is { } ctl && cf.Date <= ctl.Date)
        {
            cFrom = ToUtc(cf.Date);
            cTo = ToUtc(ctl.Date.AddDays(1));
            custom = Compute(rows, cFrom.Value, cTo.Value);
        }

        var currentPercent = ServiceFeeSettings.Parse(await db.ApplicationSettings.AsNoTracking()
            .Where(s => s.Key == ServiceFeeSettings.Key).Select(s => s.Value).FirstOrDefaultAsync(ct));

        return new ServiceFeeOverview(tiles, months, custom, cFrom, cTo, currentPercent, Zone.Id, rows.Count);
    }

    // ---------------- Ledger ----------------

    public async Task<FeeLedgerPage> LedgerAsync(FeeLedgerFilter f, CancellationToken ct = default)
    {
        var (items, total) = await LedgerQueryAsync(f, paged: true, ct);
        FeeReconciliation? rec = null;
        if (string.IsNullOrWhiteSpace(f.Search) && (string.IsNullOrEmpty(f.Status) || f.Status == "all"))
        {
            // Independent recomputation: sum the ledger rows themselves and compare with the
            // headline aggregation for the same range. They must match to the kobo.
            var (all, _) = await LedgerQueryAsync(f with { Page = 1 }, paged: false, ct);
            var (fromUtc, toUtc) = Range(f);
            var fromLedger = Reconcile(all, fromUtc, toUtc);
            var headline = Compute(await RowsAsync(ct, fresh: true), fromUtc, toUtc);
            rec = new FeeReconciliation(fromLedger.Gross, fromLedger.Reversed, fromLedger.Net,
                headline.GrossFees, headline.ReversedFees, headline.NetFees);
        }
        return new FeeLedgerPage(f, items, total, Math.Max(1, f.Page), PageSize, rec);
    }

    public (DateTime FromUtc, DateTime ToUtc) Range(FeeLedgerFilter f)
        => (f.FromLocal is { } fl ? ToUtc(fl.Date) : DateTime.MinValue,
            f.ToLocal is { } tl ? ToUtc(tl.Date.AddDays(1)) : DateTime.MaxValue);

    /// <summary>Gross / reversed / net from ledger rows using each row's own paid and refund dates.</summary>
    public static (decimal Gross, decimal Reversed, decimal Net) Reconcile(IEnumerable<FeeLedgerRow> rows, DateTime fromUtc, DateTime toUtc)
    {
        var list = rows.ToList();
        var gross = list.Where(r => r.Status is nameof(PaymentStatus.Succeeded) or nameof(PaymentStatus.Refunded)
                                    && r.PaidUtc is { } p && p >= fromUtc && p < toUtc).Sum(r => r.ServiceFee);
        var reversed = list.Where(r => r.Status == nameof(PaymentStatus.Refunded)
                                       && r.RefundedUtc is { } at && at >= fromUtc && at < toUtc).Sum(r => r.ServiceFee);
        return (gross, reversed, gross - reversed);
    }

    private async Task<(List<FeeLedgerRow> Items, int Total)> LedgerQueryAsync(FeeLedgerFilter f, bool paged, CancellationToken ct)
    {
        DateTime? fromUtc = f.FromLocal is { } fl ? ToUtc(fl.Date) : null;
        DateTime? toUtc = f.ToLocal is { } tl ? ToUtc(tl.Date.AddDays(1)) : null;

        var q = from p in db.Payments.AsNoTracking()
                where p.Method == PaymentMethod.Paystack
                join o in db.Orders.IgnoreQueryFilters().AsNoTracking() on p.OrderId equals o.Id into oj
                from o in oj.DefaultIfEmpty()
                select new { p, OrderNumber = o != null ? o.OrderNumber : null, Kind = o != null ? (OrderKind?)o.Kind : null };

        // The ledger's date is the fee's economic date: paid date for collected rows, attempt date otherwise.
        // A row is in range when its economic date OR its refund (reversal) date falls in the range.
        if (fromUtc is not null || toUtc is not null)
        {
            var a = fromUtc ?? new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var b = toUtc ?? new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            q = q.Where(x => ((x.p.PaidAtUtc ?? x.p.CreatedDateUtc) >= a && (x.p.PaidAtUtc ?? x.p.CreatedDateUtc) < b)
                             || (x.p.RefundedAtUtc != null && x.p.RefundedAtUtc >= a && x.p.RefundedAtUtc < b));
        }

        switch (f.Status)
        {
            case "collected": q = q.Where(x => x.p.Status == PaymentStatus.Succeeded); break;
            case "refunded": q = q.Where(x => x.p.Status == PaymentStatus.Refunded); break;
            case "notcollected": q = q.Where(x => x.p.Status != PaymentStatus.Succeeded && x.p.Status != PaymentStatus.Refunded); break;
        }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(x => x.p.Reference.Contains(s) || (x.OrderNumber != null && x.OrderNumber.Contains(s)));
        }

        var total = await q.CountAsync(ct);
        var ordered = q.OrderByDescending(x => x.p.PaidAtUtc ?? x.p.CreatedDateUtc);
        var page = Math.Max(1, f.Page);
        var rows = paged
            ? await ordered.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct)
            : await ordered.Take(50_000).ToListAsync(ct);

        return (rows.Select(x => ToLedgerRow(x.p.Id, x.p.OrderId, x.OrderNumber, x.Kind, x.p.Reference, x.p.Status,
            x.p.CreatedDateUtc, x.p.PaidAtUtc, x.p.RefundedAtUtc, x.p.RefundReference, x.p.OrderAmount, x.p.Amount.Amount,
            x.p.ServiceFeePercent, x.p.CustomerBorneCharge, x.p.PaystackFeeExpected, x.p.PaystackFeeActual)).ToList(), total);
    }

    private static FeeLedgerRow ToLedgerRow(Guid id, Guid orderId, string? orderNumber, OrderKind? kind, string reference,
        PaymentStatus status, DateTime created, DateTime? paid, DateTime? refunded, string? refundRef, decimal orderAmount,
        decimal amount, decimal percent, decimal fee, decimal psExpected, decimal? psActual)
    {
        var feeStatus = status switch
        {
            PaymentStatus.Succeeded => "Collected",
            PaymentStatus.Refunded => "Reversed (refunded)",
            _ => "Not collected"
        };
        var net = status == PaymentStatus.Succeeded ? fee : 0m;
        return new FeeLedgerRow(id, orderId, orderNumber ?? "(deleted order)", kind?.ToString() ?? "-", reference, status.ToString(),
            feeStatus, created, paid, refunded, refundRef, orderAmount, amount, percent, fee, psExpected, psActual, net);
    }

    public async Task<FeeDetail?> DetailAsync(Guid paymentId, CancellationToken ct = default)
    {
        var x = await (from p in db.Payments.AsNoTracking()
                       where p.Id == paymentId && p.Method == PaymentMethod.Paystack
                       join o in db.Orders.IgnoreQueryFilters().AsNoTracking() on p.OrderId equals o.Id into oj
                       from o in oj.DefaultIfEmpty()
                       select new { p, OrderNumber = o != null ? o.OrderNumber : null, Kind = o != null ? (OrderKind?)o.Kind : null,
                           OrderStatus = o != null ? o.Status.ToString() : null })
            .FirstOrDefaultAsync(ct);
        if (x is null) return null;

        var row = ToLedgerRow(x.p.Id, x.p.OrderId, x.OrderNumber, x.Kind, x.p.Reference, x.p.Status, x.p.CreatedDateUtc,
            x.p.PaidAtUtc, x.p.RefundedAtUtc, x.p.RefundReference, x.p.OrderAmount, x.p.Amount.Amount, x.p.ServiceFeePercent,
            x.p.CustomerBorneCharge, x.p.PaystackFeeExpected, x.p.PaystackFeeActual);

        var attempts = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == x.p.OrderId)
            .OrderBy(p => p.CreatedDateUtc)
            .Select(p => new FeeAttempt(p.Id, p.Reference, p.Method.ToString(), p.Status.ToString(), p.CreatedDateUtc, p.PaidAtUtc,
                p.CustomerBorneCharge, p.ServiceFeePercent, p.FailureReason))
            .ToListAsync(ct);

        var calls = await db.IntegrationCalls.AsNoTracking()
            .Where(c => c.Reference == x.p.Reference)
            .OrderByDescending(c => c.TimestampUtc).Take(50).ToListAsync(ct);

        return new FeeDetail(row, x.OrderStatus, x.p.FailureReason, x.p.Amount.Currency, attempts, calls);
    }

    /// <summary>PII-free reconciliation CSV of the ledger rows matching the filter (no customer
    /// names, emails, phones or addresses; only order number and payment reference).</summary>
    public async Task<(string Csv, int Rows, decimal Collected, decimal Reversed, decimal Net)> ExportCsvAsync(FeeLedgerFilter f, CancellationToken ct = default)
    {
        var (items, _) = await LedgerQueryAsync(f with { Page = 1 }, paged: false, ct);
        var sb = new StringBuilder();
        sb.AppendLine("PaymentId,OrderNumber,OrderKind,PaymentReference,PaymentStatus,FeeStatus,AttemptUtc,PaidUtc,RefundedUtc,RefundReference,OrderAmount,TransactionAmount,ServiceFeePercent,ServiceFee,PaystackFeeExpected,PaystackFeeActual,NetFee");
        foreach (var r in items)
        {
            sb.AppendJoin(',', new[]
            {
                r.PaymentId.ToString(), Csv(r.OrderNumber), r.OrderKind, Csv(r.Reference), r.Status, Csv(r.FeeStatus),
                Iso(r.AttemptUtc), Iso(r.PaidUtc), Iso(r.RefundedUtc), Csv(r.RefundReference ?? string.Empty),
                Money(r.OrderAmount), Money(r.TransactionAmount), Money(r.Percent), Money(r.ServiceFee),
                Money(r.PaystackFeeExpected), r.PaystackFeeActual is { } a ? Money(a) : string.Empty, Money(r.NetFee)
            });
            sb.AppendLine();
        }
        var (fromUtc, toUtc) = Range(f);
        var (collected, reversed, net) = Reconcile(items, fromUtc, toUtc);
        return (sb.ToString(), items.Count, collected, reversed, net);
    }

    private static string Money(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Iso(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>RFC 4180 quoting + spreadsheet formula-injection guard.</summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

public sealed record FeeRow(Guid Id, Guid OrderId, string Reference, PaymentStatus Status, DateTime CreatedUtc, DateTime? PaidAtUtc,
    DateTime? RefundedAtUtc, decimal OrderAmount, decimal Amount, decimal Percent, decimal Fee, decimal PaystackFeeExpected,
    decimal? PaystackFeeActual);

public sealed record FeePeriod(string Key, string Label, DateTime FromUtc, DateTime ToUtc, string? PreviousLabel,
    DateTime? PreviousFromUtc, DateTime? PreviousToUtc);

public sealed record PercentUsage(decimal Percent, int Transactions, decimal Fees, decimal TransactionValue);

public sealed record FeeTotals(int Transactions, decimal TransactionValue, decimal OrderValue, decimal GrossFees, decimal AverageFee,
    int RefundCount, decimal ReversedFees, decimal NetFees, decimal PaystackFees, int NotCollectedAttempts, decimal NotCollectedFees,
    IReadOnlyList<PercentUsage> Percentages);

public sealed record FeePeriodTotals(FeePeriod Period, FeeTotals Current, FeeTotals? Previous);
public sealed record FeeMonth(string Label, FeeTotals Totals);

public sealed record ServiceFeeOverview(IReadOnlyList<FeePeriodTotals> Periods, IReadOnlyList<FeeMonth> Months, FeeTotals? Custom,
    DateTime? CustomFromUtc, DateTime? CustomToUtc, decimal CurrentPercent, string TimeZoneId, int AttemptCount);

public sealed record FeeLedgerFilter(DateTime? FromLocal, DateTime? ToLocal, string? Status, string? Search, int Page = 1);

public sealed record FeeLedgerRow(Guid PaymentId, Guid OrderId, string OrderNumber, string OrderKind, string Reference, string Status,
    string FeeStatus, DateTime AttemptUtc, DateTime? PaidUtc, DateTime? RefundedUtc, string? RefundReference, decimal OrderAmount,
    decimal TransactionAmount, decimal Percent, decimal ServiceFee, decimal PaystackFeeExpected, decimal? PaystackFeeActual, decimal NetFee);

public sealed record FeeReconciliation(decimal LedgerGross, decimal LedgerReversed, decimal LedgerNet,
    decimal HeadlineGross, decimal HeadlineReversed, decimal HeadlineNet)
{
    public bool Matches => LedgerGross == HeadlineGross && LedgerReversed == HeadlineReversed && LedgerNet == HeadlineNet;
}

public sealed record FeeLedgerPage(FeeLedgerFilter Filter, IReadOnlyList<FeeLedgerRow> Items, int Total, int Page, int PageSize,
    FeeReconciliation? Reconciliation = null)
{
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record FeeAttempt(Guid Id, string Reference, string Method, string Status, DateTime CreatedUtc, DateTime? PaidUtc,
    decimal Fee, decimal Percent, string? FailureReason);

public sealed record FeeDetail(FeeLedgerRow Row, string? OrderStatus, string? FailureReason, string Currency,
    IReadOnlyList<FeeAttempt> Attempts, IReadOnlyList<Domain.Entities.Diagnostics.IntegrationCall> Calls);
