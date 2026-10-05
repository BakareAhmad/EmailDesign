namespace DeChimex.Contracts.Auth;

public sealed record RegisterRequest(string FirstName, string LastName, string Email, string Phone, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record VerifyEmailRequest(string Email, string Token);
public sealed record ResendVerificationEmailRequest(string Email);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Result of login/register. No access/refresh tokens: MVC auth signs the user in via
/// cookie (JWT token service removed as part of the JWT-independence change).
/// </summary>
public sealed record AuthResponse(
    Guid UserId, string Email, string FullName,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions,
    bool IsEmailVerified, bool MustChangePassword = false,
    string SecurityStamp = "");


public sealed record CreateStaffUserRequest(string FirstName, string LastName, string Email, string Password, string Username, IReadOnlyList<Guid> RoleIds);
public sealed record AssignRolesRequest(IReadOnlyList<Guid> RoleIds);
public sealed record CreateRoleRequest(string Name, string? Description);
public sealed record UpdateRoleRequest(string Name, string? Description);
public sealed record UpdateRolePermissionsRequest(IReadOnlyList<Guid> PermissionIds);
public sealed record UserDto(Guid Id, string Email, string FullName, string? Username, bool IsActive, bool IsEmailVerified, DateTime? LastLoginUtc, IReadOnlyList<string> Roles);
public sealed record RoleDto(Guid Id, string Name, string? Description, bool IsSystemRole, IReadOnlyList<string> Permissions, int UserCount = 0);
public sealed record PermissionDto(Guid Id, string Name, string? Description);
