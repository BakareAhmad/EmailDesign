using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);

        b.OwnsOne(x => x.Email, e =>
        {
            e.Property(p => p.Value).HasColumnName("Email").HasMaxLength(256).IsRequired();
            e.HasIndex(p => p.Value).IsUnique().HasFilter("[IsDeleted] = 0");
        });

        // Staff username (null for customers): unique backstop behind the
        // application-level check (case-insensitive by construction — values are
        // normalised to lower-case before they ever reach the database).
        b.OwnsOne(x => x.Username, u =>
        {
            u.Property(p => p.Value).HasColumnName("Username").HasMaxLength(30);
            u.HasIndex(p => p.Value).IsUnique().HasFilter("[Username] IS NOT NULL AND [IsDeleted] = 0");
        });

        b.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        b.Property(x => x.FirstName).HasMaxLength(80).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(80).IsRequired();
        // Authority version (AUD-006): regenerated on activation/role/password changes;
        // the session-freshness middleware compares it to the cookie claim per request.
        b.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();
        b.Property(x => x.EmailVerificationToken).HasMaxLength(256);
        b.Property(x => x.EmailVerificationAttempts); // brute-force cap for the 6-digit OTP (int, non-null, default 0)
        b.Property(x => x.PasswordResetToken).HasMaxLength(256);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.ModifiedBy).HasMaxLength(256);
        b.Property(x => x.DeletedBy).HasMaxLength(256);

        b.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("Roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.NormalizedName).HasMaxLength(80).IsRequired();
        b.HasIndex(x => x.NormalizedName).IsUnique().HasFilter("[IsDeleted] = 0");
        b.Property(x => x.Description).HasMaxLength(256);

        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("Permissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(256);
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("RolePermissions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique();
        b.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("UserRoles");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

// RefreshTokenConfiguration removed (2026-09-01): the RefreshToken entity/table was dropped
// as dormant. Auth is cookie-only; no refresh tokens are issued or stored.
