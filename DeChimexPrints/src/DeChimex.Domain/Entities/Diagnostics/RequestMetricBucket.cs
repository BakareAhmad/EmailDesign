using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities.Diagnostics;

/// <summary>
/// Aggregated HTTP request metrics for one endpoint in one 5-minute window. Aggregation
/// (instead of one row per request) keeps the monitoring cheap: rows scale with active
/// endpoints x windows, not with traffic. Unique on (BucketStartUtc, Method, Route).
/// </summary>
public sealed class RequestMetricBucket : Entity
{
    private RequestMetricBucket() { } // EF

    public DateTime BucketStartUtc { get; private set; }
    public string Method { get; private set; } = default!;
    /// <summary>Route template (e.g. "admin/orders/{id}"), never a raw URL with ids/query.</summary>
    public string Route { get; private set; } = default!;
    public int RequestCount { get; private set; }
    /// <summary>Responses with status &gt;= 500.</summary>
    public int ServerErrorCount { get; private set; }
    /// <summary>Responses with status 400-499.</summary>
    public int ClientErrorCount { get; private set; }
    public int SlowCount { get; private set; }
    public long TotalDurationMs { get; private set; }
    public long MaxDurationMs { get; private set; }

    public static RequestMetricBucket Create(DateTime bucketStartUtc, string method, string route)
        => new() { BucketStartUtc = bucketStartUtc, Method = method, Route = route };

    public void Add(int requests, int serverErrors, int clientErrors, int slow, long totalMs, long maxMs)
    {
        RequestCount += requests;
        ServerErrorCount += serverErrors;
        ClientErrorCount += clientErrors;
        SlowCount += slow;
        TotalDurationMs += totalMs;
        if (maxMs > MaxDurationMs) MaxDurationMs = maxMs;
    }
}
