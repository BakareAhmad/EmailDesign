using DeChimex.Application.Common.Interfaces;
using DeChimex.Shared.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace DeChimex.Host.DevPortal;

/// <summary>
/// Serilog sink that turns relevant log events into persisted technical events (Developer
/// Portal technical audit log / error center). Reuses the existing structured logging instead
/// of adding a second logging mechanism.
/// Captured: every Warning/Error/Fatal event, except handled 4xx business exceptions (normal
/// validation / not-found outcomes, which are not technical problems) and per-request access
/// logs. Forbidden/Unauthorized outcomes are kept and categorised as Security.
/// The recorder is resolved lazily (the logger is built before most singletons exist) and a
/// re-entrancy guard plus TechnicalContext.CaptureSuppressed prevent feedback loops.
/// </summary>
public sealed class TechnicalEventSink(IServiceProvider services) : ILogEventSink
{
    [ThreadStatic] private static bool _inEmit;
    private ITechnicalEventRecorder? _recorder;

    private static readonly HashSet<string> HandledSecurityExceptions = new(StringComparer.Ordinal)
    {
        "ForbiddenException", "UnauthorizedAppException"
    };

    public void Emit(LogEvent logEvent)
    {
        if (_inEmit || TechnicalContext.CaptureSuppressed) return;
        if (logEvent.Level < LogEventLevel.Warning) return;

        _inEmit = true;
        try
        {
            var source = Scalar(logEvent, "SourceContext");
            var template = logEvent.MessageTemplate.Text;

            // Per-request access log lines are metrics territory, not technical events.
            if (source is "Serilog.AspNetCore.RequestLoggingMiddleware") return;
            // Our own diagnostics plumbing never feeds back into itself.
            if (source?.StartsWith("DeChimex.Infrastructure.Diagnostics", StringComparison.Ordinal) == true) return;

            // The MediatR LoggingBehavior logs EVERY handler exception at Error before the caller
            // handles it. Expected business outcomes (wrong password, cart guard conflict, not found,
            // validation) are not technical faults: login failures, access denials and 4xx responses
            // are already captured as their own events/metrics, so they must not flood the error center.
            if (source == "DeChimex.Application.Common.Behaviors.LoggingBehavior" && IsExpectedBusinessException(logEvent.Exception))
                return;

            var category = Categorise(source, template, logEvent.Exception);
            var eventType = TechnicalEventType.Log;
            var severity = logEvent.Level switch
            {
                LogEventLevel.Fatal => TechnicalSeverity.Critical,
                LogEventLevel.Error => TechnicalSeverity.Error,
                _ => TechnicalSeverity.Warning
            };

            if (source == "DeChimex.Host.Middleware.ExceptionHandlingMiddleware")
            {
                if (logEvent.Level == LogEventLevel.Warning)
                {
                    var exType = Scalar(logEvent, "ExceptionType");
                    if (exType is null || !HandledSecurityExceptions.Contains(exType)) return; // business 4xx
                    category = TechnicalCategory.Security;
                    eventType = TechnicalEventType.AuthorizationFailure;
                }
                else
                {
                    eventType = TechnicalEventType.UnhandledException;
                }
            }
            else if (template.StartsWith("Rate limit exceeded", StringComparison.Ordinal))
            {
                category = TechnicalCategory.Security;
                eventType = TechnicalEventType.RateLimited;
            }
            else if (logEvent.Exception is not null && logEvent.Level >= LogEventLevel.Error)
            {
                eventType = TechnicalEventType.UnhandledException;
            }

            // Critical escalation: unhandled failures in the database layer are critical.
            if (severity == TechnicalSeverity.Error && category == TechnicalCategory.Database && logEvent.Exception is not null)
                severity = TechnicalSeverity.Critical;

            string? orderId = Scalar(logEvent, "OrderId");
            var ex = logEvent.Exception;
            var props = new Dictionary<string, string?>();
            foreach (var (k, v) in logEvent.Properties)
            {
                if (k is "SourceContext" or "CorrelationId" or "RequestId" or "RequestPath" or "ConnectionId"
                    or "EnvironmentName" or "MachineName" or "ActionId" or "EventId") continue;
                if (props.Count >= 12) break;
                props[k] = v is ScalarValue sv ? sv.Value?.ToString() : v.ToString();
            }

            _recorder ??= services.GetService<ITechnicalEventRecorder>();
            _recorder?.Record(new TechnicalEventEntry(
                severity, category, eventType,
                logEvent.RenderMessage(),
                ExceptionType: ex?.GetType().FullName,
                StackTrace: ex?.ToString(),
                OrderId: Guid.TryParse(orderId, out var oid) ? oid : null,
                Integration: category switch
                {
                    TechnicalCategory.Payments => IntegrationNames.Paystack,
                    TechnicalCategory.Email => IntegrationNames.Brevo,
                    _ => null
                },
                Source: source,
                Properties: props,
                CorrelationId: Scalar(logEvent, "CorrelationId"),
                RequestId: Scalar(logEvent, "RequestId"),
                MessageTemplate: template));
        }
        catch
        {
            // A sink must never throw.
        }
        finally
        {
            _inEmit = false;
        }
    }

