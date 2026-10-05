using DeChimex.Domain.Entities;
using DeChimex.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("Categories");
        b.HasOne(x => x.MajorCategory).WithMany().HasForeignKey(x => x.MajorCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.MajorCategoryId);
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Slug)
            .HasConversion(v => v.Value, v => Slug.FromText(v))
            .HasColumnName("Slug").HasMaxLength(150).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique().HasFilter("[IsDeleted] = 0");
        b.HasMany(x => x.Options).WithOne().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Options).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("Products");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        b.HasIndex(x => x.Sku).IsUnique().HasFilter("[IsDeleted] = 0");
        b.Property(x => x.Description).HasMaxLength(4000);

        b.Property(x => x.Slug)
            .HasConversion(v => v.Value, v => Slug.FromText(v))
            .HasColumnName("Slug").HasMaxLength(250).IsRequired();

        b.OwnsOne(x => x.SellingPrice, m =>
        {
            m.Property(p => p.Amount).HasColumnName("SellingPrice").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("Currency").HasMaxLength(3).IsRequired();
        });

        b.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Images).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Images).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(x => x.CategoryId);
        b.HasIndex(x => x.IsFeatured);
        b.HasIndex(x => new { x.CategoryId, x.IsActive }); // MED-003: active products within a category
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> b)
    {
        b.ToTable("ProductImages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        b.Property(x => x.AltText).HasMaxLength(250);
        b.HasIndex(x => x.ProductId);
    }
}

public sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> b)
    {
        b.ToTable("Services");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.IconColor).HasMaxLength(120);
        b.Property(x => x.Slug)
            .HasConversion(v => v.Value, v => Slug.FromText(v))
            .HasColumnName("Slug").HasMaxLength(250).IsRequired();
        b.OwnsOne(x => x.BasePrice, m =>
        {
            m.Property(p => p.Amount).HasColumnName("BasePrice").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("Currency").HasMaxLength(3).IsRequired();
        });
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class GalleryMediaConfiguration : IEntityTypeConfiguration<GalleryMedia>
{
    public void Configure(EntityTypeBuilder<GalleryMedia> b)
    {
        b.ToTable("GalleryMedia");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ThumbnailUrl).HasMaxLength(1000);
        b.Property(x => x.Category).HasMaxLength(120);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.HasQueryFilter(x => !x.IsDeleted);

        // Carousel posts: a post row (ParentId null) owns its extra images as child rows.
        // NO ACTION (Restrict) on the self-referencing FK: SQL Server rejects ON DELETE
        // CASCADE here (error 1785, cascade cycle / multiple cascade paths). Child rows
        // are therefore deleted explicitly by the handlers (DeleteGalleryMediaHandler,
        // SetGalleryImagesCommand) instead of by the database cascade.
        b.HasMany(x => x.Images).WithOne(x => x.Parent)
            .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ParentId);
    }
}
