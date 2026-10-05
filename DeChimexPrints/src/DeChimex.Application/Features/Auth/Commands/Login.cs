using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Auth;
using DeChimex.Domain.Repositories;
using FluentValidation;

namespace DeChimex.Application.Features.Auth.Commands;

public sealed record LoginCommand(LoginRequest Request, string? IpAddress) : ICommand<AuthResponse>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Request.Password).NotEmpty();
    }
}

/// <summary>
/// Authenticates a user with brute-force protection (account lockout) and issues tokens.
/// Failure messages are intentionally generic to avoid user enumeration.
/// </summary>
public sealed class LoginHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    IRoleRepository roles,
    IPermissionRepository permissions,
    IUnitOfWork uow) : ICommandHandler<LoginCommand, AuthResponse>
{
    private const string InvalidCredentials = "Invalid email or password.";

    public async Task<AuthResponse> Handle(LoginCommand command, CancellationToken ct)
    {
        var user = await users.GetWithRolesAsync(
            (await users.GetByEmailAsync(command.Request.Email, ct))?.Id ?? Guid.Empty, ct);

        if (user is null)
            throw new UnauthorizedAppException(InvalidCredentials);

        if (user.IsLockedOut)
            throw new UnauthorizedAppException("Account is temporarily locked due to failed login attempts. Try again later.");

        if (!user.IsActive)
            throw new ForbiddenException("This account has been disabled.");

        if (!hasher.Verify(command.Request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin();
            users.Update(user);
            await uow.SaveChangesAsync(ct);
            throw new UnauthorizedAppException(InvalidCredentials);
        }

        // AUD-025: transparently upgrade hashes created under older, weaker parameters
        // (the self-describing hash format makes this a re-hash + persist, no migration).
        if (hasher.NeedsRehash(user.PasswordHash))
        {
            user.ChangePassword(hasher.Hash(command.Request.Password));
        }

        // Gated signup: public registrants stay locked out until they enter the token from
        // the verification email. Staff/seeded accounts are verified at creation, so this
        // only ever blocks unverified self-registrations.
        if (!user.IsEmailVerified)
            throw new EmailNotVerifiedException("Your email is not verified yet. Enter the verification token we emailed you to finish signing up.");

        user.RecordSuccessfulLogin();

        var roleIds = user.Roles.Select(r => r.RoleId).ToList();
        var roleEntities = await roles.GetByIdsAsync(roleIds, ct);
        var roleNames = roleEntities.Select(r => r.Name).ToList();
        var permissionNames = await permissions.GetPermissionNamesForUserAsync(user.Id, ct);

        users.Update(user);
        await uow.SaveChangesAsync(ct);

        return new AuthResponse(user.Id, user.Email.Value, user.FullName,
            roleNames, permissionNames, user.IsEmailVerified, user.MustChangePassword,
            user.SecurityStamp);
    }
}