    /// <summary>Application/domain rule exceptions and validation failures (handled 4xx outcomes), and
    /// cancellations — in the request pipeline an OperationCanceledException means the client aborted
    /// the request (navigated away / closed the tab), which is not a server fault.</summary>
    public static bool IsExpectedBusinessException(Exception? ex)
    {
        if (ex is null) return false;
        if (ex is OperationCanceledException) return true;
        var ns = ex.GetType().Namespace;
        return ns is "DeChimex.Application.Common.Exceptions" or "DeChimex.Domain.Exceptions"
            || ex.GetType().FullName == "FluentValidation.ValidationException";
    }

    private static string? Scalar(LogEvent e, string name)
        => e.Properties.TryGetValue(name, out var v) && v is ScalarValue { Value: { } value } ? value.ToString() : null;

    public static string Categorise(string? source, string template, Exception? ex)
    {
        var s = source ?? string.Empty;
        // Match keywords on the literal template text only: a placeholder such as {Email}
        // names a value, it does not make the event an email-subsystem event.
        template = System.Text.RegularExpressions.Regex.Replace(template ?? string.Empty, @"\{[^}]*\}", string.Empty);
        if (s.Contains("Paystack", StringComparison.OrdinalIgnoreCase) || s.Contains(".Payments", StringComparison.Ordinal)
            || template.Contains("Paystack", StringComparison.OrdinalIgnoreCase) || template.Contains("Webhook", StringComparison.OrdinalIgnoreCase))
            return TechnicalCategory.Payments;
        if (s.Contains("Brevo", StringComparison.OrdinalIgnoreCase) || s.Contains("Email", StringComparison.OrdinalIgnoreCase)
            || template.Contains("email", StringComparison.OrdinalIgnoreCase))
            return TechnicalCategory.Email;
        if (s.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) || s.Contains("Persistence", StringComparison.Ordinal)
            || ex is Microsoft.EntityFrameworkCore.DbUpdateException || ex?.GetType().Name.Contains("Sql", StringComparison.Ordinal) == true)
            return TechnicalCategory.Database;
        if (s.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal) || s.Contains("Realtime", StringComparison.Ordinal))
            return TechnicalCategory.Realtime;
        if (s.Contains("Storage", StringComparison.Ordinal) || s.Contains("Gallery", StringComparison.Ordinal) || ex is IOException)
            return TechnicalCategory.Storage;
        if (s.Contains("BackgroundService", StringComparison.Ordinal) || s.Contains("Processor", StringComparison.Ordinal)
            || s.Contains("Dispatcher", StringComparison.Ordinal))
            return TechnicalCategory.BackgroundJob;
        if (s.StartsWith("Microsoft.AspNetCore.Authentication", StringComparison.Ordinal) || s.StartsWith("Microsoft.AspNetCore.Authorization", StringComparison.Ordinal)
            || s.StartsWith("Microsoft.AspNetCore.Antiforgery", StringComparison.Ordinal) || s.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal))
            return TechnicalCategory.Security;
        if (s.StartsWith("Microsoft.Extensions.Options", StringComparison.Ordinal) || ex is Microsoft.Extensions.Options.OptionsValidationException)
            return TechnicalCategory.Configuration;
        if (s.StartsWith("Microsoft.", StringComparison.Ordinal) || s.StartsWith("System.", StringComparison.Ordinal))
            return TechnicalCategory.System;
        return TechnicalCategory.Application;
    }
}
