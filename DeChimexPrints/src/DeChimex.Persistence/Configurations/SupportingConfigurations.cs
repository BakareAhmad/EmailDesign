using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

public sealed class ProcurementConfiguration : IEntityTypeConfiguration<Procurement>
{
    public void Configure(EntityTypeBuilder<Procurement> b)
    {
        b.ToTable("Procurements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Reference).IsUnique();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.ApprovedBy).HasMaxLength(256);
        b.Property(x => x.SupplierName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ProcurementId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(x => x.Status);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class ProcurementItemConfiguration : IEntityTypeConfiguration<ProcurementItem>
{
    public void Configure(EntityTypeBuilder<ProcurementItem> b)
    {
        b.ToTable("ProcurementItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
        b.OwnsOne(x => x.UnitCost, m =>
        {
            m.Property(p => p.Amount).HasColumnName("UnitCost").HasColumnType("decimal(18,2)");
            m.Property(p => p.Currency).HasColumnName("Currency").HasMaxLength(3);
        });
        b.HasIndex(x => x.ProcurementId);
    }
}

public sealed class ShoppingCartConfiguration : IEntityTypeConfiguration<ShoppingCart>
{
    public void Configure(EntityTypeBuilder<ShoppingCart> b)
    {
        b.ToTable("ShoppingCarts");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.CustomerId).IsUnique();
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.CartId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> b)
    {
        b.ToTable("CartItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.LineType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasIndex(x => x.CartId);
    }
}

public sealed class WishlistItemConfiguration : IEntityTypeConfiguration<WishlistItem>
{
    public void Configure(EntityTypeBuilder<WishlistItem> b)
    {
        b.ToTable("WishlistItems");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.CustomerId, x.ProductId }).IsUnique();
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> b)
    {
        b.ToTable("Reviews");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Comment).HasMaxLength(2000);
        b.HasIndex(x => x.ProductId);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        b.Property(x => x.Link).HasMaxLength(500);
        b.Property(x => x.RequiredPermission).HasMaxLength(60);
        b.HasIndex(x => x.RecipientUserId);
        b.HasIndex(x => x.IsRead);
        b.HasIndex(x => x.RequiredPermission);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.Property(x => x.ActivityType).HasMaxLength(80);
        b.Property(x => x.Category).HasMaxLength(32);
        b.Property(x => x.Title).HasMaxLength(160);
        b.Property(x => x.Summary).HasMaxLength(800);
        b.Property(x => x.ActorName).HasMaxLength(120);
        b.Property(x => x.ActorRole).HasMaxLength(32);
        b.Property(x => x.Importance).HasMaxLength(16);
        b.Property(x => x.SourceKey).HasMaxLength(180);
        b.HasIndex(x => x.SourceKey).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
        b.HasIndex(x => new { x.Category, x.TimestampUtc, x.Id });
        b.HasIndex(x => new { x.IsRealtime, x.RealtimeDispatchedAtUtc, x.TimestampUtc });
        b.HasIndex(x => new { x.ActorUserId, x.TimestampUtc });
        b.HasIndex(x => new { x.OrderId, x.TimestampUtc });
        b.HasIndex(x => new { x.CustomerId, x.TimestampUtc });
        b.HasIndex(x => new { x.ProductId, x.TimestampUtc });
        b.HasIndex(x => new { x.StaffUserId, x.TimestampUtc });
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(120).IsRequired();
        b.Property(x => x.EntityName).HasMaxLength(120).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.PerformedBy).HasMaxLength(256);
        b.Property(x => x.OldValues).HasColumnType("nvarchar(max)");
        b.Property(x => x.NewValues).HasColumnType("nvarchar(max)");
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.HasIndex(x => x.TimestampUtc);
        b.HasIndex(x => x.EntityName);
    }
}

public sealed class ApplicationSettingConfiguration : IEntityTypeConfiguration<ApplicationSetting>
{
    public void Configure(EntityTypeBuilder<ApplicationSetting> b)
    {
        b.ToTable("ApplicationSettings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(120).IsRequired();
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.Value).HasMaxLength(2000).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class ContactMessageConfiguration : IEntityTypeConfiguration<ContactMessage>
{
    public void Configure(EntityTypeBuilder<ContactMessage> b)
    {
        b.ToTable("ContactMessages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Email).HasMaxLength(200).IsRequired();
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.Service).HasMaxLength(120);
        b.Property(x => x.Details).HasMaxLength(4000).IsRequired();
        b.HasIndex(x => x.CreatedUtc);
        b.HasIndex(x => x.IsResolved);
        b.HasMany(x => x.Replies).WithOne().HasForeignKey(x => x.ContactMessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ContactMessageReplyConfiguration : IEntityTypeConfiguration<ContactMessageReply>
{
    public void Configure(EntityTypeBuilder<ContactMessageReply> b)
    {
        b.ToTable("ContactMessageReplies");
        b.HasKey(x => x.Id);
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        b.Property(x => x.SentByName).HasMaxLength(120);
        b.HasIndex(x => x.ContactMessageId);
    }
}
