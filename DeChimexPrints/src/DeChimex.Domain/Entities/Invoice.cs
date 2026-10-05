using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Invoice document. An invoice is issued BEFORE payment: every placed order gets one
/// immediately (status Issued/DUE), and staff can also create quote invoices with no
/// order at all (status Draft) for off-platform business; those are later converted
/// into real orders, which adopt the quote invoice instead of getting a second one.
/// After payment the invoice is marked Paid and a separate Receipt is generated.
/// </summary>
public sealed class Invoice : AggregateRoot
{
    private readonly List<InvoiceLine> _lines = new();

    private Invoice() { } // EF

    private Invoice(string invoiceNumber, Guid customerId, string customerName, Money subtotal,
        Money discount, Money deliveryFee, Money tax, Money grandTotal, InvoiceStatus status, string? notes)
    {
        InvoiceNumber = invoiceNumber;
        CustomerId = customerId;
        CustomerName = customerName;
        Subtotal = subtotal;
        Discount = discount;
        DeliveryFee = deliveryFee;
        Tax = tax;
        GrandTotal = grandTotal;
        Status = status;
        Notes = notes;
        IssuedAtUtc = DateTime.UtcNow;
    }

    public string InvoiceNumber { get; private set; } = default!;
    /// <summary>Persistence-only escape hatch (AUD-004): when the unique reference index
    /// rejects a count-generated number under concurrency, the save boundary renumbers the
    /// conflicting document with a fresh random suffix and retries. Never call during normal
    /// business flow.</summary>
    public void RenumberAfterReferenceConflict(string newNumber) => InvoiceNumber = newNumber;

    public Guid? OrderId { get; private set; }
    public Guid CustomerId { get; private set; }

    /// <summary>Customer name snapshot so the document stays correct if the record is renamed.</summary>
    public string CustomerName { get; private set; } = default!;
    public Money Subtotal { get; private set; } = default!;
    public Money Discount { get; private set; } = default!;
    public Money DeliveryFee { get; private set; } = default!;
    public Money Tax { get; private set; } = default!;
    public Money GrandTotal { get; private set; } = default!;
    public InvoiceStatus Status { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }
    public DateTime? PaidAtUtc { get; private set; }
    public string? DocumentUrl { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>Opaque token for the public share/download link (/i/{token}).</summary>
    public string ShareToken { get; private set; } = default!;

    public IReadOnlyCollection<InvoiceLine> Lines => _lines.AsReadOnly();

    /// <summary>True once this quote invoice was converted into (adopted by) a real order.</summary>
    public bool HasOrder => OrderId is not null;

    public static Invoice Issue(string invoiceNumber, Order order, string customerName, string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new BusinessRuleValidationException("invoice.number", "Invoice number is required.");
        if (order.CustomerId == Guid.Empty)
            throw new BusinessRuleValidationException("invoice.customer", "Invoice customer is required.");

        var invoice = new Invoice(invoiceNumber, order.CustomerId, customerName,
            order.ItemsSubtotal.Copy(), order.DiscountAmount.Copy(), order.DeliveryFee.Copy(), order.TaxAmount.Copy(),
            order.GrandTotal.Copy(), InvoiceStatus.Issued, notes);
        invoice.OrderId = order.Id;
        invoice.ReplaceLines(BuildLinesFromOrder(invoice.Id, order));
        return invoice;
    }

    /// <summary>Staff-created quote invoice: line items only, no order yet (status Draft).</summary>
    public static Invoice CreateQuote(string invoiceNumber, Guid customerId, string customerName,
        IReadOnlyList<InvoiceLineSpec> specs, decimal discount, decimal tax, string? notes)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new BusinessRuleValidationException("invoice.number", "Invoice number is required.");
        if (customerId == Guid.Empty)
            throw new BusinessRuleValidationException("invoice.customer", "Invoice customer is required.");
        if (specs.Count == 0)
            throw new BusinessRuleValidationException("invoice.lines", "A quote invoice needs at least one line.");
        if (discount < 0 || tax < 0)
            throw new BusinessRuleValidationException("invoice.pricing", "Discount and tax cannot be negative.");

        var invoiceId = Guid.NewGuid();
        var lines = specs.Select(sp => InvoiceLine.Create(invoiceId, sp.ProductId, sp.ServiceId,
            sp.Name, sp.UnitPrice, sp.Quantity, sp.Notes)).ToList();
        var subtotal = lines.Sum(l => l.LineTotal);
        var grandTotal = subtotal + tax - discount;
        if (grandTotal < 0)
            throw new BusinessRuleValidationException("invoice.pricing", "The grand total cannot be negative.");

        var invoice = new Invoice(invoiceNumber, customerId, customerName,
            Money.Of(subtotal), Money.Of(discount), Money.Zero(), Money.Of(tax),
            Money.Of(grandTotal), InvoiceStatus.Draft, notes);
        invoice.ReplaceLines(lines);
        return invoice;
    }

    /// <summary>
    /// Converts this draft quote into the invoice of a real order: the order adopts this
    /// document (number, lines, totals refreshed from the order's own snapshot pricing).
    /// </summary>
    public void AdoptForOrder(Order order)
    {
        if (OrderId is not null)
            throw new BusinessRuleValidationException("invoice.converted", "This invoice already has an order.");
        if (Status != InvoiceStatus.Draft)
            throw new BusinessRuleValidationException("invoice.state", "Only draft quote invoices can be converted.");

        OrderId = order.Id;
        Status = InvoiceStatus.Issued;
        Subtotal = order.ItemsSubtotal.Copy();
        Discount = order.DiscountAmount.Copy();
        DeliveryFee = order.DeliveryFee.Copy();
        Tax = order.TaxAmount.Copy();
        GrandTotal = order.GrandTotal.Copy();
        if (order.CustomerId != CustomerId)
        {
            CustomerId = order.CustomerId;
            // The customer name snapshot is refreshed by the caller (it loads the customer).
        }
        ReplaceLines(BuildLinesFromOrder(Id, order));
    }

    public void RefreshCustomerName(string customerName) => CustomerName = customerName;


    public void MarkPaid()
    {
        Status = InvoiceStatus.Paid;
        PaidAtUtc = DateTime.UtcNow;
    }

    public void MarkRefunded() => Status = InvoiceStatus.Refunded;


    public void SetShareToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new BusinessRuleValidationException("invoice.share", "A share token is required.");
        ShareToken = token;
    }


    public void ReplaceLines(IEnumerable<InvoiceLine> lines)
    {
        _lines.Clear();
        var index = 0;
        foreach (var line in lines)
            _lines.Add(line.WithSequence(index++));
    }

    private static List<InvoiceLine> BuildLinesFromOrder(Guid invoiceId, Order order)
    {
        var lines = new List<InvoiceLine>();
        foreach (var item in order.Items)
            lines.Add(InvoiceLine.Create(invoiceId, item.ProductId, null, item.ProductName, item.UnitPrice.Amount, item.Quantity));
        foreach (var svc in order.Services)
            lines.Add(InvoiceLine.Create(invoiceId, null, svc.ServiceId, svc.ServiceName, svc.UnitPrice.Amount, svc.Quantity, svc.Notes));
        // Ad-hoc custom-order charge lines: the description is the line identity.
        foreach (var custom in order.CustomLines)
            lines.Add(InvoiceLine.CreateCustom(invoiceId, custom.Description, custom.UnitPrice.Amount, custom.Quantity));
        return lines;
    }
}
