namespace DeChimex.Domain.Exceptions;

/// <summary>Thrown when a domain business rule is violated.</summary>
public sealed class BusinessRuleValidationException : DomainException
{
    public BusinessRuleValidationException(string code, string message) : base(code, message) { }
}
