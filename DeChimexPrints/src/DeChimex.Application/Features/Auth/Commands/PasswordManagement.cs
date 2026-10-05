using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Auth;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Auth.Commands;

// ---- Verify Email ----
public sealed record VerifyEmailCommand(VerifyEmailRequest Request) : ICommand<AuthResponse>;

public sealed class VerifyEmailValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailValidator()
    {
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Request.Token).NotEmpty();
    }
}

/// <summary>
/// Completes signup: checks the token from the verification email, marks the user verified,
/// and returns an AuthResponse so the MVC layer can sign the user in only AFTER successful
/// verification (gated signup). Raises UserEmailVerifiedEvent -> welcome email.
/// </summary>
public sealed class VerifyEmailHandler(IUserRepository users, IRoleRepository roles, IPermissionRepository permissions, IUnitOfWork uow)
    : ICommandHandler<VerifyEmailCommand, AuthResponse>
{
    public async Task<AuthResponse> Handle(VerifyEmailCommand command, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(command.Request.Email, ct)
            ?? throw new NotFoundException("User", command.Request.Email);
        try
        {
            user.VerifyEmail(command.Request.Token); // raises UserEmailVerifiedEvent (welcome email)
        }
        catch (BusinessRuleValidationException)
        {
            // The failed-attempt counter (brute-force cap for the 6-digit OTP) must be
            // persisted even though verification failed, otherwise the cap never sticks.
            users.Update(user);
            await uow.SaveChangesAsync(ct);
            throw;
        }
        users.Update(user);
        await uow.SaveChangesAsync(ct);

        // GetByEmailAsync does not load the Roles navigation; the cookie claims need them
        // (same two-step load LoginHandler uses), else the portal rejects the session.
        var withRoles = await users.GetWithRolesAsync(user.Id, ct) ?? user;
        var roleEntities = await roles.GetByIdsAsync(withRoles.Roles.Select(r => r.RoleId).ToList(), ct);
        var permissionNames = await permissions.GetPermissionNamesForUserAsync(user.Id, ct);
        return new AuthResponse(user.Id, user.Email.Value, user.FullName,
            roleEntities.Select(r => r.Name).ToList(), permissionNames, user.IsEmailVerified,
            SecurityStamp: user.SecurityStamp);
    }
}

// ---- Forgot Password ----
public sealed record ForgotPasswordCommand(ForgotPasswordRequest Request) : ICommand<Unit>;

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator() => RuleFor(x => x.Request.Email).NotEmpty().EmailAddress();
}

/// <summary>Always returns success to avoid user enumeration; only sends mail if the user exists.</summary>
public sealed class ForgotPasswordHandler(
    IUserRepository users, ISecureTokenGenerator tokenGenerator, IUnitOfWork uow)
    : ICommandHandler<ForgotPasswordCommand, Unit>
{
    public async Task<Unit> Handle(ForgotPasswordCommand command, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(command.Request.Email, ct);
        if (user is not null && user.IsActive)
        {
            var token = tokenGenerator.GenerateToken();
            user.RequestPasswordReset(token, TimeSpan.FromHours(1)); // raises PasswordResetRequestedEvent
            users.Update(user);
            await uow.SaveChangesAsync(ct);
        }
        return Unit.Value;
    }
}

// ---- Reset Password ----
public sealed record ResetPasswordCommand(ResetPasswordRequest Request) : ICommand<Unit>;

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Request.Token).NotEmpty();
        RuleFor(x => x.Request.NewPassword)
            .NotEmpty().MinimumLength(8).MaximumLength(128)
            .Matches("[A-Z]").Matches("[a-z]").Matches("[0-9]");
    }
}

public sealed class ResetPasswordHandler(IUserRepository users, IPasswordHasher hasher, IUnitOfWork uow)
    : ICommandHandler<ResetPasswordCommand, Unit>
{
    public async Task<Unit> Handle(ResetPasswordCommand command, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(command.Request.Email, ct)
            ?? throw new NotFoundException("User", command.Request.Email);
        var newHash = hasher.Hash(command.Request.NewPassword);
        user.ResetPassword(command.Request.Token, newHash);
        users.Update(user);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Change Password (authenticated) ----
public sealed record ChangePasswordCommand(ChangePasswordRequest Request) : ICommand<Unit>;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.Request.CurrentPassword).NotEmpty();
        RuleFor(x => x.Request.NewPassword)
            .NotEmpty().MinimumLength(8).MaximumLength(128)
            .Matches("[A-Z]").Matches("[a-z]").Matches("[0-9]");
    }
}

public sealed class ChangePasswordHandler(
    IUserRepository users, IPasswordHasher hasher, ICurrentUser currentUser, IUnitOfWork uow,
    IEmailQueue emailQueue, IEmailTemplateRenderer emailRenderer)
    : ICommandHandler<ChangePasswordCommand, Unit>
{
    public async Task<Unit> Handle(ChangePasswordCommand command, CancellationToken ct)
    {
        if (currentUser.UserId is null)
            throw new UnauthorizedAppException();
        var user = await users.GetByIdAsync(currentUser.UserId.Value, ct)
            ?? throw new NotFoundException("User", currentUser.UserId.Value);
        if (!hasher.Verify(command.Request.CurrentPassword, user.PasswordHash))
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["CurrentPassword"] = new[] { "Current password is incorrect." }
            });
        user.ChangePassword(hasher.Hash(command.Request.NewPassword));
        users.Update(user);
        await uow.SaveChangesAsync(ct);

        // Security notification: the customer changed their password from inside the app
        // (the forgot-password reset path already sends its own confirmation email).
        // Fire-and-forget: a mail outage must never fail the password change itself.
        try
        {
            var content = emailRenderer.Render(EmailTemplate.PasswordResetConfirmation,
                new Dictionary<string, string> { ["FirstName"] = user.FirstName });
            await emailQueue.EnqueueAsync(new EmailMessage(user.Email.Value, user.FullName,
                content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.PasswordResetConfirmation), Category: "customer"), ct);
        }
        catch
        {
            // Swallowed by design: the change itself already succeeded.
        }

        return Unit.Value;
    }
}
