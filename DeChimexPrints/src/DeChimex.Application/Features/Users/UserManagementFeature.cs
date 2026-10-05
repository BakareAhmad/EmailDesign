using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Auth;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using DeChimex.Domain.ValueObjects;
using DeChimex.Shared.Constants;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Users;

// ---- Create staff user (Owner only) ----
public sealed record CreateStaffUserCommand(CreateStaffUserRequest Request) : ICommand<UserDto>;

public sealed class CreateStaffUserValidator : AbstractValidator<CreateStaffUserCommand>
{
    public CreateStaffUserValidator()
    {
        RuleFor(x => x.Request.FirstName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Request.LastName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress();
        // Mirrors Username.Create (which lower-cases first, hence the
        // case-insensitive match): the domain type remains the final backstop.
        RuleFor(x => x.Request.Username).NotEmpty().WithMessage("Username is required.")
            .MinimumLength(Username.MinLength)
            .MaximumLength(Username.MaxLength)
            .Matches($"(?i){Username.AllowedPattern}")
            .WithMessage("Username may only contain letters, digits, dots, underscores and hyphens, and must start and end with a letter or digit.")
            .Must(u => !Username.IsReserved((u ?? string.Empty).Trim().ToLowerInvariant()))
            .WithMessage("That username is reserved. Choose a different username.");
        RuleFor(x => x.Request.Password).NotEmpty().MinimumLength(8)
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
        RuleFor(x => x.Request.RoleIds).NotEmpty().WithMessage("Select at least one role.");
    }
}

public sealed class CreateStaffUserHandler(
    IUserRepository users, IRoleRepository roles, IPasswordHasher hasher, IUnitOfWork uow)
    : ICommandHandler<CreateStaffUserCommand, UserDto>
{
    public async Task<UserDto> Handle(CreateStaffUserCommand c, CancellationToken ct)
    {
        var r = c.Request;
        if (await users.EmailExistsAsync(r.Email, ct))
            throw new ConflictException("user.email.exists", "A user with this email already exists.");
        if (await users.UsernameExistsAsync(r.Username, null, ct))
            throw new ConflictException("user.username.exists", "That username is already taken.");

        var user = User.CreateStaff(r.Email, hasher.Hash(r.Password), r.FirstName, r.LastName, r.Username);
        var roleEntities = await roles.GetByIdsAsync(r.RoleIds, ct);
        if (roleEntities.Count != r.RoleIds.Count)
            throw new NotFoundException("One or more roles were not found.");
        user.SetRoles(roleEntities.Select(x => x.Id));

        await users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);

        return new UserDto(user.Id, user.Email.Value, user.FullName, user.Username?.Value, user.IsActive, user.IsEmailVerified,
            user.LastLoginUtc, roleEntities.Select(x => x.Name).ToList());
    }
}

// ---- Change a staff username (propagates to the author's chat history) ----
public sealed record SetUsernameCommand(Guid UserId, string Username) : ICommand<Unit>;

public sealed class SetUsernameValidator : AbstractValidator<SetUsernameCommand>
{
    public SetUsernameValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Username).NotEmpty().WithMessage("Username is required.")
            .MinimumLength(Username.MinLength)
            .MaximumLength(Username.MaxLength)
            .Matches($"(?i){Username.AllowedPattern}")
            .WithMessage("Username may only contain letters, digits, dots, underscores and hyphens, and must start and end with a letter or digit.")
            .Must(u => !Username.IsReserved((u ?? string.Empty).Trim().ToLowerInvariant()))
            .WithMessage("That username is reserved. Choose a different username.");
    }
}

