namespace DeChimex.Host.Models.Admin;

/// <summary>Row models for the admin analytics page (all derived from live data).</summary>

public sealed record AnalyticsSummaryRow(
    decimal Revenue, int Orders, decimal Aov, decimal Conv,
    string RevenueTrend, string OrdersTrend, string AovTrend, string ConvTrend);

public sealed record TopProductRow(string Name, int UnitsSold, decimal Revenue);

public sealed record OrderStatusMixRow(string Status, int Count, decimal Percentage, string Tone);
