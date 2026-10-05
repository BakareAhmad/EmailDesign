using DeChimex.Domain.Common;

namespace DeChimex.Domain.Events;

public sealed record UserRegisteredEvent(Guid UserId, string Email, string FirstName, string EmailVerificationToken) : DomainEvent;
public sealed record VerificationEmailRequestedEvent(Guid UserId, string Email, string FirstName, string EmailVerificationToken) : DomainEvent;
public sealed record PasswordResetRequestedEvent(Guid UserId, string Email, string FirstName, string ResetToken) : DomainEvent;
public sealed record UserEmailVerifiedEvent(Guid UserId, string Email, string FirstName) : DomainEvent;
