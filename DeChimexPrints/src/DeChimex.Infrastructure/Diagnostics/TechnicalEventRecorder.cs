using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities.Diagnostics;
using DeChimex.Shared.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DeChimex.Infrastructure.Diagnostics;

/// <summary>Observers notified (synchronously, must be cheap and non-throwing) of each recorded
/// technical event. Used by the Host to push critical alerts to the Developer Portal.</summary>
public interface ITechnicalEventObserver
{
    void OnRecorded(TechnicalEvent technicalEvent);
}

/// <summary>
/// Turns a <see cref="TechnicalEventEntry"/> into a redacted, fingerprinted
/// <see cref="TechnicalEvent"/>, enriches it with ambient request context (correlation id,
/// user id, route) and queues it for persistence. Never throws.
/// </summary>
public sealed class TechnicalEventRecorder(
    DiagnosticsQueue queue,
    IHttpContextAccessor httpContextAccessor,
    IHostEnvironment environment,
    IEnumerable<ITechnicalEventObserver> observers) : ITechnicalEventRecorder
{
    private readonly ITechnicalEventObserver[] _observers = observers.ToArray();

    public void Record(TechnicalEventEntry entry)
    {
        try
        {
            var (ctxUserId, ctxRoute, ctxRequestId) = RequestSnapshot();
            Guid? userId = entry.UserId ?? ctxUserId;

            var severity = TechnicalSeverity.All.Contains(entry.Severity) ? entry.Severity : TechnicalSeverity.Information;
            var template = Trim(entry.MessageTemplate, 1000);
            var fingerprint = Fingerprint(entry.Category, entry.EventType, entry.Source, entry.ExceptionType,
                template ?? entry.Message);

            string? props = null;
            if (entry.Properties is { Count: > 0 })
            {
                var redacted = entry.Properties
                    .Take(25)
                    .ToDictionary(kv => kv.Key, kv => SensitiveDataRedactor.IsSensitiveKey(kv.Key)
                        ? (kv.Value is null ? null : "[REDACTED]")
                        : SensitiveDataRedactor.Redact(kv.Value, 500));
                props = Trim(JsonSerializer.Serialize(redacted), 4000);
            }

            var e = TechnicalEvent.Create(
                timestampUtc: DateTime.UtcNow,
                severity: severity,
                category: Trim(entry.Category, 32)!,
                eventType: Trim(entry.EventType, 64)!,
                message: SensitiveDataRedactor.Redact(entry.Message, 2000) ?? string.Empty,
                fingerprint: fingerprint,
                messageTemplate: SensitiveDataRedactor.Redact(template, 1000),
                exceptionType: Trim(entry.ExceptionType, 256),
                stackTrace: SensitiveDataRedactor.Redact(entry.StackTrace, 8000),
                correlationId: Trim(entry.CorrelationId ?? TechnicalContext.CorrelationId, 64),
                requestId: Trim(entry.RequestId ?? ctxRequestId, 64),
                userId: userId,
                route: Trim(entry.Route ?? ctxRoute, 256),
                orderId: entry.OrderId,
                integration: Trim(entry.Integration, 32),
                source: Trim(entry.Source, 256),
                environment: Trim(environment.EnvironmentName, 32),
                properties: props);

            queue.Enqueue(e);
            foreach (var o in _observers)
            {
                try { o.OnRecorded(e); } catch { /* observers are best-effort */ }
            }
        }
        catch
        {
            // Diagnostics must never break the application — but a lost event is counted.
            queue.CountCaptureFailure();
        }
    }

    /// <summary>
    /// User/route/request id of the ambient request, if it is still live. Fire-and-forget work
    /// started by a request (e.g. the email queue's Task.Run) keeps seeing that request's
    /// HttpContext through the async-local accessor; once the response has completed Kestrel
    /// recycles the context and touching it throws. That must never cost us the event itself,
    /// so a recycled context is treated as "no request".
    /// </summary>
    private (Guid? UserId, string? Route, string? RequestId) RequestSnapshot()
    {
        try
        {
            var http = httpContextAccessor.HttpContext;
            if (http is null) return (null, null, null);
            Guid? userId = Guid.TryParse(http.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;
            return (userId, http.Request.Method + " " + http.Request.Path.Value, http.TraceIdentifier);
        }
        catch (ObjectDisposedException)
        {
            return (null, null, null);
        }
        catch (NullReferenceException)
        {
            return (null, null, null);
        }
    }

    public static string Fingerprint(params string?[] parts)
    {
        var raw = string.Join("|", parts.Select(p => p ?? string.Empty));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>Queues integration-call rows with masked recipients and redacted details. Never throws.</summary>
public sealed class IntegrationCallRecorder(DiagnosticsQueue queue) : IIntegrationCallRecorder
{
    public void Record(IntegrationCallEntry entry)
    {
        try
        {
            queue.Enqueue(IntegrationCall.Create(
                DateTime.UtcNow,
                entry.Integration,
                entry.Operation,
                entry.Outcome,
                entry.Succeeded,
                entry.DurationMs,
                entry.HttpStatus,
                entry.Reference is { Length: > 100 } r ? r[..100] : entry.Reference,
                entry.OrderId,
                entry.Template,
                entry.RecipientCategory,
                SensitiveDataRedactor.MaskEmail(entry.RecipientEmail),
                SensitiveDataRedactor.Redact(entry.Detail, 1000),
                TechnicalContext.CorrelationId));
        }
        catch
        {
            // Diagnostics must never break the application — but a lost event is counted.
            queue.CountCaptureFailure();
        }
    }
}
