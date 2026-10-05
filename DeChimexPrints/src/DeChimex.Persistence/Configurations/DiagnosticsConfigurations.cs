using DeChimex.Domain.Entities.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

// Developer Portal diagnostics tables. Indexes are chosen for the portal's access paths:
// time-window scans (every screen filters by time first), error grouping by fingerprint,
// correlation-id lookup, and per-integration history. Retention pruning uses the time index.

public sealed class TechnicalEventConfiguration : IEntityTypeConfiguration<TechnicalEvent>
{
    public void Configure(EntityTypeBuilder<TechnicalEvent> b)
    {
        b.ToTable("TechnicalEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Severity).HasMaxLength(16).IsRequired();
        b.Property(x => x.Category).HasMaxLength(32).IsRequired();
        b.Property(x => x.EventType).HasMaxLength(64).IsRequired();
        b.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        b.Property(x => x.MessageTemplate).HasMaxLength(1000);
        b.Property(x => x.Fingerprint).HasMaxLength(40).IsRequired();
        b.Property(x => x.ExceptionType).HasMaxLength(256);
        b.Property(x => x.StackTrace).HasMaxLength(8000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.RequestId).HasMaxLength(64);
        b.Property(x => x.Route).HasMaxLength(256);
        b.Property(x => x.Integration).HasMaxLength(32);
        b.Property(x => x.Source).HasMaxLength(256);
        b.Property(x => x.Environment).HasMaxLength(32);
        b.Property(x => x.Properties).HasMaxLength(4000);

        b.HasIndex(x => x.TimestampUtc);
        b.HasIndex(x => new { x.Severity, x.TimestampUtc });
        b.HasIndex(x => new { x.Category, x.TimestampUtc });
        b.HasIndex(x => new { x.Fingerprint, x.TimestampUtc });
        b.HasIndex(x => x.CorrelationId);
    }
}

public sealed class IntegrationCallConfiguration : IEntityTypeConfiguration<IntegrationCall>
{
    public void Configure(EntityTypeBuilder<IntegrationCall> b)
    {
        b.ToTable("IntegrationCalls");
        b.HasKey(x => x.Id);
        b.Property(x => x.Integration).HasMaxLength(32).IsRequired();
        b.Property(x => x.Operation).HasMaxLength(32).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(32).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Template).HasMaxLength(64);
        b.Property(x => x.RecipientCategory).HasMaxLength(32);
        b.Property(x => x.RecipientMasked).HasMaxLength(256);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);

        b.HasIndex(x => new { x.Integration, x.TimestampUtc });
        b.HasIndex(x => new { x.Integration, x.Operation, x.TimestampUtc });
        b.HasIndex(x => x.Reference);
    }
}

public sealed class RequestMetricBucketConfiguration : IEntityTypeConfiguration<RequestMetricBucket>
{
    public void Configure(EntityTypeBuilder<RequestMetricBucket> b)
    {
        b.ToTable("RequestMetricBuckets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Method).HasMaxLength(10).IsRequired();
        b.Property(x => x.Route).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.BucketStartUtc, x.Method, x.Route }).IsUnique();
    }
}
