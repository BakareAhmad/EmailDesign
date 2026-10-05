using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace DeChimex.Persistence.Configurations;
public sealed class MajorCategoryConfiguration : IEntityTypeConfiguration<MajorCategory>
{
    public void Configure(EntityTypeBuilder<MajorCategory> b)
    {
        b.ToTable("MajorCategories"); b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.NormalizedName).HasMaxLength(120).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.HasIndex(x => x.NormalizedName).IsUnique().HasFilter("[IsDeleted] = 0");
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}
