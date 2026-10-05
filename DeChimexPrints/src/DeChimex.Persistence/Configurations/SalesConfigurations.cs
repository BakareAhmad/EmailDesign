using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("Customers");
        b.HasKey(x => x.Id);
        b.Property(x => x.FirstName).HasMaxLength(80).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(80).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Ignore(x => x.FullName);

        // Marketing consent + per-customer unsubscribe token (marketing broadcasts).
        b.Property(x => x.MarketingSubscribed).IsRequired().HasDefaultValue(true);
        b.HasIndex(x => x.UnsubscribeToken).IsUnique();

        b.OwnsOne(x => x.Email, e =>
        {
            // Optional: walk-in guests are created without an email address.
            e.Property(p => p.Value).HasColumnName("Email").HasMaxLength(256);
            e.HasIndex(p => p.Value);
        });
        b.OwnsOne(x => x.Phone, p =>
            p.Property(v => v.Value).HasColumnName("Phone").HasMaxLength(20));
        b.OwnsOne(x => x.LifetimeSpending, m =>
        {
            m.Property(p => p.Amount).HasColumnName("LifetimeSpending").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("SpendingCurrency").HasMaxLength(3).IsRequired();
        });

        b.HasMany(x => x.Addresses).WithOne().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Addresses).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(x => x.UserId);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> b)
    {
        b.ToTable("CustomerAddresses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Label).HasMaxLength(60).IsRequired();
        b.OwnsOne(x => x.Address, a =>
        {
            a.Property(p => p.Street).HasColumnName("Street").HasMaxLength(250).IsRequired();
            a.Property(p => p.City).HasColumnName("City").HasMaxLength(120).IsRequired();
            a.Property(p => p.State).HasColumnName("State").HasMaxLength(120).IsRequired();
            a.Property(p => p.Country).HasColumnName("Country").HasMaxLength(120).IsRequired();
            a.Property(p => p.PostalCode).HasColumnName("PostalCode").HasMaxLength(20);
        });
        b.HasIndex(x => x.CustomerId);
    }
}

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("Orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.OrderNumber).IsUnique();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.CustomerNotes).HasMaxLength(2000);
        // AUD-002: database-generated concurrency token — payment confirmation and other
        // lifecycle writes fail loudly instead of silently interleaving.
        b.Property(x => x.RowVersion).IsRowVersion();

        // Sales-assignment workflow: per-order owner + communication state.
        b.Property(x => x.AssignedSalesName).HasMaxLength(200);
        b.Property(x => x.ConversationState).HasConversion<string>().HasMaxLength(30)
            .HasDefaultValue(OrderConversationState.Active);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(OrderKind.Custom)
            // 'Direct' is the CLR default; without an explicit sentinel EF omits the
            // column on insert and the database default (Custom) would silently win —
            // direct orders must never be recorded as custom.
            .HasSentinel((OrderKind)255);
        b.Ignore(x => x.Type);
        b.Ignore(x => x.ItemsSubtotal);
        b.Ignore(x => x.GrandTotal);

        MoneyOwned(b, x => x.DiscountAmount, "DiscountAmount", "DiscountCurrency");
        MoneyOwned(b, x => x.DeliveryFee, "DeliveryFee", "DeliveryCurrency");
        MoneyOwned(b, x => x.TaxAmount, "TaxAmount", "TaxCurrency");

        b.OwnsOne(x => x.ShippingAddress, a => MapAddress(a, "Shipping"));
        b.OwnsOne(x => x.BillingAddress, a => MapAddress(a, "Billing"));

        b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Services).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.Services).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Custom-order workflow children (all cascade-deleted with the order).
        b.HasMany(x => x.Responses).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Files).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Timeline).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Proofs).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.CustomLines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Responses).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.Files).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.Timeline).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.Proofs).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.CustomLines).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(x => x.CustomerId);
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.AssignedSalesUserId);
        b.HasIndex(x => x.CreatedDateUtc);
        b.HasIndex(x => new { x.Status, x.CreatedDateUtc });    // MED-003: status board ordered by date
        b.HasIndex(x => new { x.CustomerId, x.CreatedDateUtc }); // MED-003: per-customer order history
        b.HasQueryFilter(x => !x.IsDeleted);
    }

    private static void MoneyOwned(EntityTypeBuilder<Order> b,
        System.Linq.Expressions.Expression<Func<Order, DeChimex.Domain.ValueObjects.Money?>> nav, string amount, string currency)
    {
        b.OwnsOne(nav, m =>
        {
            m.Property(p => p.Amount).HasColumnName(amount).HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName(currency).HasMaxLength(3).IsRequired();
        });
    }

    private static void MapAddress(Microsoft.EntityFrameworkCore.Metadata.Builders.OwnedNavigationBuilder<Order, DeChimex.Domain.ValueObjects.Address> a, string prefix)
    {
        a.Property(p => p.Street).HasColumnName($"{prefix}Street").HasMaxLength(250);
        a.Property(p => p.City).HasColumnName($"{prefix}City").HasMaxLength(120);
        a.Property(p => p.State).HasColumnName($"{prefix}State").HasMaxLength(120);
        a.Property(p => p.Country).HasColumnName($"{prefix}Country").HasMaxLength(120);
        a.Property(p => p.PostalCode).HasColumnName($"{prefix}PostalCode").HasMaxLength(20);
    }
}

public sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> b)
    {
        b.ToTable("OrderItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        b.Ignore(x => x.LineTotal);
        b.OwnsOne(x => x.UnitPrice, m =>
        {
            m.Property(p => p.Amount).HasColumnName("UnitPrice").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("Currency").HasMaxLength(3).IsRequired();
        });
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.ProductId);
    }
}

