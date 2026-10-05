namespace DeChimex.Domain.Exceptions;

/// <summary>Base exception for violations of domain invariants and business rules.</summary>
public class DomainException : Exception
{
    public string Code { get; }

    public DomainException(string message) : base(message) => Code = "domain.error";
    public DomainException(string code, string message) : base(message) => Code = code;
}
