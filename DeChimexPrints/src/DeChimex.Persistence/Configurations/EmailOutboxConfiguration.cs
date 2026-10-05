using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

/// <summary>
/// Durable email outbox (AUD-003): transactional emails are persisted rows drained by the
/// OutboxEmailProcessor, so a restart can no longer lose queued mail. Pending rows are
/// indexed by (Status, NextAttemptAtUtc) — exactly the processor's polling query.
/// </summary>
public sealed class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutbox>
{
    public void Configure(EntityTypeBuilder<EmailOutbox> b)
    {
        b.ToTable("EmailOutbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.ToEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.ToName).HasMaxLength(160).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(500).IsRequired();
        b.Property(x => x.HtmlBody).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.PlainTextBody).HasColumnType("nvarchar(max)");
        b.Property(x => x.Template).HasMaxLength(100);
        b.Property(x => x.Category).HasMaxLength(50);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
    }
}
