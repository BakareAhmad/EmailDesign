using System.Net;
using System.Text;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using DeChimex.IntegrationTests.Fakes;
using DeChimex.Persistence.Context;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Full payment lifecycle correctness: paid happens ONLY after server-side verification
/// or a signed webhook, in-flight/transient verification stays retryable and unpaid,
/// terminal failures keep the order retryable with a fresh reference per attempt, foreign
/// references can never be probed or confirmed by other customers, and refunds reverse
/// lifetime spending exactly once. Duplicate events never double-apply side effects.
/// </summary>
public class PaymentLifecycleTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly FakePaymentGateway _gateway;
    private readonly IPaymentChargeCalculator _calculator;

    public PaymentLifecycleTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _gateway = (FakePaymentGateway)factory.Services.GetRequiredService<IPaymentGateway>();
        _calculator = factory.Services.GetRequiredService<IPaymentChargeCalculator>();
    }

    [Fact]
    public async Task Pending_verification_stays_retryable_and_unpaid_then_success_settles_once()
    {
        var (orderId, orderTotal, customerId) = await PlaceProductOrderAsync(5000m);
        var reference = await InitializeAsync(orderId);

        // 1. In-flight / transient verification (bank transfer, USSD, mobile money or a
        //    blipped verify request) must NOT mark the payment failed or paid.
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Pending;
        PaymentDto dto;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            dto = await sender.Send(new VerifyPaymentCommand(reference, customerId));
        }
        dto.Status.Should().Be("Initialized", "pending verification keeps the attempt open");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var payment = await db.Payments.SingleAsync(p => p.Reference == reference);
            payment.Status.Should().Be(PaymentStatus.Initialized);
            payment.FailureReason.Should().BeNull("a pending verification is not a failure");
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.PendingPayment);
        }

        // 2. The same attempt settles on a later successful verification.
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            dto = await sender.Send(new VerifyPaymentCommand(reference, customerId));
        }
        dto.Status.Should().Be("Succeeded");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Paid);
            (await db.Customers.SingleAsync(c => c.Id == customerId)).LifetimeSpending.Amount.Should().Be(orderTotal,
                "lifetime spending records the business order value once, regardless of which verification settled it");
        }
    }

    [Fact]
    public async Task Failed_attempt_keeps_order_retryable_with_fresh_reference_and_settles_once()
    {
        var (orderId, orderTotal, customerId) = await PlaceProductOrderAsync(4500m);

        // 1. Terminal failure marks only the attempt failed.
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Failed;
        var firstRef = await InitializeAsync(orderId);
        PaymentDto dto;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            dto = await sender.Send(new VerifyPaymentCommand(firstRef, customerId));
        }
        dto.Status.Should().Be("Failed");
        dto.FailureReason.Should().NotBeNullOrWhiteSpace();

        // 2. A second verification of the same failed attempt is a no-op (no second
        //    failure transition, no throw) — customer-safe to refresh the page.
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            dto = await sender.Send(new VerifyPaymentCommand(firstRef, customerId));
        }
        dto.Status.Should().Be("Failed");

        // 3. Retry on the SAME order: a fresh reference per attempt, order untouched.
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        var secondRef = await InitializeAsync(orderId);
        secondRef.Should().NotBe(firstRef, "each attempt gets its own traceable reference");
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            dto = await sender.Send(new VerifyPaymentCommand(secondRef, customerId));
        }
        dto.Status.Should().Be("Succeeded");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Paid);
            var attempts = await db.Payments.Where(p => p.OrderId == orderId).ToListAsync();
            attempts.Should().HaveCount(2, "attempts stay historically traceable");
            attempts.Count(p => p.Status == PaymentStatus.Succeeded).Should().Be(1);
            attempts.Count(p => p.Status == PaymentStatus.Failed).Should().Be(1);
            (await db.Receipts.CountAsync(r => r.OrderId == orderId)).Should().Be(1);
            (await db.Customers.SingleAsync(c => c.Id == customerId)).LifetimeSpending.Amount
                .Should().Be(orderTotal, "spending records the business order value exactly once");
        }
    }

    [Fact]
    public async Task Foreign_customer_cannot_verify_or_probe_another_customers_payment()
    {
        var (orderId, _, customerId) = await PlaceProductOrderAsync(3000m);
        var reference = await InitializeAsync(orderId);
        var callsBefore = _gateway.VerifyCalls.Count;

        var otherUserId = await TestSeed.RegisterCustomerAsync(_factory.Services, $"intruder.{Guid.NewGuid():N}@yopmail.com");
        Guid intruderCustomerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var customers = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
            intruderCustomerId = (await customers.GetByUserIdAsync(otherUserId))!.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var act = async () => await sender.Send(new VerifyPaymentCommand(reference, intruderCustomerId));
            await act.Should().ThrowAsync<NotFoundException>("a foreign reference must behave exactly like an unknown one");
        }

        _gateway.VerifyCalls.Count.Should().Be(callsBefore, "an ownership mismatch must not even call the gateway");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Payments.SingleAsync(p => p.Reference == reference)).Status.Should().Be(PaymentStatus.Initialized);
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.PendingPayment);
        }

        // The rightful owner can still verify it afterwards.
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var act = async () => await sender.Send(new VerifyPaymentCommand("PAY-DOES-NOT-EXIST", customerId));
            await act.Should().ThrowAsync<NotFoundException>("unknown references are rejected the same way");
        }
    }

    [Fact]
    public async Task Currency_mismatch_is_never_confirmed_on_verify_or_webhook()
    {
        var (orderId, orderTotal, customerId) = await PlaceProductOrderAsync(2500m);
        var payable = orderTotal + _calculator.ComputeCustomerBorneCharge(orderTotal);

        // 1. Verify reports the right number in the WRONG currency -> not confirmed.
        var reference = await InitializeAsync(orderId);
        _gateway.VerifyCurrency = "USD";
        PaymentDto dto;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            dto = await sender.Send(new VerifyPaymentCommand(reference, customerId));
        }
        dto.Status.Should().Be("Failed");
        dto.FailureReason.Should().Contain("Currency mismatch");
        _gateway.VerifyCurrency = null;

        // 2. Webhook with the right amount in the wrong currency -> ignored, unpaid.
        var reference2 = await InitializeAsync(orderId);
        (await PostWebhookAsync(reference2, ToKobo(payable), "USD")).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Payments.SingleAsync(p => p.Reference == reference2)).Status.Should().Be(PaymentStatus.Initialized);
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.PendingPayment);
        }

        // 3. The right currency confirms.
        (await PostWebhookAsync(reference2, ToKobo(payable), "NGN")).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Payments.SingleAsync(p => p.Reference == reference2)).Status.Should().Be(PaymentStatus.Succeeded);
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Paid);
        }
    }

    [Fact]
    public async Task Refund_reverses_lifetime_spending_exactly_once()
    {
        var refundCallsBefore = _gateway.RefundCalls.Count;
        var (orderId, orderTotal, customerId) = await PlaceProductOrderAsync(7000m);
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        var reference = await InitializeAsync(orderId);
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new VerifyPaymentCommand(reference, customerId));
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Customers.SingleAsync(c => c.Id == customerId)).LifetimeSpending.Amount.Should().Be(orderTotal);
        }

        // Refund runs against Paystack first, then reverses the business accounting.
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new RefundOrderCommand(orderId));
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Refunded);
            var payment = await db.Payments.SingleAsync(p => p.Reference == reference);
            payment.Status.Should().Be(PaymentStatus.Refunded);
            payment.RefundReference.Should().NotBeNullOrWhiteSpace();
            (await db.Customers.SingleAsync(c => c.Id == customerId)).LifetimeSpending.Amount.Should().Be(0m,
                "lifetime spending is net of refunds");
        }

        // A second refund is rejected and never reverses spending again.
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var act = async () => await sender.Send(new RefundOrderCommand(orderId));
            await act.Should().ThrowAsync<BusinessRuleValidationException>();
        }
        _gateway.RefundCalls.Count.Should().Be(refundCallsBefore + 1, "the provider is never asked to refund twice");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Customers.SingleAsync(c => c.Id == customerId)).LifetimeSpending.Amount.Should().Be(0m);
        }
    }

    [Fact]
    public async Task Refund_provider_failure_leaves_order_and_spending_untouched()
    {
        var (orderId, orderTotal, customerId) = await PlaceProductOrderAsync(6500m);
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        var reference = await InitializeAsync(orderId);
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new VerifyPaymentCommand(reference, customerId));
        }

        _gateway.RefundSucceeds = false;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var act = async () => await sender.Send(new RefundOrderCommand(orderId));
            await act.Should().ThrowAsync<ConflictException>();
        }
        _gateway.RefundSucceeds = true;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Paid,
                "a failed provider refund must leave the order paid and retryable");
            (await db.Payments.SingleAsync(p => p.Reference == reference)).Status.Should().Be(PaymentStatus.Succeeded);
            (await db.Customers.SingleAsync(c => c.Id == customerId)).LifetimeSpending.Amount.Should().Be(orderTotal);
        }

        // The refund can be retried and then succeeds end to end.
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new RefundOrderCommand(orderId));
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Refunded);
            (await db.Customers.SingleAsync(c => c.Id == customerId)).LifetimeSpending.Amount.Should().Be(0m);
        }
    }

    [Fact]
    public async Task Duplicate_verification_and_webhook_never_double_apply_side_effects()
    {
        var (orderId, orderTotal, customerId) = await PlaceProductOrderAsync(5500m);
        var payable = orderTotal + _calculator.ComputeCustomerBorneCharge(orderTotal);
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        var reference = await InitializeAsync(orderId);

        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new VerifyPaymentCommand(reference, customerId));
        }
        // Duplicate verify + duplicate webhook + webhook-after-return: all no-ops.
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new VerifyPaymentCommand(reference, customerId));
        }
        (await PostWebhookAsync(reference, ToKobo(payable), "NGN")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostWebhookAsync(reference, ToKobo(payable), "NGN")).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Receipts.CountAsync(r => r.OrderId == orderId)).Should().Be(1);
            (await db.Customers.SingleAsync(c => c.Id == customerId)).LifetimeSpending.Amount.Should().Be(orderTotal,
                "spending is recorded exactly once");
            (await db.Payments.SingleAsync(p => p.Reference == reference)).PaidAtUtc.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_zero_payable_order_never_reaches_the_gateway()
    {
        // A 100% discount drives the payable to zero if the pricing model allows it.
        // Either the order/pricing model refuses the invalid total, or the payment-init
        // guard refuses the charge — in every case the gateway is never asked to charge
        // a zero amount and the failure is a clean, customer-safe validation error.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create("Zero Payable Category");
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var product = Product.Create("Zero Payable Probe", $"ZP-{Guid.NewGuid():N}"[..12], null, category.Id, 1200m);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.Inventories.Add(Inventory.Create(product.Id, product.Name, 10));
        var customer = Customer.Create("Zero", "Payable", $"zero.{Guid.NewGuid():N}@yopmail.com");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var act = async () =>
        {
            var order = await sender.Send(new CreateOrderCommand(new CreateOrderRequest(
                customer.Id,
                new List<CreateOrderItemRequest> { new(product.Id, 1) },
                new List<CreateOrderServiceRequest>(),
                null, null, null, Discount: 1200m, DeliveryFee: 0m, Tax: 0m)));
            await sender.Send(new InitializePaymentCommand(
                new InitializePaymentRequest(order.Id, "paystack"), "https://localhost/checkout/return"));
        };

        var exception = await act.Should().ThrowAsync<Exception>();
        (exception.Which is ConflictException or BusinessRuleValidationException or ValidationAppException)
            .Should().BeTrue("an invalid amount must be refused before the gateway is involved");
    }

    private static long ToKobo(decimal naira) => (long)Math.Round(naira * 100m);

    private async Task<(Guid OrderId, decimal Total, Guid CustomerId)> PlaceProductOrderAsync(decimal price)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create("Lifecycle Test Category");
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var product = Product.Create($"Lifecycle {price} {Guid.NewGuid():N}"[..40], $"LC-{Guid.NewGuid():N}"[..12], null, category.Id, price);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.Inventories.Add(Inventory.Create(product.Id, product.Name, 50));
        var customer = Customer.Create("Life", "Cycle", $"life.{Guid.NewGuid():N}@yopmail.com");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(new CreateOrderRequest(
            customer.Id,
            new List<CreateOrderItemRequest> { new(product.Id, 1) },
            new List<CreateOrderServiceRequest>(),
            null, null, null, 0m, 0m, 0m)));
        return (order.Id, order.GrandTotal, customer.Id);
    }

    private async Task<string> InitializeAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var init = await sender.Send(new InitializePaymentCommand(
            new InitializePaymentRequest(orderId, "paystack"), "https://localhost/checkout/return"));
        return init.Reference;
    }

    private Task<HttpResponseMessage> PostWebhookAsync(string reference, long amountKobo, string? currency)
    {
        var currencyField = currency is null ? "" : $",\"currency\":\"{currency}\"";
        var body = $"{{\"event\":\"charge.success\",\"data\":{{\"reference\":\"{reference}\",\"amount\":{amountKobo}{currencyField}}}}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhook/paystack")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-paystack-signature", "valid-signature");
        return _factory.CreateClient().SendAsync(request);
    }
}
