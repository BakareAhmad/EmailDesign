using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

public sealed class InventoryConfiguration : IEntityTypeConfiguration<Inventory>
{
    public void Configure(EntityTypeBuilder<Inventory> b)
    {
        b.ToTable("Inventories");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.ProductId).IsUnique();
        b.Ignore(x => x.Available);
        b.Ignore(x => x.IsLowStock);
        b.Ignore(x => x.IsOutOfStock);

        b.HasMany(x => x.Transactions).WithOne().HasForeignKey(x => x.InventoryId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Transactions).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class InventoryTransactionConfiguration : IEntityTypeConfiguration<InventoryTransaction>
{
    public void Configure(EntityTypeBuilder<InventoryTransaction> b)
    {
        b.ToTable("InventoryTransactions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Reference).HasMaxLength(120);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.PerformedBy).HasMaxLength(256);
        b.HasIndex(x => x.ProductId);
        b.HasIndex(x => x.OccurredOnUtc);
    }
}