public sealed class SetUsernameHandler(
    IUserRepository users, IRoleRepository roles, IOrderRepository orders, IUnitOfWork uow)
    : ICommandHandler<SetUsernameCommand, Unit>
{
    public async Task<Unit> Handle(SetUsernameCommand c, CancellationToken ct)
    {
        var user = await users.GetWithRolesAsync(c.UserId, ct) ?? throw new NotFoundException("User", c.UserId);
        var roleEntities = await roles.GetByIdsAsync(user.Roles.Select(r => r.RoleId), ct);
        if (roleEntities.Any(r => r.Name == RoleNames.Customer))
            throw new BusinessRuleValidationException("user.username.customer",
                "Customer accounts do not have usernames.");
        if (await users.UsernameExistsAsync(c.Username, c.UserId, ct))
            throw new ConflictException("user.username.exists", "That username is already taken.");

        user.SetUsername(c.Username);
        users.Update(user);
        // A username change rewrites the author's past staff messages so every
        // customer-facing conversation keeps showing exactly one identity.
        // Quotes resolve the parent's name live and follow automatically.
        await orders.RenameStaffAuthorAsync(user.Id, user.Username!.Value, ct);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Disable / Enable user ----
public sealed record SetUserActiveCommand(Guid UserId, bool IsActive) : ICommand<Unit>;
public sealed class SetUserActiveHandler(IUserRepository users, IUnitOfWork uow)
    : ICommandHandler<SetUserActiveCommand, Unit>
{
    public async Task<Unit> Handle(SetUserActiveCommand c, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(c.UserId, ct) ?? throw new NotFoundException("User", c.UserId);
        if (c.IsActive) user.Activate(); else user.Disable();
        users.Update(user);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Assign roles ----
public sealed record AssignRolesCommand(Guid UserId, AssignRolesRequest Request) : ICommand<Unit>;
public sealed class AssignRolesValidator : AbstractValidator<AssignRolesCommand>
{
    public AssignRolesValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Request.RoleIds).NotEmpty();
    }
}
public sealed class AssignRolesHandler(IUserRepository users, IRoleRepository roles, IUnitOfWork uow)
    : ICommandHandler<AssignRolesCommand, Unit>
{
    public async Task<Unit> Handle(AssignRolesCommand c, CancellationToken ct)
    {
        var user = await users.GetWithRolesAsync(c.UserId, ct) ?? throw new NotFoundException("User", c.UserId);
        var roleEntities = await roles.GetByIdsAsync(c.Request.RoleIds, ct);
        if (roleEntities.Count != c.Request.RoleIds.Count)
            throw new NotFoundException("One or more roles were not found.");
        user.SetRoles(roleEntities.Select(x => x.Id));
        users.Update(user);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Role CRUD Commands ----

public sealed record CreateRoleCommand(CreateRoleRequest Request) : ICommand<RoleDto>;
public sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.Description).MaximumLength(500);
    }
}
public sealed class CreateRoleHandler(IRoleRepository roles, IUnitOfWork uow)
    : ICommandHandler<CreateRoleCommand, RoleDto>
{
    public async Task<RoleDto> Handle(CreateRoleCommand c, CancellationToken ct)
    {
        var existing = await roles.GetByNameAsync(c.Request.Name, ct);
        if (existing is not null)
            throw new ConflictException("role.name.exists", "A role with this name already exists.");

        var role = Role.Create(c.Request.Name, c.Request.Description, isSystemRole: false);
        await roles.AddAsync(role, ct);
        await uow.SaveChangesAsync(ct);

        return new RoleDto(role.Id, role.Name, role.Description, role.IsSystemRole, new List<string>(), 0);
    }
}

public sealed record UpdateRoleCommand(Guid RoleId, UpdateRoleRequest Request) : ICommand<Unit>;
public sealed class UpdateRoleValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleValidator()
    {
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.Description).MaximumLength(500);
    }
}
public sealed class UpdateRoleHandler(IRoleRepository roles, IUnitOfWork uow)
    : ICommandHandler<UpdateRoleCommand, Unit>
{
    public async Task<Unit> Handle(UpdateRoleCommand c, CancellationToken ct)
    {
        var role = await roles.GetByIdAsync(c.RoleId, ct)
            ?? throw new NotFoundException("Role", c.RoleId);

        if (role.IsSystemRole)
            throw new BusinessRuleValidationException("role.system", "System roles cannot be modified.");

        // Check name uniqueness (excluding self)
        var existing = await roles.GetByNameAsync(c.Request.Name, ct);
        if (existing is not null && existing.Id != c.RoleId)
            throw new ConflictException("role.name.exists", "A role with this name already exists.");

        role.Update(c.Request.Name, c.Request.Description);
        roles.Update(role);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record DeleteRoleCommand(Guid RoleId) : ICommand<Unit>;
public sealed class DeleteRoleHandler(IRoleRepository roles, IUnitOfWork uow)
    : ICommandHandler<DeleteRoleCommand, Unit>
{
    public async Task<Unit> Handle(DeleteRoleCommand c, CancellationToken ct)
    {
        var role = await roles.GetByIdAsync(c.RoleId, ct)
            ?? throw new NotFoundException("Role", c.RoleId);

        if (role.IsSystemRole)
            throw new BusinessRuleValidationException("role.system", "System roles cannot be deleted.");

        // Check if any users are assigned to this role
        var userCount = await roles.CountUsersInRoleAsync(c.RoleId, ct);
        if (userCount > 0)
            throw new BusinessRuleValidationException("role.in_use", $"Cannot delete role '{role.Name}' because {userCount} user(s) are assigned to it. Reassign them first.");

        roles.Remove(role);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record UpdateRolePermissionsCommand(Guid RoleId, UpdateRolePermissionsRequest Request) : ICommand<Unit>;
public sealed class UpdateRolePermissionsHandler(IRoleRepository roles, IPermissionRepository permissions,
    IUserRepository users, IUnitOfWork uow)
    : ICommandHandler<UpdateRolePermissionsCommand, Unit>
{
    public async Task<Unit> Handle(UpdateRolePermissionsCommand c, CancellationToken ct)
    {
        var role = await roles.GetWithPermissionsAsync(c.RoleId, ct)
            ?? throw new NotFoundException("Role", c.RoleId);

        // Admins own the permission checklist for every role, including the seeded
        // system roles. The seeder only assigns the canonical map when a role is
        // first created (DatabaseSeeder.SeedRolesAsync), so these edits persist
        // across application restarts.
        //
        // Developer Portal permissions (dev.*) are outside the business checklist: ids the
        // role already holds are preserved, and dev ids submitted for any role are ignored,
        // so developer access can only come from the seeded Developer role.
        var devIds = (await permissions.ListAsync(ct))
            .Where(p => DeChimex.Shared.Constants.Permissions.Developer.IsDeveloperPermission(p.Name))
            .Select(p => p.Id).ToHashSet();
        var preserved = role.Permissions.Select(rp => rp.PermissionId).Where(devIds.Contains);
        var requested = c.Request.PermissionIds.Where(id => !devIds.Contains(id));
        role.SetPermissions(requested.Concat(preserved).Distinct().ToList());
        roles.Update(role);

        // AUD-006: every user holding this role holds permissions claims in their cookie —
        // bump their SecurityStamps so SessionFreshnessMiddleware re-issues those cookies
        // on the holders' next request instead of letting stale claims ride for days.
        var holders = await users.GetUsersInRoleAsync(c.RoleId, ct);
        foreach (var holder in holders)
        {
            holder.BumpSecurityStamp();
            users.Update(holder);
        }

        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---- Queries ----
public interface IUserQueries
{
    Task<Application.Common.Models.PagedResult<UserDto>> SearchAsync(Application.Common.Models.PagedRequest request, CancellationToken ct);
    Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct);
    Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken ct);
}

public sealed record SearchUsersQuery(Application.Common.Models.PagedRequest Request) : IQuery<Application.Common.Models.PagedResult<UserDto>>;
public sealed class SearchUsersHandler(IUserQueries q) : IQueryHandler<SearchUsersQuery, Application.Common.Models.PagedResult<UserDto>>
{
    public Task<Application.Common.Models.PagedResult<UserDto>> Handle(SearchUsersQuery query, CancellationToken ct) => q.SearchAsync(query.Request, ct);
}

public sealed record GetUserByIdQuery(Guid UserId) : IQuery<UserDto?>;
public sealed class GetUserByIdHandler(IUserQueries q) : IQueryHandler<GetUserByIdQuery, UserDto?>
{
    public Task<UserDto?> Handle(GetUserByIdQuery query, CancellationToken ct) => q.GetByIdAsync(query.UserId, ct);
}

public sealed record GetRolesQuery : IQuery<IReadOnlyList<RoleDto>>;
public sealed class GetRolesHandler(IUserQueries q) : IQueryHandler<GetRolesQuery, IReadOnlyList<RoleDto>>
{
    public Task<IReadOnlyList<RoleDto>> Handle(GetRolesQuery query, CancellationToken ct) => q.GetRolesAsync(ct);
}

public sealed record GetPermissionsQuery : IQuery<IReadOnlyList<PermissionDto>>;
public sealed class GetPermissionsHandler(IUserQueries q) : IQueryHandler<GetPermissionsQuery, IReadOnlyList<PermissionDto>>
{
    public Task<IReadOnlyList<PermissionDto>> Handle(GetPermissionsQuery query, CancellationToken ct) => q.GetPermissionsAsync(ct);
}
