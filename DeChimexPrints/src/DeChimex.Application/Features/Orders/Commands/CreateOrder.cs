using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using DeChimex.Domain.ValueObjects;
using FluentValidation;

namespace DeChimex.Application.Features.Orders.Commands;

public sealed record CreateOrderCommand(
    CreateOrderRequest Request,
    Guid? QuoteInvoiceId = null,
    Domain.Enums.OrderKind Kind = Domain.Enums.OrderKind.Direct) : ICommand<OrderDto>;

public sealed class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        // Business rule (MED-001): an order needs at least one product or service line.
        RuleFor(x => x.Request).Must(r => r.Items.Count > 0 || r.Services.Count > 0)
            .WithMessage("An order must contain at least one product or service.");

        RuleForEach(x => x.Request.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.ProductId).NotEmpty();
            i.RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
        });
        RuleForEach(x => x.Request.Services).ChildRules(s =>
        {
            s.RuleFor(x => x.ServiceId).NotEmpty();
            s.RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
            s.RuleFor(x => x.Notes).MaximumLength(1000);
        });

        RuleFor(x => x.Request.Discount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.DeliveryFee).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.Tax).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.CustomerNotes).MaximumLength(2000);

        // MED-001: validate addresses when supplied (required fields + length limits matching the schema).
        When(x => x.Request.ShippingAddress is not null,
            () => RuleFor(x => x.Request.ShippingAddress!).SetValidator(new OrderAddressValidator()));
        When(x => x.Request.BillingAddress is not null,
            () => RuleFor(x => x.Request.BillingAddress!).SetValidator(new OrderAddressValidator()));
    }
}

/// <summary>MED-001: reusable validator for an order shipping/billing address.</summary>
public sealed class OrderAddressValidator : AbstractValidator<AddressRequest>
{
    public OrderAddressValidator()
    {
        RuleFor(x => x.Street).NotEmpty().MaximumLength(250);
        RuleFor(x => x.City).NotEmpty().MaximumLength(120);
        RuleFor(x => x.State).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Country).NotEmpty().MaximumLength(120);
        RuleFor(x => x.PostalCode).MaximumLength(20);
    }
}

/// <summary>
/// Creates an order in Draft then places it (PendingPayment). Crucially, order creation is
/// NEVER blocked by stock availability. Snapshot pricing is captured from the current catalog,
/// and inventory is only touched later, after payment confirmation.
/// </summary>
public sealed class CreateOrderHandler(
    IOrderRepository orders,
    IProductRepository products,
    IServiceRepository services,
    ICustomerRepository customers,
    IInvoiceRepository invoices,
    ICurrentUser currentUser,
    IReferenceGenerator references,
    IOrderQueries orderQueries,
    DeChimex.Application.Common.Interfaces.ICategoryOptionsQueries categoryOptions,
    IUnitOfWork uow) : ICommandHandler<CreateOrderCommand, OrderDto>
{
    public async Task<OrderDto> Handle(CreateOrderCommand command, CancellationToken ct)
    {
        var r = command.Request;

        // Quote conversions were negotiated by staff -> the custom workflow kind.
        var kind = command.QuoteInvoiceId is not null ? OrderKind.Custom : command.Kind;

        // Resolve the customer (explicit for staff-created orders, self for authenticated customers).
        var customerId = r.CustomerId;
        if (customerId is null && currentUser.UserId is not null)
        {
            var self = await customers.GetByUserIdAsync(currentUser.UserId.Value, ct);
            customerId = self?.Id;
        }
        if (customerId is null)
            throw new NotFoundException("A valid customer is required to create an order.");

        var customer = await customers.GetByIdAsync(customerId.Value, ct)
            ?? throw new NotFoundException("Customer", customerId.Value);

        var orderNumber = await references.NextOrderNumberAsync(ct);
        var order = Order.CreateDraft(orderNumber, customerId.Value, kind);

        // Product lines capture price snapshot from the catalog. The catalog price is
        // authoritative: the client can only request quantities, never amounts.
        foreach (var item in r.Items)
        {
            var product = await products.GetByIdAsync(item.ProductId, ct)
                ?? throw new NotFoundException("Product", item.ProductId);

            // Server-side authoritative direct-purchase gate: custom-only products can
            // never slip through the cart/checkout path (catches items added before the
            // product became custom, or a tampered request). Staff/quote orders skip it.
            if (kind == OrderKind.Direct)
            {
                var hasOptions = product.CategoryId != Guid.Empty
                    && await categoryOptions.CategoryHasActiveOptionsAsync(product.CategoryId, ct);
                DirectCheckout.RequireEligible(product.Name, product.IsCustomizable, hasOptions);
            }

            order.AddProduct(product.Id, product.Name, product.Sku, product.SellingPrice.Amount, item.Quantity);
        }

        // Service lines never affect inventory.
        foreach (var svc in r.Services)
        {
            var service = await services.GetByIdAsync(svc.ServiceId, ct)
                ?? throw new NotFoundException("Service", svc.ServiceId);
            order.AddService(service.Id, service.Name, service.BasePrice.Amount, svc.Quantity, svc.Notes);
        }

        order.SetPricing(r.Discount, r.DeliveryFee, r.Tax);
        order.SetAddresses(ToAddress(r.ShippingAddress), ToAddress(r.BillingAddress));
        order.SetCustomerNotes(r.CustomerNotes);
        order.PlaceOrder(); // -> PendingPayment, raises OrderPlacedEvent

        // The invoice is issued BEFORE payment (status Issued/DUE). When the order is
        // created from a staff quote invoice, that quote is adopted instead, so the
        // customer sees one document from the quote through to payment.
        if (command.QuoteInvoiceId is not null)
        {
            var quote = await invoices.GetByIdAsync(command.QuoteInvoiceId.Value, ct)
                ?? throw new NotFoundException("Invoice", command.QuoteInvoiceId.Value);
            if (quote.HasOrder)
                throw new ConflictException("invoice.converted", "This invoice was already converted to an order.");
            if (quote.Status != InvoiceStatus.Draft)
                throw new BusinessRuleValidationException("invoice.state", "Only draft quote invoices can be converted to an order.");
            quote.AdoptForOrder(order);
            // The adopted document becomes this order's invoice: a QTE- reference is
            // renumbered onto the INV sequence so every paid document reads INV-….
            quote.RenumberAfterReferenceConflict(await references.NextInvoiceNumberAsync(ct));
            quote.RefreshCustomerName(customer.FullName);
            invoices.Update(quote);
        }
        else
        {
            var invoice = Domain.Entities.Invoice.Issue(
                await references.NextInvoiceNumberAsync(ct), order, customer.FullName, r.CustomerNotes);
            invoice.SetShareToken(references.NewShareToken());
            await invoices.AddAsync(invoice, ct);
        }

        await orders.AddAsync(order, ct);
        await uow.SaveChangesAsync(ct);

        return await orderQueries.GetByIdAsync(order.Id, ct)
            ?? throw new NotFoundException("Order", order.Id);
    }

    private static Address? ToAddress(AddressRequest? a) =>
        a is null ? null : Address.Create(a.Street, a.City, a.State, a.Country, a.PostalCode);
}
