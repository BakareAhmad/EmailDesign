namespace DeChimex.Application.Common.Exceptions;

public sealed class NotFoundException : Exception
{
    public string Code { get; }
    public NotFoundException(string entity, object key)
        : base($"{entity} with key '{key}' was not found.") => Code = $"{entity.ToLowerInvariant()}.notfound";
    public NotFoundException(string message) : base(message) => Code = "resource.notfound";
}

public sealed class ConflictException : Exception
{
    public string Code { get; }
    public ConflictException(string code, string message) : base(message) => Code = code;
}

public class ForbiddenException : Exception
{
    public ForbiddenException(string message = "You do not have permission to perform this action.") : base(message) { }
}

/// <summary>
/// Thrown by LoginHandler when a self-registered account has not completed email
/// verification yet (gated signup). Subclasses ForbiddenException so existing generic
/// handlers/middleware keep treating it as a 403-style failure, while the MVC login
/// flow can catch the specific type and route the user to the verify-email page
/// (the login form has no token field, so showing the message there strands them).
/// </summary>
public sealed class EmailNotVerifiedException : ForbiddenException
{
    public EmailNotVerifiedException(string message) : base(message) { }
}

public sealed class UnauthorizedAppException : Exception
{
    public UnauthorizedAppException(string message = "Authentication is required.") : base(message) { }
}

public sealed class ValidationAppException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }
    public ValidationAppException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation failures occurred.") => Errors = errors;
}

/// <summary>
/// Optimistic-concurrency conflict at the persistence boundary (AUD-002). Thrown by the
/// UnitOfWork when the database rejects a write because another request changed the same
/// row first (Order/Payment rowversion). Handlers catch it to retry-and-recheck instead of
/// surfacing a 500. Kept in Application so the layer stays free of EF Core types.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException() : base("The record was changed by another operation. Retry the request.") { }
}
