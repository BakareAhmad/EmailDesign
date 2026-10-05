using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Auth;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentValidation;

namespace DeChimex.Application.Features.Auth.Commands;

public sealed record RegisterCommand(RegisterRequest Request, string? IpAddress) : ICommand<AuthResponse>;

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Request.FirstName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Request.LastName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Request.Phone).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Request.Password)
            .NotEmpty().MinimumLength(8).MaximumLength(128)
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
    }
}

/// <summary>
/// Registers a customer account: creates the login user, a linked customer profile, assigns the
/// Customer role, and issues tokens. Email verification is triggered via a domain event.
/// Anti-enumeration (AUD-010): when the email is already registered the handler answers with
/// the SAME success-shaped response as a fresh registration — no conflict, no oracle — so the
/// form flow simply tells the visitor a verification code was sent (ForgotPassword and
/// ResendVerification already behave this way).
/// </summary>
public sealed class RegisterHandler(
    IUserRepository users,
    ICustomerRepository customers,
    IRoleRepository roles,
    IPasswordHasher hasher,
    ISecureTokenGenerator tokenGenerator,
    IPermissionRepository permissions,
    IUnitOfWork uow) : ICommandHandler<RegisterCommand, AuthResponse>
{
    public async Task<AuthResponse> Handle(RegisterCommand command, CancellationToken ct)
    {
        var r = command.Request;
        if (await users.EmailExistsAsync(r.Email, ct))
        {
            // Pretend success: identical response shape, no reveal, no email storm to the
            // existing address (an attacker could otherwise use the endpoint to spam it).
            return new AuthResponse(Guid.Empty, r.Email, $"{r.FirstName} {r.LastName}".Trim(),
                new[] { RoleNames.Customer }, Array.Empty<string>(), IsEmailVerified: false);
        }

        var passwordHash = hasher.Hash(r.Password);
        var verificationToken = tokenGenerator.GenerateNumericToken(); // 6-digit OTP: the user types this
        var user = User.Register(r.Email, passwordHash, r.FirstName, r.LastName, verificationToken);

        var customerRole = await roles.GetByNameAsync(RoleNames.Customer, ct)
            ?? throw new NotFoundException("Role", RoleNames.Customer);
        user.AssignRole(customerRole.Id);
        await users.AddAsync(user, ct);

        var customer = Customer.Create(r.FirstName, r.LastName, r.Email, r.Phone, user.Id);
        await customers.AddAsync(customer, ct);

        var roleNames = new[] { RoleNames.Customer };
        var permissionNames = await permissions.GetPermissionNamesForUserAsync(user.Id, ct);

        await uow.SaveChangesAsync(ct); // dispatches UserRegisteredEvent -> verification email

        return new AuthResponse(user.Id, user.Email.Value, user.FullName,
            roleNames, permissionNames, user.IsEmailVerified,
            SecurityStamp: user.SecurityStamp);
    }
}
