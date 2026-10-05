using System.Text;
using System.Text.Json;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Contracts.Common;
using DeChimex.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DeChimex.Host.Middleware;

/// <summary>
/// Global exception handler. Maps domain/application exceptions to standardised API responses
/// and correct HTTP status codes for /api requests. For browser (MVC) requests it renders a
/// styled HTML error page instead of a raw JSON payload, while keeping the same status codes,
/// messages, and logging. Never leaks stack traces; logs every unexpected error.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        // The response has already begun streaming: headers and part of the body are gone
        // out to the client. Clear()/StatusCode would throw here and mask the real error,
        // so let the exception propagate - Kestrel aborts the connection and the browser
        // shows its own network-error UI rather than a corrupt half page.
        if (context.Response.HasStarted)
            throw exception;

        var traceId = context.TraceIdentifier;

        var (status, message) = exception switch
        {
            ValidationAppException v => (StatusCodes.Status400BadRequest,
                "One or more validation errors occurred."),

            NotFoundException nf => (StatusCodes.Status404NotFound, nf.Message),

            ConflictException c => (StatusCodes.Status409Conflict, c.Message),

            ForbiddenException f => (StatusCodes.Status403Forbidden, f.Message),

            UnauthorizedAppException u => (StatusCodes.Status401Unauthorized, u.Message),

            BusinessRuleValidationException b => (StatusCodes.Status422UnprocessableEntity, b.Message),

            DomainException d => (StatusCodes.Status422UnprocessableEntity, d.Message),

            OperationCanceledException => (499, "The request was cancelled."),

            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred. Please try again later.")
        };

        // MED-005: enrich error logs with request context (method, path, user). The correlation
        // id is already attached via the Serilog LogContext pushed by CorrelationIdMiddleware.
        var requestContext = $"{context.Request.Method} {context.Request.Path}";
        var user = context.User?.Identity is { IsAuthenticated: true } auth
            ? auth.Name ?? "authenticated"
            : "anonymous";

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception on {RequestContext} (user: {User}). TraceId: {TraceId}",
                requestContext, user, traceId);
        else
            logger.LogWarning("Handled {ExceptionType} on {RequestContext} (user: {User}): {Message}. TraceId: {TraceId}",
                exception.GetType().Name, requestContext, user, message, traceId);

        context.Response.Clear();
        context.Response.StatusCode = status;

        if (ErrorPageWriter.IsBrowserHtmlRequest(context))
        {
            // For validation errors include the per-field messages on the HTML page.
            var fieldErrors = exception is ValidationAppException va
                ? va.Errors
                : null;
            await ErrorPageWriter.WriteAsync(context, status, message, traceId, fieldErrors);
            return;
        }

        var errors = exception is ValidationAppException v2 ? v2.Errors : null;
        var code = exception switch
        {
            ValidationAppException => "validation.failed",
            NotFoundException nf => nf.Code,
            ConflictException c => c.Code,
            ForbiddenException => "forbidden",
            UnauthorizedAppException => "unauthorized",
            DomainException d => d.Code,
            OperationCanceledException => "request.cancelled",
            _ => "server.error"
        };
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(
            ApiResponse.Fail(message, code, errors, traceId), JsonOptions));
    }
}
