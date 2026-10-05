using System.Net;
using System.Text;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.IntegrationTests.Fakes;
using DeChimex.Persistence.Context;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Customer-borne Paystack charge (owner directive 2026-09-08): Paystack transactions are
/// initialized for order amount + published NGN charge (the business value never absorbs the
/// fee), the webhook only confirms matching amounts, and server-side verification rejects
/// amount mismatches. Duplicate deliveries stay idempotent.
/// </summary>
public class CustomerBorneChargeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly FakePaymentGateway _gateway;
    private readonly IPaymentChargeCalculator _calculator;

    public CustomerBorneChargeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _gateway = (FakePaymentGateway)factory.Services.GetRequiredService<IPaymentGateway>();
        _calculator = factory.Services.GetRequiredService<IPaymentChargeCalculator>();
    }

    [Fact]
    public async Task Paystack_initialization_charges_order_plus_customer_borne_charge()
    {
        var (orderId, orderTotal) = await PlaceProductOrderAsync(5000m);

        Guid paymentId;
        string reference;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var init = await sender.Send(new InitializePaymentCommand(
                new InitializePaymentRequest(orderId, "paystack"), "https://localhost:7061/checkout/return"));
            paymentId = init.PaymentId;
            reference = init.Reference;
        }

        var expectedCharge = _calculator.ComputeCustomerBorneCharge(orderTotal);
        expectedCharge.Should().BeGreaterThan(0, "the customer bears the gateway fee");

        // The gateway was initialized for order + charge, not the bare order value...
        _gateway.LastInitialization.Should().NotBeNull();
        _gateway.LastInitialization!.Amount.Should().Be(orderTotal + expectedCharge);

        // ...and the payment record distinguishes the final payable from the charge portion.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
            payment.Reference.Should().Be(reference);
            payment.Amount.Amount.Should().Be(orderTotal + expectedCharge);
            payment.CustomerBorneCharge.Should().Be(expectedCharge);
            payment.Status.Should().Be(PaymentStatus.Initialized, "initialization must never mark the payment succeeded");
        }
    }

    [Fact]
    public async Task Webhook_confirms_matching_amount_once_and_ignores_duplicates_and_mismatches()
    {
        var (orderId, orderTotal) = await PlaceProductOrderAsync(4000m);
        string reference = await InitializeAsync(orderId);
        var payable = orderTotal + _calculator.ComputeCustomerBorneCharge(orderTotal);

        // 1. A charge.success carrying the exact initialized amount confirms the order.
        (await PostWebhookAsync(reference, ToKobo(payable))).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Payments.SingleAsync(p => p.Reference == reference)).Status.Should().Be(PaymentStatus.Succeeded);
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Paid);
        }

        // 2. Duplicate delivery: still one succeeded payment, one receipt, order still Paid once.
        (await PostWebhookAsync(reference, ToKobo(payable))).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Receipts.CountAsync(r => r.OrderId == orderId)).Should().Be(1, "duplicate webhooks must not duplicate receipts");
            (await db.Payments.CountAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Succeeded)).Should().Be(1);
        }

        // 3. A mismatched amount for a fresh payment is rejected (never confirmed).
        var (orderId2, orderTotal2) = await PlaceProductOrderAsync(3000m);
        var reference2 = await InitializeAsync(orderId2);
        await PostWebhookAsync(reference2, ToKobo(orderTotal2)); // order value, NOT order + charge
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Payments.SingleAsync(p => p.Reference == reference2)).Status.Should().Be(PaymentStatus.Initialized);
            (await db.Orders.SingleAsync(o => o.Id == orderId2)).Status.Should().Be(OrderStatus.PendingPayment);
        }
    }

    [Fact]
    public async Task Verification_with_mismatched_amount_fails_and_order_stays_unpaid()
    {
        var (orderId, orderTotal) = await PlaceProductOrderAsync(6000m);
        var reference = await InitializeAsync(orderId);

        _gateway.VerifyAmountPaid = orderTotal; // gateway reports the bare order value, not the payable
        DeChimex.Contracts.Payments.PaymentDto dto;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            dto = await sender.Send(new VerifyPaymentCommand(reference));
        }
        dto.Status.Should().Be("Failed", "an amount mismatch must never confirm the payment");
        _gateway.VerifyAmountPaid = null;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var payment = await db.Payments.SingleAsync(p => p.Reference == reference);
            payment.Status.Should().Be(PaymentStatus.Failed);
            payment.FailureReason.Should().Contain("Amount mismatch");
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.PendingPayment);
        }
    }

    private static long ToKobo(decimal naira) => (long)Math.Round(naira * 100m);

    private async Task<(Guid OrderId, decimal Total)> PlaceProductOrderAsync(decimal price)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create("Charge Test Category");
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var product = Product.Create($"Charge Test {price}", $"CT-{price:0}", null, category.Id, price);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.Inventories.Add(Inventory.Create(product.Id, product.Name, 50));
        var customer = Customer.Create("Charge", "Test", $"charge.{Guid.NewGuid():N}@yopmail.com");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(new CreateOrderRequest(
            customer.Id,
            new List<CreateOrderItemRequest> { new(product.Id, 1) },
            new List<CreateOrderServiceRequest>(),
            null, null, null, 0m, 0m, 0m)));
        return (order.Id, order.GrandTotal);
    }

    private async Task<string> InitializeAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var init = await sender.Send(new InitializePaymentCommand(
            new InitializePaymentRequest(orderId, "paystack"), "https://localhost:7061/checkout/return"));
        return init.Reference;
    }

    private Task<HttpResponseMessage> PostWebhookAsync(string reference, long amountKobo)
    {
        var body = $"{{\"event\":\"charge.success\",\"data\":{{\"reference\":\"{reference}\",\"amount\":{amountKobo}}}}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhook/paystack")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-paystack-signature", "valid-signature");
        return _factory.CreateClient().SendAsync(request);
    }
}
