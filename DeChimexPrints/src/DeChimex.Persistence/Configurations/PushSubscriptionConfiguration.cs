using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

/// <summary>EF mapping for Web Push subscriptions (PWA push notifications).</summary>
public sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> b)
    {
        b.ToTable("PushSubscriptions");
        b.HasKey(x => x.Id);

        b.Property(x => x.UserId).IsRequired();
        b.Property(x => x.Endpoint).IsRequired().HasMaxLength(450);
        b.Property(x => x.P256dh).IsRequired().HasMaxLength(160);
        b.Property(x => x.Auth).IsRequired().HasMaxLength(80);
        b.Property(x => x.UserAgent).HasMaxLength(300);

        // One row per (user, device). Lookup pattern is by user + endpoint (subscribe upsert,
        // unsubscribe) and by endpoint alone (push-service 410 cleanup).
        // Soft-delete aware: unsubscribed rows stay for audit, so both the visibility filter
        // and the unique index must ignore them (a browser may resubscribe the same endpoint).
        b.HasQueryFilter(x => !x.IsDeleted);
        b.HasIndex(x => new { x.UserId, x.Endpoint }).IsUnique().HasFilter("[IsDeleted] = 0");
        b.HasIndex(x => x.Endpoint);
    }
}
