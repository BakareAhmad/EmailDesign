using Serilog.Context;

namespace DeChimex.Host.Middleware;

/// <summary>
/// Ensures every request carries a correlation id (echoed in the response header and pushed
/// into the Serilog LogContext for structured, traceable logging).
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : context.TraceIdentifier;

        // Bound the caller-supplied value (it is echoed and stored in diagnostics).
        if (correlationId.Length > 64) correlationId = correlationId[..64];

        context.Response.Headers[HeaderName] = correlationId;
        // Ambient correlation for technical events / integration calls (flows into background
        // continuations such as the email queue).
        DeChimex.Shared.Diagnostics.TechnicalContext.CorrelationId = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
