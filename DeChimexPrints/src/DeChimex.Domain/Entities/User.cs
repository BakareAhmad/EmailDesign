using DeChimex.Domain.Common;
using DeChimex.Domain.Events;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Login account aggregate for both staff and customers. Encapsulates password hash,
/// email verification, account lockout (brute-force protection) and role assignment.
/// The aggregate never sees plain passwords. Hashing is performed by an infrastructure
/// service and the resulting hash is passed in.
/// </summary>
public sealed class User : AggregateRoot
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly List<UserRole> _roles = new();

    private User() { } // EF

    private User(Email email, string passwordHash, string firstName, string lastName, string? verificationToken)
    {
        Email = email;
        PasswordHash = passwordHash;
        FirstName = firstName;
        LastName = lastName;
        IsActive = true;
        IsEmailVerified = false;
        EmailVerificationToken = verificationToken;
        EmailVerificationTokenExpiresUtc = DateTime.UtcNow.AddHours(24);
    }

    public Email Email { get; private set; } = default!;
    public string PasswordHash { get; private set; } = default!;
    public string FirstName { get; private set; } = default!;
    public string LastName { get; private set; } = default!;

    /// <summary>Staff username (unique). Null for customer accounts, which never
    /// need one. The username is the ONLY staff identity shown in customer-facing
    /// chat; staff names and emails must never substitute for it there.</summary>
    public ValueObjects.Username? Username { get; private set; }

    /// <summary>Customer-safe staff identity for chat: the username, or the
    /// generic "Staff" label when no username exists. Never a name or email.</summary>
    public string StaffDisplayName => Username?.Value ?? "Staff";
    public bool IsActive { get; private set; }
    public bool IsEmailVerified { get; private set; }

    public string? EmailVerificationToken { get; private set; }
    public DateTime? EmailVerificationTokenExpiresUtc { get; private set; }

    /// <summary>Failed verification attempts against the CURRENT token; the token is
    /// invalidated (and must be resent) once this reaches <see cref="MaxEmailVerificationAttempts"/>.
    /// Brute-force cap required because the emailed code is a short numeric OTP.</summary>
    public const int MaxEmailVerificationAttempts = 10;
    public int EmailVerificationAttempts { get; private set; }

    public string? PasswordResetToken { get; private set; }
    public DateTime? PasswordResetTokenExpiresUtc { get; private set; }

    /// <summary>
    /// Set for the seeded bootstrap owner: the user must choose a new password on
    /// first login before using the rest of the admin area. Cleared by any
    /// successful password change.
    /// </summary>
    public bool MustChangePassword { get; private set; }

    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public DateTime? LastLoginUtc { get; private set; }

    /// <summary>
    /// Authority version (AUD-006). Re-generated whenever the facts baked into the auth
    /// cookie change (activation, roles, role permissions, password). The per-request
    /// freshness middleware compares the stamp claim against this value and re-issues
    /// (or rejects) the session, so permission changes and account disabling take effect
    /// on the very next request instead of at the next login.
    /// </summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    public string FullName => $"{FirstName} {LastName}".Trim();
    public bool IsLockedOut => LockoutEndUtc.HasValue && LockoutEndUtc.Value > DateTime.UtcNow;

    public IReadOnlyCollection<UserRole> Roles => _roles.AsReadOnly();

    public static User Register(string email, string passwordHash, string firstName, string lastName, string verificationToken)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new BusinessRuleValidationException("user.password", "Password hash is required.");
        if (string.IsNullOrWhiteSpace(firstName))
            throw new BusinessRuleValidationException("user.firstname", "First name is required.");
        if (string.IsNullOrWhiteSpace(lastName))
            throw new BusinessRuleValidationException("user.lastname", "Last name is required.");

        var user = new User(Email.Create(email), passwordHash, firstName.Trim(), lastName.Trim(), verificationToken);
        user.RaiseDomainEvent(new UserRegisteredEvent(user.Id, user.Email.Value, user.FirstName, verificationToken));
        return user;
    }

    /// <summary>Creates a staff account (created by the Owner). It is pre-verified, no self-service email.
    /// A username is mandatory: it becomes the account's customer-facing chat identity.</summary>
    public static User CreateStaff(string email, string passwordHash, string firstName, string lastName, string username)
    {
        var user = new User(Email.Create(email), passwordHash, firstName.Trim(), lastName.Trim(), null)
        {
            IsEmailVerified = true,
            EmailVerificationToken = null,
            EmailVerificationTokenExpiresUtc = null,
            Username = Username.Create(username)
        };
        return user;
    }

    /// <summary>Changes the staff username (validated + normalised by <see cref="Username"/>).
    /// Callers propagate the new name to the account's chat history so customer-facing
    /// conversations keep showing exactly one identity for the author.</summary>
    public void SetUsername(string username) => Username = ValueObjects.Username.Create(username);

    public void AssignRole(Guid roleId)
    {
        if (_roles.Any(r => r.RoleId == roleId)) return;
        _roles.Add(UserRole.Create(Id, roleId));
    }


    public void SetRoles(IEnumerable<Guid> roleIds)
    {
        _roles.Clear();
        foreach (var id in roleIds.Distinct()) _roles.Add(UserRole.Create(Id, id));
        BumpSecurityStamp(); // AUD-006: role changes alter what the cookie is allowed to do
    }

    public void RecordSuccessfulLogin()
    {
        FailedLoginAttempts = 0;
        LockoutEndUtc = null;
        LastLoginUtc = DateTime.UtcNow;
    }

    /// <summary>Increments failed attempts and locks the account after the threshold.</summary>
    public void RecordFailedLogin()
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= MaxFailedAttempts)
        {
            LockoutEndUtc = DateTime.UtcNow.Add(LockoutDuration);
            FailedLoginAttempts = 0;
        }
    }

    public void VerifyEmail(string token)
    {
        if (IsEmailVerified) return;
        if (EmailVerificationAttempts >= MaxEmailVerificationAttempts)
            throw new BusinessRuleValidationException("user.verify.locked",
                "Too many incorrect attempts. Request a new verification email (the code has been invalidated).");
        if (EmailVerificationToken is null || EmailVerificationToken != token)
        {
            EmailVerificationAttempts++;
            if (EmailVerificationAttempts >= MaxEmailVerificationAttempts)
            {
                // Invalidate the code: a 6-digit OTP has a small keyspace, so cap brute force.
                EmailVerificationToken = null;
                EmailVerificationTokenExpiresUtc = null;
                throw new BusinessRuleValidationException("user.verify.locked",
                    "Too many incorrect attempts. Request a new verification email (the code has been invalidated).");
            }
            throw new BusinessRuleValidationException("user.verify.token", "Invalid verification token.");
        }
        if (EmailVerificationTokenExpiresUtc.HasValue && EmailVerificationTokenExpiresUtc.Value < DateTime.UtcNow)
            throw new BusinessRuleValidationException("user.verify.expired", "Verification token has expired.");

        IsEmailVerified = true;
        EmailVerificationToken = null;
        EmailVerificationTokenExpiresUtc = null;
        EmailVerificationAttempts = 0;
        RaiseDomainEvent(new UserEmailVerifiedEvent(Id, Email.Value, FirstName));
    }

    /// <summary>Regenerates the email-verification token (e.g. "resend email") and raises an event to send it.</summary>
    public void RequestEmailVerification(string token)
    {
        if (IsEmailVerified) return;
        EmailVerificationToken = token;
        EmailVerificationTokenExpiresUtc = DateTime.UtcNow.AddHours(24);
        EmailVerificationAttempts = 0; // fresh code => fresh attempt budget
        RaiseDomainEvent(new VerificationEmailRequestedEvent(Id, Email.Value, FirstName, token));
    }

    public void RequestPasswordReset(string resetToken, TimeSpan validFor)
    {
        PasswordResetToken = resetToken;
        PasswordResetTokenExpiresUtc = DateTime.UtcNow.Add(validFor);
        RaiseDomainEvent(new PasswordResetRequestedEvent(Id, Email.Value, FirstName, resetToken));
    }

    public void ResetPassword(string token, string newPasswordHash)
    {
        if (PasswordResetToken is null || PasswordResetToken != token)
            throw new BusinessRuleValidationException("user.reset.token", "Invalid reset token.");
        if (PasswordResetTokenExpiresUtc.HasValue && PasswordResetTokenExpiresUtc.Value < DateTime.UtcNow)
            throw new BusinessRuleValidationException("user.reset.expired", "Reset token has expired.");

        PasswordHash = newPasswordHash;
        PasswordResetToken = null;
        PasswordResetTokenExpiresUtc = null;
        FailedLoginAttempts = 0;
        LockoutEndUtc = null;
        MustChangePassword = false;
        BumpSecurityStamp(); // a password change invalidates the previous session's authority
    }

    public void ChangePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        MustChangePassword = false;
        BumpSecurityStamp(); // the must_change_password claim is stale the moment this succeeds
    }

    /// <summary>Seeder-only: flags the bootstrap owner to change the default password on first login.</summary>
    public void SetMustChangePassword() => MustChangePassword = true;

    public void Activate() { IsActive = true; BumpSecurityStamp(); }
    public void Disable() { IsActive = false; BumpSecurityStamp(); }

    /// <summary>Regenerates the authority version. Called by every mutation that changes
    /// what the auth cookie asserts (roles, activation, password).</summary>
    public void BumpSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");
}
