using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>Catalog-neutral description of an invoice line, before the invoice id exists.</summary>
public sealed record InvoiceLineSpec(Guid? ProductId, Guid? ServiceId, string Name, decimal UnitPrice, int Quantity, string? Notes);

/// <summary>
/// One line on an invoice document. Snapshotted from the catalog (products) or
/// the service catalogue at issue time, so the document never changes after creation.
/// Quote invoices carry their lines here even though no order exists yet.
/// </summary>
public sealed class InvoiceLine : Entity
{
    private InvoiceLine() { } // EF

    private InvoiceLine(Guid invoiceId, Guid? productId, Guid? serviceId, string name,
        decimal unitPrice, int quantity, string? notes, int sequence)
    {
        InvoiceId = invoiceId;
        ProductId = productId;
        ServiceId = serviceId;
        Name = name;
        UnitPrice = unitPrice;
        Quantity = quantity;
        Notes = notes;
        Sequence = sequence;
    }

    public Guid InvoiceId { get; private set; }
    public Guid? ProductId { get; private set; }
    public Guid? ServiceId { get; private set; }
    public string Name { get; private set; } = default!;
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>Display order on the document (0-based).</summary>
    public int Sequence { get; private set; }

    public decimal LineTotal => UnitPrice * Quantity;

    public static InvoiceLine Create(Guid invoiceId, Guid? productId, Guid? serviceId, string name,
        decimal unitPrice, int quantity, string? notes = null, int sequence = 0)
    {
        if (invoiceId == Guid.Empty)
            throw new BusinessRuleValidationException("invoice.line.invoice", "An invoice id is required.");
        if (productId is null && serviceId is null)
            throw new BusinessRuleValidationException("invoice.line.item", "A line must reference a product or a service.");
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("invoice.line.name", "Line name is required.");
        if (quantity < 1)
            throw new BusinessRuleValidationException("invoice.line.qty", "Line quantity must be at least 1.");
        if (unitPrice < 0)
            throw new BusinessRuleValidationException("invoice.line.price", "Line unit price cannot be negative.");

        return new InvoiceLine(invoiceId, productId, serviceId, name.Trim(), unitPrice, quantity,
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), sequence);
    }

    /// <summary>Creates this line re-sequenced (used when an invoice rebuilds its lines).</summary>
    public InvoiceLine WithSequence(int sequence)
    {
        if (sequence == Sequence) return this;
        // Ad-hoc (custom-order) lines carry no product/service reference and must keep that
        // shape through re-sequencing; catalog lines use the standard factory.
        return ProductId is null && ServiceId is null
            ? CreateCustom(InvoiceId, Name, UnitPrice, Quantity, sequence)
            : Create(InvoiceId, ProductId, ServiceId, Name, UnitPrice, Quantity, Notes, sequence);
    }

    /// <summary>
    /// Creates an ad-hoc invoice line (custom-order charge lines such as "Design
    /// preparation" or "Customization × 10"). Unlike catalog lines these carry no
    /// product/service reference — the description IS the line identity — so this
    /// factory exists separately instead of loosening the catalog-line invariant.
    /// </summary>
    public static InvoiceLine CreateCustom(Guid invoiceId, string name, decimal unitPrice,
        int quantity, int sequence = 0)
    {
        if (invoiceId == Guid.Empty)
            throw new BusinessRuleValidationException("invoice.line.invoice", "An invoice id is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("invoice.line.name", "Line name is required.");
        if (quantity < 1)
            throw new BusinessRuleValidationException("invoice.line.qty", "Line quantity must be at least 1.");
        if (unitPrice < 0)
            throw new BusinessRuleValidationException("invoice.line.price", "Line unit price cannot be negative.");
        return new InvoiceLine(invoiceId, null, null, name.Trim(), unitPrice, quantity, null, sequence);
    }

}
