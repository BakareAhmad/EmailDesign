using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Auth;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Auth.Commands;

public sealed record ResendVerificationEmailCommand(ResendVerificationEmailRequest Request) : ICommand<Unit>;

public sealed class ResendVerificationEmailValidator : AbstractValidator<ResendVerificationEmailCommand>
{
    public ResendVerificationEmailValidator() => RuleFor(x => x.Request.Email).NotEmpty().EmailAddress();
}

/// <summary>
/// Regenerates and re-sends the email-verification link. Returns a generic success whether or not
/// the account exists or is already verified, so the endpoint cannot be used to enumerate accounts.
/// </summary>
public sealed class ResendVerificationEmailHandler(
    IUserRepository users,
    ISecureTokenGenerator tokenGenerator,
    IUnitOfWork uow) : ICommandHandler<ResendVerificationEmailCommand, Unit>
{
    public async Task<Unit> Handle(ResendVerificationEmailCommand command, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(command.Request.Email, ct);
        if (user is null || user.IsEmailVerified)
            return Unit.Value; // silent success

        user.RequestEmailVerification(tokenGenerator.GenerateNumericToken()); // 6-digit OTP: the user types this
        users.Update(user);
        await uow.SaveChangesAsync(ct); // dispatches VerificationEmailRequestedEvent -> email
        return Unit.Value;
    }
}
