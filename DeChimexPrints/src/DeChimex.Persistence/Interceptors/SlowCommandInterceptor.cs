using System.Data.Common;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Shared.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DeChimex.Persistence.Interceptors;

/// <summary>
/// Records database commands slower than a threshold as technical "SlowDatabaseCommand"
/// warnings (relational provider only). Captures the elapsed time and a truncated, redacted
/// command text; parameter VALUES are never captured (EF logs them only when sensitive-data
/// logging is on, which this interceptor does not read).
/// </summary>
public sealed class SlowCommandInterceptor(ITechnicalEventRecorder recorder, int thresholdMs) : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { Check(command, eventData); return result; }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    { Check(command, eventData); return ValueTask.FromResult(result); }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { Check(command, eventData); return result; }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    { Check(command, eventData); return ValueTask.FromResult(result); }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    { Check(command, eventData); return result; }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    { Check(command, eventData); return ValueTask.FromResult(result); }

    private void Check(DbCommand command, CommandExecutedEventData eventData)
    {
        if (TechnicalContext.CaptureSuppressed) return;
        var ms = (long)eventData.Duration.TotalMilliseconds;
        if (ms < thresholdMs) return;
        var sql = SensitiveDataRedactor.Redact(command.CommandText, 300);
        recorder.Record(new TechnicalEventEntry(
            TechnicalSeverity.Warning, TechnicalCategory.Database, TechnicalEventType.SlowDatabaseCommand,
            $"Database command took {ms} ms (threshold {thresholdMs} ms).",
            Source: "EF Core",
            Properties: new Dictionary<string, string?> { ["durationMs"] = ms.ToString(), ["command"] = sql }));
    }
}
