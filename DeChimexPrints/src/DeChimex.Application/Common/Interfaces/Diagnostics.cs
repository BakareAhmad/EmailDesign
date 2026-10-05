namespace DeChimex.Application.Common.Interfaces;

/// <summary>Severity vocabulary of the technical audit log. Ordinary events are Information;
/// only real problems are Warning/Error/Critical.</summary>
public static class TechnicalSeverity
{
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Critical = "Critical";

    public static readonly IReadOnlyList<string> All = new[] { Information, Warning, Error, Critical };

    public static int Rank(string? severity) => severity switch
    {
        Critical => 3, Error => 2, Warning => 1, _ => 0
    };
}

/// <summary>Subsystem categories of technical events.</summary>
public static class TechnicalCategory
{
    public const string Application = "Application";
    public const string Security = "Security";
    public const string Database = "Database";
    public const string Payments = "Payments";
    public const string Email = "Email";
    public const string Realtime = "Realtime";
    public const string Storage = "Storage";
    public const string BackgroundJob = "BackgroundJob";
    public const string Configuration = "Configuration";
    public const string Performance = "Performance";
    public const string Maintenance = "Maintenance";
    public const string System = "System";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Application, Security, Database, Payments, Email, Realtime, Storage,
        BackgroundJob, Configuration, Performance, Maintenance, System
    };
}

/// <summary>Well-known technical event types (free-form strings are allowed; these are the ones the app raises).</summary>
public static class TechnicalEventType
{
    public const string Log = "Log";
    public const string UnhandledException = "UnhandledException";
    public const string AuthenticationFailure = "AuthenticationFailure";
    public const string AuthorizationFailure = "AuthorizationFailure";
    public const string RateLimited = "RateLimited";
    public const string IntegrationFailure = "IntegrationFailure";
    public const string WebhookRejected = "WebhookRejected";
    public const string WebhookMismatch = "WebhookMismatch";
    public const string EmailFailure = "EmailFailure";
    public const string JobFailure = "JobFailure";
    public const string RealtimeFailure = "RealtimeFailure";
    public const string SlowDatabaseCommand = "SlowDatabaseCommand";
    public const string DatabaseMigration = "DatabaseMigration";
    public const string SystemStart = "SystemStart";
    public const string SystemStop = "SystemStop";
    public const string MaintenanceAction = "MaintenanceAction";
    public const string HealthChanged = "HealthChanged";
}

/// <summary>An explicit technical event raised by application code (not via logging).</summary>
public sealed record TechnicalEventEntry(
    string Severity,
    string Category,
    string EventType,
    string Message,
    string? ExceptionType = null,
    string? StackTrace = null,
    Guid? UserId = null,
    string? Route = null,
    Guid? OrderId = null,
    string? Integration = null,
    string? Source = null,
    IReadOnlyDictionary<string, string?>? Properties = null,
    string? CorrelationId = null,
    string? RequestId = null,
    string? MessageTemplate = null);

/// <summary>Records technical events. Non-blocking: entries are queued and persisted in the background.</summary>
public interface ITechnicalEventRecorder
{
    void Record(TechnicalEventEntry entry);
}

/// <summary>Names used for integration call records.</summary>
public static class IntegrationNames
{
    public const string Paystack = "Paystack";
    public const string Brevo = "Brevo";
}

/// <summary>One external-integration call outcome (see IntegrationCall entity).</summary>
public sealed record IntegrationCallEntry(
    string Integration,
    string Operation,
    string Outcome,
    bool Succeeded,
    long DurationMs,
    int? HttpStatus = null,
    string? Reference = null,
    Guid? OrderId = null,
    string? Template = null,
    string? RecipientCategory = null,
    string? RecipientEmail = null,
    string? Detail = null);

/// <summary>Records integration calls. Non-blocking; recipients are masked and details redacted by the implementation.</summary>
public interface IIntegrationCallRecorder
{
    void Record(IntegrationCallEntry entry);
}

/// <summary>
/// Lightweight in-process monitor for hosted services / recurring work. State is transient
/// (since process start); failures are ALSO written as technical events so they survive restarts.
/// </summary>
public interface IJobMonitor
{
    /// <summary>Declares a job so it is listed even before its first run.</summary>
    void Register(string key, string name, string description, TimeSpan? expectedInterval = null, bool recurring = true);

    /// <summary>Starts a run; dispose after calling <see cref="IJobRun.Succeeded"/> or <see cref="IJobRun.Failed"/>.
    /// A run disposed without either is recorded as succeeded.</summary>
    IJobRun Begin(string key);

    IReadOnlyList<JobStatusSnapshot> Snapshot();
}

public interface IJobRun : IDisposable
{
    void Succeeded(string? note = null);
    /// <param name="recordEvent">False when the caller already logs the failure (the log capture
    /// records it), to avoid a duplicate technical event.</param>
    void Failed(Exception? exception, string? message = null, bool recordEvent = true);
}

public sealed record JobStatusSnapshot(
    string Key,
    string Name,
    string Description,
    bool Recurring,
    TimeSpan? ExpectedInterval,
    bool Running,
    DateTime? LastStartedUtc,
    DateTime? LastCompletedUtc,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    string? LastFailureMessage,
    long SuccessCount,
    long FailureCount,
    int ConsecutiveFailures,
    double? LastDurationMs,
    double? MaxDurationMs,
    string? LastNote,
    DateTime? NextExpectedUtc,
    string State);
