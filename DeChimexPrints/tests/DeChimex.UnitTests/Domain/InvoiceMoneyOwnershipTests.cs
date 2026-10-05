using DeChimex.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain;

/// <summary>Money is an EF owned type: an invoice must never share Money instances with its order
/// (sharing made EF warn "same entity tracked as different entity types" on every save).</summary>
public class InvoiceMoneyOwnershipTests
{
    [Fact]
    public void Issued_invoice_copies_order_totals_instead_of_sharing_instances()
    {
        var order = Order.CreateDraft("T-INV-MONEY", Guid.NewGuid());
        var invoice = Invoice.Issue("INV-T-1", order, "Test Customer");

        invoice.Tax.Should().NotBeSameAs(order.TaxAmount);
        invoice.Subtotal.Should().NotBeSameAs(order.ItemsSubtotal);
        invoice.Discount.Should().NotBeSameAs(order.DiscountAmount);
        invoice.DeliveryFee.Should().NotBeSameAs(order.DeliveryFee);
        invoice.GrandTotal.Should().NotBeSameAs(order.GrandTotal);

        invoice.Tax.Should().Be(order.TaxAmount, "the copy has the same value");
        invoice.GrandTotal.Should().Be(order.GrandTotal);
    }

    [Fact]
    public void Receipt_copies_the_payment_amount_instead_of_sharing_it()
    {
        var order = Order.CreateDraft("T-RCPT-MONEY", Guid.NewGuid());
        var payment = Payment.Initialize(order.Id, order.CustomerId, 1250m, DeChimex.Domain.Enums.PaymentMethod.Paystack, "PAY-T-RCPT-1");
        payment.MarkSucceeded("gw-T-1", "{}");
        var receipt = Receipt.CreateForPayment("RCT-T-1", order, payment, "Test Customer");

        receipt.Amount.Should().NotBeSameAs(payment.Amount);
        receipt.Amount.Should().Be(payment.Amount);
    }
}
