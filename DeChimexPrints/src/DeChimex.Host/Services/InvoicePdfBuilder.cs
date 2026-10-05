using DeChimex.Contracts.Invoices;
using QuestPDF.Elements.Table;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DeChimex.Host.Services;

/// <summary>
/// Generates the downloadable PDF for a shared invoice document. Reuses the same
/// InvoiceDocumentDto and business settings as the HTML invoice, so both render the
/// same data; only the layout differs.
/// </summary>
public static class InvoicePdfBuilder
{
    private static readonly Color Primary = Color.FromHex("#4F46E5");
    private static readonly Color Ink = Color.FromHex("#1E293B");
    private static readonly Color Muted = Color.FromHex("#64748B");
    private static readonly Color Line = Color.FromHex("#E2E8F0");
    private static readonly Color HeaderBg = Color.FromHex("#F8FAFC");

    public static byte[] Build(InvoiceDocumentDto doc, Dictionary<string, string> business)
    {
        var name = Get(business, "business.name", "DeChimex");
        var motto = Get(business, "business.motto", "");
        var phone = Get(business, "business.phone", "");
        var email = Get(business, "business.email", "");
        var address = Get(business, "business.address", "");

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Ink));

                page.Header().Row(r =>
                {
                    r.RelativeItem().AlignLeft().Column(col =>
                    {
                        col.Item().Text(name).FontSize(20).Bold().FontColor(Primary);
                        if (!string.IsNullOrWhiteSpace(motto))
                            col.Item().PaddingTop(2).Text(motto).FontSize(10).FontColor(Muted);
                    });
                    r.RelativeItem().AlignRight().Column(col =>
                    {
                        if (!string.IsNullOrWhiteSpace(phone))
                            col.Item().Text(phone).FontSize(9).FontColor(Muted);
                        if (!string.IsNullOrWhiteSpace(email))
                            col.Item().Text(email).FontSize(9).FontColor(Muted);
                        if (!string.IsNullOrWhiteSpace(address))
                            col.Item().PaddingTop(2).Text(address).FontSize(9).FontColor(Muted);
                    });
                });

                page.Content().PaddingVertical(16).Column(col =>
                {
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Text(doc.IsQuote ? "QUOTATION" : "INVOICE").FontSize(16).Bold();
                        r.RelativeItem().AlignRight().Text(doc.InvoiceNumber).FontSize(12).Bold().FontColor(Primary);
                    });
                    col.Item().PaddingTop(4).Row(r =>
                    {
                        r.RelativeItem().Text($"Issued {doc.IssuedAtUtc:dd MMM yyyy}").FontSize(9).FontColor(Muted);
                        r.RelativeItem().AlignRight().Text(doc.Status).FontSize(9).FontColor(Muted);
                    });
                    if (!string.IsNullOrWhiteSpace(doc.OrderNumber))
                        col.Item().PaddingTop(2).Text($"Order reference: {doc.OrderNumber}").FontSize(9).FontColor(Muted);

                    col.Item().PaddingTop(16).Column(col2 =>
                    {
                        col2.Item().Text(doc.IsQuote ? "PREPARED FOR" : "BILLED TO").FontSize(8).SemiBold().FontColor(Muted);
                        col2.Item().PaddingTop(4).Text(doc.CustomerName).FontSize(12).Bold();
                        if (!string.IsNullOrWhiteSpace(doc.CustomerEmail))
                            col2.Item().Text(doc.CustomerEmail).FontSize(9).FontColor(Muted);
                        if (!string.IsNullOrWhiteSpace(doc.CustomerPhone))
                            col2.Item().Text(doc.CustomerPhone).FontSize(9).FontColor(Muted);
                    });

                    col.Item().PaddingTop(16).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(30);
                            c.RelativeColumn(2.65f);
                            c.ConstantColumn(50);
                            c.ConstantColumn(80);
                            c.ConstantColumn(90);
                        });

                        table.Header(header =>
                        {
                            HeaderCell(header.Cell(), "#");
                            HeaderCell(header.Cell(), "Item");
                            HeaderCell(header.Cell(), "Qty", right: true);
                            HeaderCell(header.Cell(), "Unit price", right: true);
                            HeaderCell(header.Cell(), "Amount", right: true);
                        });

                        var i = 1;
                        foreach (var l in doc.Lines)
                        {
                            table.Cell().PaddingVertical(6).BorderBottom(1).BorderColor(Line).Text(i.ToString()).FontSize(9);
                            table.Cell().PaddingVertical(6).BorderBottom(1).BorderColor(Line).Text(l.Name).FontSize(9);
                            table.Cell().PaddingVertical(6).BorderBottom(1).BorderColor(Line).AlignRight().Text(l.Quantity.ToString()).FontSize(9);
                            table.Cell().PaddingVertical(6).BorderBottom(1).BorderColor(Line).AlignRight().Text(l.UnitPrice.ToString("N2")).FontSize(9);
                            table.Cell().PaddingVertical(6).BorderBottom(1).BorderColor(Line).AlignRight().Text(l.LineTotal.ToString("N2")).FontSize(9);
                            i++;
                        }
                    });

                    col.Item().PaddingTop(12).Row(r =>
                    {
                        r.RelativeItem();
                        r.ConstantItem(220).Column(totals =>
                        {
                            TotalRow(totals, "Subtotal", doc.Subtotal.ToString("N2"));
                            if (doc.Discount > 0)
                                TotalRow(totals, "Discount", "-" + doc.Discount.ToString("N2"));
                            if (doc.DeliveryFee > 0)
                                TotalRow(totals, "Delivery", doc.DeliveryFee.ToString("N2"));
                            if (doc.Tax > 0)
                                TotalRow(totals, "Tax", doc.Tax.ToString("N2"));
                            totals.Item().PaddingTop(6).Row(g =>
                            {
                                g.RelativeItem().Text("Grand total").SemiBold().FontSize(11);
                                g.RelativeItem().AlignRight().Text($"{doc.Currency} {doc.GrandTotal:N2}").SemiBold().FontSize(11);
                            });
                        });
                    });

                    if (!string.IsNullOrWhiteSpace(doc.Notes))
                    {
                        col.Item().PaddingTop(16).Column(notes =>
                        {
                            notes.Item().Text("NOTES").FontSize(8).SemiBold().FontColor(Muted);
                            notes.Item().PaddingTop(2).Text(doc.Notes).FontSize(9).FontColor(Muted);
                        });
                    }
                });

                page.Footer().AlignCenter().Row(r =>
                {
                    r.RelativeItem().Text(name).FontSize(8).FontColor(Muted);
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void HeaderCell(ITableCellContainer cell, string text, bool right = false)
    {
        // QuestPDF consumes a container once it is styled: the styled container must
        // be the one that receives the text, never the original variable.
        var styled = cell.Background(HeaderBg)
            .Border(1)
            .BorderColor(Line)
            .PaddingVertical(6)
            .PaddingHorizontal(8);
        var aligned = right ? styled.AlignRight() : styled;
        aligned.Text(text).FontSize(8).SemiBold().FontColor(Muted);
    }

    private static void TotalRow(ColumnDescriptor totals, string label, string value)
    {
        totals.Item().Row(r =>
        {
            r.RelativeItem().Text(label).FontSize(9).FontColor(Muted);
            r.RelativeItem().AlignRight().Text(value).FontSize(9);
        });
    }

    private static string Get(Dictionary<string, string> settings, string key, string fallback)
        => settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
}
