using System.Net;
using System.Text;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.IntegrationTests.Fakes;
using DeChimex.Persistence.Context;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Configurable service fee (SERVICE_FEE_EVALUATION.md): Service Fee = Paystack fee × Y/100.
/// Configuration (Owner/Manager only, two-step confirmation, concurrency guard, audit),
/// transaction snapshots at 0%/partial%/100%, historical immutability, failure/retry
/// semantics and the refund/spending workflow for both order kinds.
/// </summary>
public class ServiceFeeFeatureTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly FakePaymentGateway _gateway;
    private readonly IPaymentChargeCalculator _calculator;

    public ServiceFeeFeatureTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _gateway = (FakePaymentGateway)factory.Services.GetRequiredService<IPaymentGateway>();
        _calculator = factory.Services.GetRequiredService<IPaymentChargeCalculator>();
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        _gateway.VerifyAmountPaid = null;
        _gateway.VerifyFees = null;
        _gateway.RefundSucceeds = true;
    }

    // ------------------------------------------------------------------
    // Configuration
    // ------------------------------------------------------------------

    [Fact]
    public async Task Owner_changes_service_fee_through_the_confirmed_flow()
    {
        await SetServiceFeePercentAsync(100m);
        var client = await OwnerBrowserAsync();

        // Step 1: preview — nothing saved, confirmation required (brief §3).
        var preview = await PostServiceFeePreviewAsync(client, "50");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await preview.Content.ReadAsStringAsync();
        html.Should().Contain("Change Service Fee?");
        html.Should().Contain("Current service fee", "the confirmation must show the current value");
        html.Should().Contain("50%", "the confirmation must show the new value");
        html.Should().Contain("affect future transactions");
        html.Should().Contain("will not be recalculated");
        (await GetServiceFeePercentAsync()).Should().Be(100m, "preview alone must never change the rate");

        // Step 2: explicit confirmation.
        var token = TestSeed.ExtractAntiforgeryToken(html);
        var confirm = await client.PostAsync("/admin/settings/service-fee/confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPercent"] = "100",
            ["RequestedPercent"] = "50",
            ["__RequestVerificationToken"] = token
        }));
        confirm.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await GetServiceFeePercentAsync()).Should().Be(50m);
    }

    [Fact]
    public async Task Manager_can_change_service_fee_but_sales_staff_cannot()
    {
        await SetServiceFeePercentAsync(100m);
        var managerEmail = $"mgr.{Guid.NewGuid():N}@example.com";
        await TestSeed.CreateStaffUserAsync(_factory.Services, managerEmail, TestSeed.DefaultPassword, "Manager");
        var manager = CreateBrowser();
        (await TestSeed.LoginMvcAsync(manager, managerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var preview = await PostServiceFeePreviewAsync(manager, "25");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirm = await manager.PostAsync("/admin/settings/service-fee/confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPercent"] = "100",
            ["RequestedPercent"] = "25",
            ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await preview.Content.ReadAsStringAsync())
        }));
        confirm.StatusCode.Should().Be(HttpStatusCode.Redirect, "Manager holds settings.manage");
        (await GetServiceFeePercentAsync()).Should().Be(25m);

        var salesEmail = $"sales.{Guid.NewGuid():N}@example.com";
        await TestSeed.CreateStaffUserAsync(_factory.Services, salesEmail, TestSeed.DefaultPassword, "SalesStaff");
        var sales = CreateBrowser();
        (await TestSeed.LoginMvcAsync(sales, salesEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var denied = await PostServiceFeePreviewAsync(sales, "75");
        denied.StatusCode.Should().Be(HttpStatusCode.Redirect, "challenge: sales staff lack settings.manage");
        denied.Headers.Location!.ToString().Should().Contain("AccessDenied", "signed-in staff without settings.manage must be turned away");
        (await GetServiceFeePercentAsync()).Should().Be(25m, "other staff cannot change the setting");
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("150")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("5.001")]
    public async Task Invalid_percentages_are_rejected_and_nothing_is_saved(string value)
    {
        await SetServiceFeePercentAsync(100m);
        var client = await OwnerBrowserAsync();
        var preview = await PostServiceFeePreviewAsync(client, value);
        preview.StatusCode.Should().Be(HttpStatusCode.Redirect, "invalid values bounce back to settings with an error");
        (await GetServiceFeePercentAsync()).Should().Be(100m);
    }

    [Fact]
    public async Task Stale_confirmation_cannot_overwrite_a_newer_change()
    {
        await SetServiceFeePercentAsync(100m);
        var client = await OwnerBrowserAsync();
        var preview = await PostServiceFeePreviewAsync(client, "50");
        var html = await preview.Content.ReadAsStringAsync();

        // Meanwhile the live value moves (another admin / another session).
        await SetServiceFeePercentAsync(75m);

        var confirm = await client.PostAsync("/admin/settings/service-fee/confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPercent"] = "100", // the value the admin reviewed — now stale
            ["RequestedPercent"] = "50",
            ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(html)
        }));
        confirm.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await GetServiceFeePercentAsync()).Should().Be(75m, "a stale form must never silently overwrite the newer value");
    }

    [Fact]
    public async Task Generic_settings_save_cannot_change_the_service_fee()
    {
        await SetServiceFeePercentAsync(100m);
        var client = await OwnerBrowserAsync();
        var page = await client.GetAsync("/admin/settings");
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        var post = await client.PostAsync("/admin/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["values[business.motto]"] = "Silent change attempt",
            ["values[service_fee.percent]"] = "7",
            ["__RequestVerificationToken"] = token
        }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await GetServiceFeePercentAsync()).Should().Be(100m, "only the confirmed flow may change the rate (brief §3)");
    }

    [Fact]
    public async Task Change_is_written_to_the_business_activity_audit_log()
    {
        await SetServiceFeePercentAsync(100m);
        var client = await OwnerBrowserAsync();
        var preview = await PostServiceFeePreviewAsync(client, "50");
        await client.PostAsync("/admin/settings/service-fee/confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPercent"] = "100",
            ["RequestedPercent"] = "50",
            ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await preview.Content.ReadAsStringAsync())
        }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var entries = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Title == "Service fee configuration changed")
            .ToListAsync();
        entries.Should().Contain(a => a.Summary!.Contains("from 100% to 50%")
            && a.Summary.Contains("Applies to future transactions"));
    }

    [Fact]
    public async Task Current_value_is_displayed_on_the_settings_page()
    {
        await SetServiceFeePercentAsync(33.5m);
        var client = await OwnerBrowserAsync();
        var html = await (await client.GetAsync("/admin/settings")).Content.ReadAsStringAsync();
        html.Should().Contain("Service Fee");
        html.Should().Contain("33.5", "the settings page shows the current percentage");
    }

    // ------------------------------------------------------------------
    // Transactions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Zero_percent_leaves_the_order_total_untouched()
    {
        await SetServiceFeePercentAsync(0m);
        var (orderId, total) = await PlaceProductOrderAsync(7000m);
        var init = await InitializeAsync(orderId);
        init.Payable.Should().Be(total);
        init.Charge.Should().Be(0m);
    }

    [Fact]
    public async Task Partial_percent_applies_a_cut_of_the_paystack_fee_to_the_payable()
    {
        await SetServiceFeePercentAsync(50m);
        var (orderId, total) = await PlaceProductOrderAsync(9500m);
        var (paymentId, reference, payable, charge) = await InitializeAsync(orderId);

        var expected = _calculator.Compute(total, 50m);
        charge.Should().Be(expected.ServiceFeeAmount);
        payable.Should().Be(total + expected.ServiceFeeAmount);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
        payment.ServiceFeePercent.Should().Be(50m, "the applied percentage is snapshotted (brief §5)");
        payment.OrderAmount.Should().Be(total);
        payment.PaystackFeeExpected.Should().Be(expected.PaystackFee);
        payment.CustomerBorneCharge.Should().Be(expected.ServiceFeeAmount);
    }

    [Fact]
    public async Task Verification_records_the_authoritative_paystack_fee()
    {
        await SetServiceFeePercentAsync(50m);
        var (orderId, _) = await PlaceProductOrderAsync(9500m);
        var (_, reference, _, _) = await InitializeAsync(orderId);

        _gateway.VerifyFees = 244.33m;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new VerifyPaymentCommand(reference));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var payment = await db.Payments.SingleAsync(p => p.Reference == reference);
            payment.Status.Should().Be(PaymentStatus.Succeeded);
            payment.PaystackFeeActual.Should().Be(244.33m, "the verify response `fees` is the authoritative fee and must be stored");
        }
    }

    [Fact]
    public async Task Historical_transactions_are_immutable_after_reconfiguration()
    {
        await SetServiceFeePercentAsync(50m);
        var (orderId, total) = await PlaceProductOrderAsync(9500m);
        var (paymentId, reference, payable, charge) = await InitializeAsync(orderId);
        _gateway.VerifyFees = 244.33m;
        await ConfirmAsync(reference);

        // Tomorrow's business decision must not rewrite yesterday's money (brief §4).
        await SetServiceFeePercentAsync(10m);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
        payment.ServiceFeePercent.Should().Be(50m, "the historical percentage is the one that was applied");
        payment.CustomerBorneCharge.Should().Be(charge);
        payment.Amount.Amount.Should().Be(payable);
        payment.PaystackFeeActual.Should().Be(244.33m);

        var receipt = await db.Receipts.AsNoTracking().SingleAsync(r => r.OrderId == orderId);
        receipt.Amount.Amount.Should().Be(payable);
        receipt.ServiceFee.Amount.Should().Be(charge, "the receipt shows the fee actually applied");
        receipt.OrderTotal.Amount.Should().Be(total);

        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.OrderId == orderId);
        invoice.GrandTotal.Amount.Should().Be(total, "the invoice is an order-value document and stays untouched");
    }

    [Fact]
    public async Task Failed_attempts_record_no_success_no_spending_and_no_fee_charge()
    {
        await SetServiceFeePercentAsync(50m);
        var (orderId, total) = await PlaceProductOrderAsync(6000m);
        var (paymentId, _, _, _) = await InitializeAsync(orderId);
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Failed;
        await ConfirmAsync((await ReferenceOfAsync(paymentId)));
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
        payment.Status.Should().Be(PaymentStatus.Failed, "a failed attempt is never a successful service-fee charge");
        (await db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.PendingPayment);
        (await db.Receipts.CountAsync(r => r.OrderId == orderId)).Should().Be(0);
        var failedOrder = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == failedOrder.CustomerId);
        customer.LifetimeSpending.Amount.Should().Be(0m, "failed payments never move lifetime spending");
    }

    [Fact]
    public async Task Retry_never_double_charges_and_reprices_at_the_new_configuration()
    {
        await SetServiceFeePercentAsync(50m);
        var (orderId, total) = await PlaceProductOrderAsync(9500m);
        var (firstId, firstRef, _, firstCharge) = await InitializeAsync(orderId);
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Failed;
        await ConfirmAsync(firstRef);
        _gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;

        // A retry is a NEW gateway transaction (its own Paystack fee): explicitly priced
        // at the configuration current at initialization (evaluation S6).
        await SetServiceFeePercentAsync(100m);
        var (secondId, secondRef, payable, charge) = await InitializeAsync(orderId);

        secondId.Should().NotBe(firstId, "each attempt has its own reconstructable payment record");
        var expected = _calculator.Compute(total, 100m);
        charge.Should().Be(expected.ServiceFeeAmount);
        charge.Should().NotBe(firstCharge, "the retry reflects the current configuration");

        await ConfirmAsync(secondRef);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.Payments.CountAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Succeeded))
            .Should().Be(1, "exactly one successful charge — the service fee is never applied twice");
        (await db.Receipts.CountAsync(r => r.OrderId == orderId)).Should().Be(1);
        var paidOrder = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == paidOrder.CustomerId);
        customer.LifetimeSpending.Amount.Should().Be(total, "spending records the order value exactly once");
    }

    [Fact]
    public async Task Refund_reverses_spending_and_keeps_the_fee_snapshot()
    {
        await SetServiceFeePercentAsync(50m);
        var (orderId, total) = await PlaceProductOrderAsync(9500m);
        var (paymentId, reference, payable, charge) = await InitializeAsync(orderId);
        await ConfirmAsync(reference);

        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new RefundOrderCommand(orderId));
        }

        using var dbScope = _factory.Services.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.CustomerBorneCharge.Should().Be(charge, "the refund never rewrites the applied service fee");
        payment.Amount.Amount.Should().Be(payable, "the full customer payment (incl. service fee) is the refunded amount");
        var receipt = await db.Receipts.SingleAsync(r => r.OrderId == orderId);
        receipt.IsRefunded.Should().BeTrue();
        receipt.ServiceFee.Amount.Should().Be(charge);
        var refundedOrder = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == refundedOrder.CustomerId);
        customer.LifetimeSpending.Amount.Should().Be(0m, "spending is net of refunds and order-value based");
    }

    [Fact]
    public async Task Service_fee_applies_identically_to_custom_orders()
    {
        await SetServiceFeePercentAsync(50m);
        var (orderId, total) = await PlaceCustomOrderAsync(12000m);
        var (_, reference, payable, charge) = await InitializeAsync(orderId);
        var expected = _calculator.Compute(total, 50m);
        charge.Should().Be(expected.ServiceFeeAmount);
        payable.Should().Be(total + expected.ServiceFeeAmount);
        await ConfirmAsync(reference);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var receipt = await db.Receipts.SingleAsync(r => r.OrderId == orderId);
        receipt.ServiceFee.Amount.Should().Be(charge);
        (await db.Orders.SingleAsync(o => o.Id == orderId)).Kind.Should().Be(OrderKind.Custom);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private async Task<HttpClient> OwnerBrowserAsync() =>
        await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);

    private async Task<HttpResponseMessage> PostServiceFeePreviewAsync(HttpClient client, string value)
    {
        var page = await client.GetAsync("/admin/settings");
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        return await client.PostAsync("/admin/settings/service-fee", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["newValue"] = value,
            ["__RequestVerificationToken"] = token
        }));
    }

    private async Task<decimal> GetServiceFeePercentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var setting = await db.ApplicationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "service_fee.percent");
        return DeChimex.Application.Common.ServiceFeeSettings.Parse(setting?.Value);
    }

    private async Task SetServiceFeePercentAsync(decimal percent)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var setting = await db.ApplicationSettings.FirstOrDefaultAsync(s => s.Key == "service_fee.percent");
        if (setting is null)
            db.ApplicationSettings.Add(ApplicationSetting.Create("service_fee.percent", DeChimex.Application.Common.ServiceFeeSettings.Format(percent)));
        else
            setting.SetValue(DeChimex.Application.Common.ServiceFeeSettings.Format(percent));
        await db.SaveChangesAsync();
        // Mirror the app's save path: the settings cache must never serve a stale rate.
        await scope.ServiceProvider.GetRequiredService<ICacheService>()
            .RemoveAsync(DeChimex.Application.Common.CacheKeys.SettingsAll);
    }

    private async Task<(Guid OrderId, decimal Total)> PlaceProductOrderAsync(decimal price)
        => await PlaceOrderAsync(price, custom: false);

    private async Task<(Guid OrderId, decimal Total)> PlaceCustomOrderAsync(decimal price)
        => await PlaceOrderAsync(price, custom: true);

    private async Task<(Guid OrderId, decimal Total)> PlaceOrderAsync(decimal price, bool custom)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create($"SF Test {Guid.NewGuid():N}"[..30]);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var product = Product.Create($"SF Test {price:0} {Guid.NewGuid():N}"[..40], $"SF-{price:0}-{Guid.NewGuid():N}"[..20], null, category.Id, price);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.Inventories.Add(Inventory.Create(product.Id, product.Name, 50));
        var customer = Customer.Create("ServiceFee", "Test", $"sf.{Guid.NewGuid():N}@yopmail.com");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(new CreateOrderRequest(
            customer.Id,
            new List<CreateOrderItemRequest> { new(product.Id, 1) },
            new List<CreateOrderServiceRequest>(),
            null, null, null, 0m, 0m, 0m),
            Kind: custom ? OrderKind.Custom : OrderKind.Direct));
        return (order.Id, order.GrandTotal);
    }

    private async Task<(Guid PaymentId, string Reference, decimal Payable, decimal Charge)> InitializeAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var init = await sender.Send(new InitializePaymentCommand(
            new InitializePaymentRequest(orderId, "paystack"), "https://localhost:7061/checkout/return"));
        using var dbScope = _factory.Services.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == init.PaymentId);
        return (init.PaymentId, init.Reference, payment.Amount.Amount, payment.CustomerBorneCharge);
    }

    private async Task<string> ReferenceOfAsync(Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        return (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId)).Reference;
    }

    private async Task ConfirmAsync(string reference)
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await sender.Send(new VerifyPaymentCommand(reference));
    }
}
