using DeChimex.Domain.Entities;
using DeChimex.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.ToTable("Suppliers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Contact).HasMaxLength(200);
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(50);
        b.Property(x => x.Category).HasMaxLength(100);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

// NOTE: Quotation + QuotationLine configurations were removed (2026-09-01) as part of
// de-duplicating quoting onto the Invoice "quote invoice" path. The Quotations /
// QuotationLines tables are also being dropped by a migration (2026-09-01) along with
// RefreshTokens as dormant; the EF model no longer maps any of them.
