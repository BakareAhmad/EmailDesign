namespace DeChimex.Infrastructure.Diagnostics;

/// <summary>Developer Portal / diagnostics settings (section "DevPortal"). All optional.</summary>
public sealed class DevPortalOptions
{
    public const string SectionName = "DevPortal";

    /// <summary>Days technical events and integration calls are kept.</summary>
    public int RetentionDays { get; set; } = 30;
    /// <summary>Days request-metric buckets are kept.</summary>
    public int MetricsRetentionDays { get; set; } = 30;
    /// <summary>A request slower than this counts as "slow".</summary>
    public int SlowRequestMs { get; set; } = 1000;
    /// <summary>A database command slower than this is recorded (relational provider only).</summary>
    public int SlowDbCommandMs { get; set; } = 500;
    /// <summary>In-memory queue bound; when full the OLDEST pending entries are dropped (counted).</summary>
    public int QueueCapacity { get; set; } = 5000;
    /// <summary>IANA/Windows time zone used for "today / this week / this month" service-fee periods.</summary>
    public string ReportingTimeZone { get; set; } = "Africa/Lagos";
    /// <summary>Push Error/Critical technical events to connected developers over SignalR.</summary>
    public bool RealtimeAlerts { get; set; } = true;
}
