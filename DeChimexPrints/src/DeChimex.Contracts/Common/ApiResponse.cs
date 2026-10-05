namespace DeChimex.Contracts.Common;

/// <summary>Standardised API response envelope for all endpoints.</summary>
public sealed class ApiResponse<T>
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public T? Data { get; init; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
    public string? ErrorCode { get; init; }
    public string? TraceId { get; init; }

    public static ApiResponse<T> Ok(T data, string? message = null) =>
        new() { Success = true, Data = data, Message = message };

    public static ApiResponse<T> Fail(string message, string? errorCode = null,
        IReadOnlyDictionary<string, string[]>? errors = null, string? traceId = null) =>
        new() { Success = false, Message = message, ErrorCode = errorCode, Errors = errors, TraceId = traceId };
}

/// <summary>Non-generic helper for messages without a payload.</summary>
public sealed class ApiResponse
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
    public string? ErrorCode { get; init; }
    public string? TraceId { get; init; }

    public static ApiResponse Ok(string? message = null) => new() { Success = true, Message = message };
    public static ApiResponse Fail(string message, string? errorCode = null,
        IReadOnlyDictionary<string, string[]>? errors = null, string? traceId = null) =>
        new() { Success = false, Message = message, ErrorCode = errorCode, Errors = errors, TraceId = traceId };
}
