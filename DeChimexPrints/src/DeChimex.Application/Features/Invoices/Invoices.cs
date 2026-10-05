using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Invoices;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Invoices;

// ---- Create quote invoice (staff, no order) ----

public sealed record QuoteLineRequest(Guid? ProductId, Guid? ServiceId, int Quantity, string? Notes);

public sealed record CreateQuoteInvoiceRequest(
    Guid CustomerId, IReadOnlyList<QuoteLineRequest> Lines, decimal Discount, decimal Tax, string? Notes);

public sealed record CreateQuoteInvoiceCommand(CreateQuoteInvoiceRequest Request) : ICommand<InvoiceDocumentDto>;

public sealed class CreateQuoteInvoiceValidator : AbstractValidator<CreateQuoteInvoiceCommand>
{
    public CreateQuoteInvoiceValidator()
    {
        RuleFor(x => x.Request.CustomerId).NotEmpty();
        RuleFor(x => x.Request.Lines).NotEmpty().WithMessage("Add at least one product or service line.");
        RuleForEach(x => x.Request.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotNull().When(x => x.ServiceId is null)
                .WithMessage("Each line needs a product or a service.");
            l.RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
        });
        RuleFor(x => x.Request.Discount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.Tax).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.Notes).MaximumLength(2000);
    }
}

public sealed class CreateQuoteInvoiceHandler(
    ICustomerRepository customers,
    IProductRepository products,
    IServiceRepository services,
    IInvoiceRepository invoices,
    IReferenceGenerator references,
    IUnitOfWork uow) : ICommandHandler<CreateQuoteInvoiceCommand, InvoiceDocumentDto>
{
    public async Task<InvoiceDocumentDto> Handle(CreateQuoteInvoiceCommand command, CancellationToken ct)
    {
        var r = command.Request;

        var customer = await customers.GetByIdAsync(r.CustomerId, ct)
            ?? throw new NotFoundException("Customer", r.CustomerId);

        // Snapshot catalog prices into line specs (the quote never changes after creation).
        var specs = new List<InvoiceLineSpec>();
        foreach (var l in r.Lines)
        {
            if (l.ProductId is not null)
            {
                var product = await products.GetByIdAsync(l.ProductId.Value, ct)
                    ?? throw new NotFoundException("Product", l.ProductId.Value);
                specs.Add(new InvoiceLineSpec(product.Id, null, product.Name, product.SellingPrice.Amount, l.Quantity, l.Notes));
            }
            else
            {
                var service = await services.GetByIdAsync(l.ServiceId!.Value, ct)
                    ?? throw new NotFoundException("Service", l.ServiceId!.Value);
                specs.Add(new InvoiceLineSpec(null, service.Id, service.Name, service.BasePrice.Amount, l.Quantity, l.Notes));
            }
        }

        var invoice = Domain.Entities.Invoice.CreateQuote(
            await references.NextQuoteNumberAsync(ct), customer.Id, customer.FullName,
            specs, r.Discount, r.Tax, r.Notes);
        invoice.SetShareToken(references.NewShareToken());
        await invoices.AddAsync(invoice, ct);
        await uow.SaveChangesAsync(ct);

        return Map(invoice, customer, null);
    }

    internal static InvoiceDocumentDto Map(Invoice invoice, Customer? customer, string? orderNumber) => new(
        invoice.Id, invoice.InvoiceNumber, invoice.Status.ToString(), invoice.IssuedAtUtc, invoice.PaidAtUtc,
        invoice.CustomerId, invoice.CustomerName, customer?.Email?.Value, customer?.Phone?.Value,
        invoice.OrderId, orderNumber,
        invoice.Subtotal.Amount, invoice.Discount.Amount, invoice.DeliveryFee.Amount, invoice.Tax.Amount,
        invoice.GrandTotal.Amount, invoice.GrandTotal.Currency, invoice.Notes,
        invoice.OrderId is null, invoice.ShareToken,
        invoice.Lines.Select(l => new InvoiceLineDto(
            l.Id, l.ProductId, l.ServiceId, l.Name, l.UnitPrice, l.Quantity, l.Notes, l.LineTotal)).ToList());
}
