using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

public sealed class MarketingBroadcastConfiguration : IEntityTypeConfiguration<MarketingBroadcast>
{
    public void Configure(EntityTypeBuilder<MarketingBroadcast> b)
    {
        b.ToTable("MarketingBroadcasts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.HtmlContent).IsRequired();
        b.Property(x => x.PlainTextContent);
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.CreatedBy).HasMaxLength(256);

        b.HasMany(x => x.Recipients).WithOne()
            .HasForeignKey(r => r.BroadcastId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MarketingBroadcastRecipientConfiguration : IEntityTypeConfiguration<MarketingBroadcastRecipient>
{
    public void Configure(EntityTypeBuilder<MarketingBroadcastRecipient> b)
    {
        b.ToTable("MarketingBroadcastRecipients");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasMaxLength(256).IsRequired();
        b.Property(x => x.FirstName).HasMaxLength(80);
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.FailureReason).HasMaxLength(500);

        // At-most-once delivery: a customer can appear on a broadcast exactly once.
        b.HasIndex(x => new { x.BroadcastId, x.CustomerId }).IsUnique();
        b.HasIndex(x => x.Status);
    }
}
