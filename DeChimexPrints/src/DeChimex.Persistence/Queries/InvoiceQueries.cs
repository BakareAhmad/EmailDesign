using System.Linq.Expressions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Contracts.Invoices;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

/// <summary>Read side for invoice/receipt documents (admin views + public share links).</summary>
public sealed class InvoiceQueries(DeChimexDbContext db) : IInvoiceQueries
{
    public async Task<InvoiceDocumentDto?> GetDocumentByIdAsync(Guid id, CancellationToken ct)
        => await GetDocumentAsync(i => i.Id == id, ct);

    public async Task<InvoiceDocumentDto?> GetDocumentByOrderIdAsync(Guid orderId, CancellationToken ct)
        => await GetDocumentAsync(i => i.OrderId == orderId, ct);

    public async Task<InvoiceDocumentDto?> GetDocumentByShareTokenAsync(string shareToken, CancellationToken ct)
        => await GetDocumentAsync(i => i.ShareToken == shareToken, ct);

    private async Task<InvoiceDocumentDto?> GetDocumentAsync(Expression<Func<Invoice, bool>> predicate, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(predicate, ct);
        if (invoice is null) return null;

        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == invoice.CustomerId, ct);
        var orderNumber = invoice.OrderId is null
            ? null
            : await db.Orders.AsNoTracking().Where(o => o.Id == invoice.OrderId).Select(o => o.OrderNumber).FirstOrDefaultAsync(ct);

        return Map(invoice, customer, orderNumber);
    }

    public async Task<ReceiptDocumentDto?> GetReceiptByOrderIdAsync(Guid orderId, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == orderId, ct);
        return receipt is null ? null : Map(receipt);
    }

    public async Task<ReceiptDocumentDto?> GetReceiptByIdAsync(Guid id, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return receipt is null ? null : Map(receipt);
    }

    public async Task<ReceiptDocumentDto?> GetReceiptByShareTokenAsync(string shareToken, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.ShareToken == shareToken, ct);
        return receipt is null ? null : Map(receipt);
    }

    private static InvoiceDocumentDto Map(Invoice invoice, Customer? customer, string? orderNumber) => new(
        invoice.Id, invoice.InvoiceNumber, invoice.Status.ToString(), invoice.IssuedAtUtc, invoice.PaidAtUtc,
        invoice.CustomerId, invoice.CustomerName, customer?.Email?.Value, customer?.Phone?.Value,
        invoice.OrderId, orderNumber,
        invoice.Subtotal.Amount, invoice.Discount.Amount, invoice.DeliveryFee.Amount, invoice.Tax.Amount,
        invoice.GrandTotal.Amount, invoice.GrandTotal.Currency, invoice.Notes,
        invoice.OrderId is null, invoice.ShareToken,
        invoice.Lines.OrderBy(l => l.Sequence).Select(l => new InvoiceLineDto(
            l.Id, l.ProductId, l.ServiceId, l.Name, l.UnitPrice, l.Quantity, l.Notes, l.LineTotal)).ToList());

    private static ReceiptDocumentDto Map(Receipt receipt) => new(
        receipt.Id, receipt.ReceiptNumber, receipt.OrderId, receipt.OrderNumber,
        receipt.CustomerId, receipt.CustomerName,
        receipt.Amount.Amount, receipt.Amount.Currency, receipt.Method.ToString(), receipt.PaymentReference,
        receipt.PaidAtUtc, receipt.IsRefunded, receipt.ShareToken,
        receipt.OrderTotal.Amount, receipt.ServiceFee.Amount);
}
