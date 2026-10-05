using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DeChimex.Application.Common.Behaviors;

/// <summary>Logs a warning for requests that exceed a latency budget.</summary>
public sealed class PerformanceBehavior<TRequest, TResponse>(ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private const long WarnThresholdMs = 500;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var response = await next();
        sw.Stop();
        if (sw.ElapsedMilliseconds > WarnThresholdMs)
            logger.LogWarning("Long-running request {RequestName} took {ElapsedMs}ms", typeof(TRequest).Name, sw.ElapsedMilliseconds);
        return response;
    }
}
