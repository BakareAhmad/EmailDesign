using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

/// <summary>
/// EF mappings for the custom-order workflow entities (category ordering options,
/// order-request responses, files, design proofs, chat messages, timeline and
/// ad-hoc charge lines). See CUSTOM_ORDER_FLOW_PLAN.md §9.
/// </summary>

public sealed class CategoryOptionConfiguration : IEntityTypeConfiguration<CategoryOption>
{
    public void Configure(EntityTypeBuilder<CategoryOption> b)
    {
        b.ToTable("CategoryOptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Question).HasMaxLength(300).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.ShowWhenValue).HasMaxLength(200);
        b.HasIndex(x => x.CategoryId);

        // Self-referencing conditional FK: NO ACTION (Restrict). SQL Server rejects
        // ON DELETE CASCADE on a self-FK (error 1785); the option set is rebuilt
        // explicitly by Category.ReplaceOptions.
        b.HasMany(x => x.Choices).WithOne().HasForeignKey(x => x.CategoryOptionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Choices).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class CategoryOptionChoiceConfiguration : IEntityTypeConfiguration<CategoryOptionChoice>
{
    public void Configure(EntityTypeBuilder<CategoryOptionChoice> b)
    {
        b.ToTable("CategoryOptionChoices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Label).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.CategoryOptionId);
    }
}

public sealed class OrderRequirementResponseConfiguration : IEntityTypeConfiguration<OrderRequirementResponse>
{
    public void Configure(EntityTypeBuilder<OrderRequirementResponse> b)
    {
        b.ToTable("OrderRequirementResponses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Question).HasMaxLength(300).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Value).HasColumnType("nvarchar(max)").IsRequired();
        b.HasIndex(x => x.OrderId);
    }
}

public sealed class OrderFileConfiguration : IEntityTypeConfiguration<OrderFile>
{
    public void Configure(EntityTypeBuilder<OrderFile> b)
    {
        b.ToTable("OrderFiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.StoredFileName).HasMaxLength(500).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(500);
        b.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(120);
        b.Property(x => x.Purpose).HasMaxLength(120);
        b.Property(x => x.UploadedByRole).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.OrderId);
    }
}

public sealed class OrderDesignProofConfiguration : IEntityTypeConfiguration<OrderDesignProof>
{
    public void Configure(EntityTypeBuilder<OrderDesignProof> b)
    {
        b.ToTable("OrderDesignProofs");
        b.HasKey(x => x.Id);
        b.Property(x => x.StoredFileName).HasMaxLength(500).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(500);
        b.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(120);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.StaffNotes).HasMaxLength(1000);
        b.Property(x => x.CustomerFeedback).HasMaxLength(2000);
        b.HasIndex(x => x.OrderId);
    }
}

public sealed class OrderDesignReviewConfiguration : IEntityTypeConfiguration<OrderDesignReview>
{
    public void Configure(EntityTypeBuilder<OrderDesignReview> b)
    {
        b.ToTable("OrderDesignReviews");
        b.HasKey(x => x.Id);
        
        // File metadata
        b.Property(x => x.StoredFileName).HasMaxLength(500).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(500).IsRequired();
        b.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
        b.Property(x => x.SizeBytes).IsRequired();
        
        // Preview metadata (nullable for non-previewable files)
        b.Property(x => x.PreviewUrl).HasMaxLength(1000);
        b.Property(x => x.PreviewStoredFileName).HasMaxLength(500);
        
        // Submission metadata
        b.Property(x => x.UploadedByUserId).IsRequired();
        b.Property(x => x.UploadedByRole).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(2000);
        
        // Approval workflow
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.RejectionReason).HasMaxLength(2000);
        b.Property(x => x.ApprovedByUserId);
        b.Property(x => x.ApprovedAtUtc);
        b.Property(x => x.RejectedByUserId);
        b.Property(x => x.RejectedAtUtc);
        b.Property(x => x.CreatedDateUtc).IsRequired();
        
        b.HasIndex(x => x.OrderId);
    }
}

public sealed class OrderMessageConfiguration : IEntityTypeConfiguration<OrderMessage>
{
    public void Configure(EntityTypeBuilder<OrderMessage> b)
    {
        b.ToTable("OrderMessages");
        b.HasKey(x => x.Id);
        b.Property(x => x.AuthorRole).HasMaxLength(20).IsRequired();
        b.Property(x => x.AuthorName).HasMaxLength(160).IsRequired();
        // Body is optional since media-only messages carry no text.
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        b.HasIndex(x => x.OrderId);
        // ReplyToMessageId stays a scalar (no FK): the quoted message may be
        // unavailable at render time and the UI degrades gracefully.
        b.HasMany(x => x.Attachments).WithOne().HasForeignKey(x => x.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrderMessageAttachmentConfiguration : IEntityTypeConfiguration<OrderMessageAttachment>
{
    public void Configure(EntityTypeBuilder<OrderMessageAttachment> b)
    {
        b.ToTable("OrderMessageAttachments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(10).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired();
        b.Property(x => x.StoredFileName).HasMaxLength(500).IsRequired();
        b.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
        b.HasIndex(x => x.MessageId);
    }
}

public sealed class OrderTimelineEntryConfiguration : IEntityTypeConfiguration<OrderTimelineEntry>
{
    public void Configure(EntityTypeBuilder<OrderTimelineEntry> b)
    {
        b.ToTable("OrderTimelineEntries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasMaxLength(60).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ActorName).HasMaxLength(160);
        b.HasIndex(x => x.OrderId);
    }
}

public sealed class OrderCustomLineConfiguration : IEntityTypeConfiguration<OrderCustomLine>
{
    public void Configure(EntityTypeBuilder<OrderCustomLine> b)
    {
        b.ToTable("OrderCustomLines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Description).HasMaxLength(250).IsRequired();
        b.Ignore(x => x.LineTotal);
        b.OwnsOne(x => x.UnitPrice, m =>
        {
            m.Property(p => p.Amount).HasColumnName("UnitPrice").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("Currency").HasMaxLength(3).IsRequired();
        });
        b.HasIndex(x => x.OrderId);
    }
}