public sealed class OrderServiceLineConfiguration : IEntityTypeConfiguration<OrderServiceLine>
{
    public void Configure(EntityTypeBuilder<OrderServiceLine> b)
    {
        b.ToTable("OrderServiceLines");
        b.HasKey(x => x.Id);
        b.Property(x => x.ServiceName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Ignore(x => x.LineTotal);
        b.OwnsOne(x => x.UnitPrice, m =>
        {
            m.Property(p => p.Amount).HasColumnName("UnitPrice").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("Currency").HasMaxLength(3).IsRequired();
        });
        b.HasIndex(x => x.OrderId);
    }
}

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable("Invoices");
        b.HasKey(x => x.Id);
        b.Property(x => x.InvoiceNumber).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.InvoiceNumber).IsUnique();
        // Nullable: quote invoices exist before (or without) an order. One invoice per order stays unique.
        b.HasIndex(x => x.OrderId).IsUnique();
        b.HasIndex(x => x.ShareToken).IsUnique();
        b.HasIndex(x => x.Status);         // MED-003: invoice status filtering
        b.HasIndex(x => x.CreatedDateUtc); // MED-003: invoice date ordering
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.DocumentUrl).HasMaxLength(1000);
        b.Property(x => x.CustomerName).HasMaxLength(160).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.ShareToken).HasMaxLength(64).IsRequired();
        b.Ignore(x => x.HasOrder);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        Own(b, x => x.Subtotal, "Subtotal");
        Own(b, x => x.Discount, "Discount");
        Own(b, x => x.DeliveryFee, "DeliveryFee");
        Own(b, x => x.Tax, "Tax");
        Own(b, x => x.GrandTotal, "GrandTotal");
        b.HasQueryFilter(x => !x.IsDeleted);
    }

    private static void Own(EntityTypeBuilder<Invoice> b,
        System.Linq.Expressions.Expression<Func<Invoice, DeChimex.Domain.ValueObjects.Money?>> nav, string name)
    {
        b.OwnsOne(nav, m =>
        {
            m.Property(p => p.Amount).HasColumnName(name).HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName($"{name}Currency").HasMaxLength(3).IsRequired();
        });
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("Payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(80).IsRequired();
        b.HasIndex(x => x.Reference).IsUnique();
        b.Property(x => x.GatewayReference).HasMaxLength(120);
        b.Property(x => x.AuthorizationUrl).HasMaxLength(1000);
        b.Property(x => x.RawResponse).HasColumnType("nvarchar(max)");
        b.Property(x => x.FailureReason).HasMaxLength(1000);
        // Money/percent scalars: explicit store types (matches AddServiceFeeSnapshot;
        // silences EF's "no store type for decimal" truncation warnings).
        b.Property(x => x.CustomerBorneCharge).HasColumnType("decimal(18,2)");
        b.Property(x => x.OrderAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.ServiceFeePercent).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaystackFeeExpected).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaystackFeeActual).HasColumnType("decimal(18,2)");
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        // AUD-005: refund settlement lifecycle (Requested -> Processed | Failed).
        b.Property(x => x.RefundStatus).HasConversion<string>().HasMaxLength(20);
        // AUD-002: database-generated concurrency token for exactly-once confirmation.
        b.Property(x => x.RowVersion).IsRowVersion();
        // AUD-002 hard backstop: at most ONE succeeded payment per order (refunded rows
        // are excluded so a refund + later re-payment still works). SQL Server only —
        // the InMemory test provider ignores filters, mirroring production semantics only
        // on the real store.
        b.HasIndex(x => x.OrderId)
            .IsUnique()
            .HasFilter("[Status] = 'Succeeded'")
            .HasDatabaseName("IX_Payments_OrderId_OneSucceeded");
        b.OwnsOne(x => x.Amount, m =>
        {
            m.Property(p => p.Amount).HasColumnName("Amount").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("Currency").HasMaxLength(3).IsRequired();
        });
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.CustomerId);
        b.HasIndex(x => x.Status);         // MED-003: payment status filtering
        b.HasIndex(x => x.CreatedDateUtc); // MED-003: payment date ordering
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.ToTable("InvoiceLines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)").IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.Sequence).IsRequired();
        b.HasIndex(x => x.InvoiceId);
    }
}

public sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    public void Configure(EntityTypeBuilder<Receipt> b)
    {
        b.ToTable("Receipts");
        b.HasKey(x => x.Id);
        b.Property(x => x.ReceiptNumber).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.ReceiptNumber).IsUnique();
        b.HasIndex(x => x.ShareToken).IsUnique();
        b.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        b.Property(x => x.CustomerName).HasMaxLength(160).IsRequired();
        b.Property(x => x.PaymentReference).HasMaxLength(80).IsRequired();
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(30);
        b.OwnsOne(x => x.Amount, m =>
        {
            m.Property(p => p.Amount).HasColumnName("Amount").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("Currency").HasMaxLength(3).IsRequired();
        });
        // Service-fee snapshot columns (brief §5/§11): the document's fee split is fixed
        // at creation and never recomputed from global configuration.
        b.OwnsOne(x => x.OrderTotal, m =>
        {
            m.Property(p => p.Amount).HasColumnName("OrderTotal").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("OrderTotalCurrency").HasMaxLength(3).IsRequired();
        });
        b.OwnsOne(x => x.ServiceFee, m =>
        {
            m.Property(p => p.Amount).HasColumnName("ServiceFee").HasColumnType("decimal(18,2)").IsRequired();
            m.Property(p => p.Currency).HasColumnName("ServiceFeeCurrency").HasMaxLength(3).IsRequired();
        });
        // AUD-002 hard backstop: one receipt per order (mirrors the invoice guarantee).
        b.HasIndex(x => x.OrderId)
            .IsUnique()
            .HasDatabaseName("IX_Receipts_OrderId_Unique");
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}
