using DeChimex.Application.Common;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using FluentValidation;

namespace DeChimex.Application.Features.Payments.Commands;

public sealed record InitializePaymentCommand(InitializePaymentRequest Request, string CallbackUrl) : ICommand<PaymentInitResponse>;

public sealed class InitializePaymentValidator : AbstractValidator<InitializePaymentCommand>
{
    public InitializePaymentValidator()
    {
        RuleFor(x => x.Request.OrderId).NotEmpty();
        RuleFor(x => x.Request.Method).NotEmpty();
    }
}

/// <summary>
/// Creates a Payment record and, for Paystack, initializes a gateway transaction returning an
/// authorization URL. Non-gateway counter payments (bank transfer / cash) are confirmed by staff.
/// </summary>
public sealed class InitializePaymentHandler(
    IOrderRepository orders,
    IPaymentRepository payments,
    ICustomerRepository customers,
    IPaymentGateway gateway,
    IReferenceGenerator references,
    IPaymentChargeCalculator chargeCalculator,
    IApplicationSettingRepository settings,
    IUnitOfWork uow) : ICommandHandler<InitializePaymentCommand, PaymentInitResponse>
{
    public async Task<PaymentInitResponse> Handle(InitializePaymentCommand command, CancellationToken ct)
    {
        var order = await orders.GetWithLinesAsync(command.Request.OrderId, ct)
            ?? throw new NotFoundException("Order", command.Request.OrderId);

        if (order.Status is not (OrderStatus.PendingPayment or OrderStatus.Draft))
            throw new ConflictException("payment.order.state", "Order is not awaiting payment.");

        if (!Enum.TryParse<PaymentMethod>(command.Request.Method, ignoreCase: true, out var method))
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["Method"] = new[] { "Unsupported payment method." }
            });

        var reference = references.NewPaymentReference();

        // The order total is the business value; for gateway payments the configured
        // service fee — the passed-on portion of Paystack's transaction charge (owner
        // directive 2026-09-08; service-fee feature 2026-09-23) — is included in the
        // transaction while the order record keeps its own total. The percentage and the
        // resulting amounts are determined server-side here; nothing a browser submits
        // can change them.
        var orderAmount = order.GrandTotal.Amount;
        var serviceFeePercent = method == PaymentMethod.Paystack
            ? ServiceFeeSettings.Parse((await settings.GetByKeyAsync(ServiceFeeSettings.Key, ct))?.Value)
            : 0m;
        var breakdown = chargeCalculator.Compute(orderAmount, serviceFeePercent);
        var customerBorneCharge = method == PaymentMethod.Paystack ? breakdown.ServiceFeeAmount : 0m;
        var customerPayable = orderAmount + customerBorneCharge;

        // Gateway charges must be strictly positive (Paystack rejects zero/negative
        // amounts); reject up front with a clear message instead of failing at Paystack.
        if (method == PaymentMethod.Paystack && customerPayable <= 0m)
            throw new ConflictException("payment.amount.invalid",
                "There is nothing to pay on this order. A Paystack payment requires a positive amount.");

        var payment = Payment.Initialize(order.Id, order.CustomerId, customerPayable, method, reference,
            customerBorneCharge, orderAmount, serviceFeePercent, method == PaymentMethod.Paystack ? breakdown.PaystackFee : 0m);

        // AUD-011: persist the attempt BEFORE the external call. If the save below were to
        // fail there is no gateway transaction yet; conversely, if the gateway call fails,
        // the attempt stays on record as Failed instead of leaving an orphaned (or missing)
        // ledger row whose later webhook would land with no local payment.
        await payments.AddAsync(payment, ct);
        await uow.SaveChangesAsync(ct);

        if (method == PaymentMethod.Paystack)
        {
            var customer = await customers.GetByIdAsync(order.CustomerId, ct)
                ?? throw new NotFoundException("Customer", order.CustomerId);

            // Paystack requires a customer email; walk-in customers must be given one before
            // card payment (the portal/checkout registration paths always capture it).
            if (customer.Email is null)
                throw new ConflictException("payment.customer.email.required",
                    "The customer has no email address, which Paystack requires. Add one or use Bank Transfer / Cash.");

            var init = await gateway.InitializeAsync(new PaymentInitializationRequest(
                reference, customer.Email.Value, customerPayable, order.GrandTotal.Currency,
                command.CallbackUrl, new Dictionary<string, string> { ["orderId"] = order.Id.ToString() }), ct);

            if (!init.Success)
            {
                payment.MarkFailed(init.Message ?? "Failed to initialize payment.", null);
                await uow.SaveChangesAsync(ct);
                throw new ConflictException("payment.init.failed", init.Message ?? "Failed to initialize payment.");
            }

            if (init.AuthorizationUrl is not null) payment.SetAuthorizationUrl(init.AuthorizationUrl);
            await uow.SaveChangesAsync(ct); // attach the authorization URL to the persisted attempt
        }

        return new PaymentInitResponse(payment.Id, payment.Reference, payment.AuthorizationUrl, payment.Status.ToString());
    }
}
