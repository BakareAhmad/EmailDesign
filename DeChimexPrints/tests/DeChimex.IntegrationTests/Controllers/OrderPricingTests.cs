using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// PRICING INTEGRITY: the stored product price must survive every order path.
/// Regression coverage for the ₦0.00-per-unit bug: custom-order submissions used to
/// hardcode 0m as the line unit price, so the customer Order Summary, the admin
/// finalization prefill, order totals and the payment amount all showed/charged zero
/// until staff retyped a price. These tests pin the catalog price snapshot at order
/// creation for the custom-order path and the cart-checkout path (multiple products
/// and quantities), and prove finalization can still override a line price.
/// </summary>
public class OrderPricingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OrderPricingTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private async Task<Guid> SeedCustomizableProductAsync(string name, decimal price)
    {
        var services = _factory.Services;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var category = Category.Create($"{name} Category", "test seed", 99);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        await sender.Send(new SaveCategoryOptionsCommand(category.Id, new SaveCategoryOptionsRequest(
            new List<SaveCategoryOptionDto>
            {
                new("colour", "Which colour?", "Text", true, null, null, new List<string>())
            })));

        var product = Product.Create(name, "SKU-" + Guid.NewGuid().ToString("N")[..12],
            "Seeded customizable product", category.Id, price, isCustomizable: true);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<(Guid OrderId, DeChimex.Domain.Entities.Order Order)> SubmitCustomOrderAsync(
        HttpClient customer, Guid productId, int quantity)
    {
        var form = await customer.GetAsync($"/portal/shop/{productId}/order");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var formHtml = await form.Content.ReadAsStringAsync();
        var optionId = Regex.Match(formHtml, "name=\"opt_([0-9a-f-]{36})\"").Groups[1].Value;
        optionId.Should().NotBeEmpty();

        var submit = await customer.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = quantity.ToString(),
                [$"opt_{optionId}"] = "Royal blue",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(formHtml)
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var orderId = Guid.Parse(Regex.Match(submit.Headers.Location!.ToString(),
            @"(?<=/portal/orders/)[0-9a-f-]{36}").Value);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var order = await db.Orders.Include(o => o.Items).FirstAsync(o => o.Id == orderId);
        return (orderId, order);
    }

    [Fact]
    public async Task CustomOrder_Submission_SnapshotsCatalogUnitPriceAndTotals()
    {
        var services = _factory.Services;
        var email = $"pricing-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email);
        await TestSeed.VerifyCustomerAsync(services, email);

        const decimal catalogPrice = 2750.5m;
        var productId = await SeedCustomizableProductAsync("Priced Jersey", catalogPrice);

        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        (_, var order) = await SubmitCustomOrderAsync(customer, productId, quantity: 3);

        order.Status.Should().Be(OrderStatus.AwaitingReview);
        var item = order.Items.Should().ContainSingle().Subject;
        item.UnitPrice.Amount.Should().Be(catalogPrice,
            "the order line must carry the stored product price, not a 0 placeholder");
        item.LineTotal.Amount.Should().Be(8251.5m);          // 3 × 2,750.50
        order.ItemsSubtotal.Amount.Should().Be(8251.5m);
        order.GrandTotal.Amount.Should().Be(8251.5m);

        // The customer's Order Summary renders the real per-unit price.
        var workspaceHtml = await (await customer.GetAsync($"/portal/orders/{order.Id}/workspace"))
            .Content.ReadAsStringAsync();
        workspaceHtml.Should().Contain("2,750.50");
        workspaceHtml.Should().Contain("8,251.50");

        // The admin finalization panel prefills the same real per-unit price.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var adminHtml = await (await owner.GetAsync($"/admin/orders/{order.Id}/workspace"))
            .Content.ReadAsStringAsync();
        adminHtml.Should().Contain("2750.5");
    }

    [Fact]
    public async Task CustomOrder_Finalization_StaffOverrideStillSetsLinePrice()
    {
        var services = _factory.Services;
        var email = $"pricing-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email);
        await TestSeed.VerifyCustomerAsync(services, email);

        var productId = await SeedCustomizableProductAsync("Override Jersey", 2750.5m);

        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();
        var (orderId, _) = await SubmitCustomOrderAsync(customer, productId, quantity: 2);

        // Staff finalize: negotiated unit price 9,999 replaces the catalog snapshot;
        // plus a custom charge line and delivery fee.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var adminHtml = await (await owner.GetAsync($"/admin/orders/{orderId}/workspace"))
            .Content.ReadAsStringAsync();
        var finalize = await owner.PostAsync($"/admin/orders/{orderId}/workspace/finalize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [$"line_{productId}_qty"] = "2",
                [$"line_{productId}_price"] = "9999",
                ["deliveryFee"] = "1500",
                ["discount"] = "0",
                ["tax"] = "0",
                ["custom_desc"] = "Design preparation",
                ["custom_price"] = "5000",
                ["custom_qty"] = "1",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(adminHtml)
            }));
        finalize.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var order = await db.Orders.Include(o => o.Items).Include(o => o.CustomLines)
            .FirstAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.Items.Single().UnitPrice.Amount.Should().Be(9999m);
        order.GrandTotal.Amount.Should().Be(26498m);         // 2×9,999 + 5,000 + 1,500
    }

    [Fact]
    public async Task CartCheckout_MultipleProductsAndQuantities_SnapshotCorrectPrices()
    {
        var services = _factory.Services;
        var email = $"pricing-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email);
        await TestSeed.VerifyCustomerAsync(services, email);

        var mugId = await TestSeed.SeedProductAsync(services, "Pricing Mug", 1200m);
        var bannerId = await TestSeed.SeedProductAsync(services, "Pricing Banner", 4500.25m);

        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        foreach (var (productId, qty) in new[] { (mugId, 2), (bannerId, 1) })
        {
            var page = await customer.GetAsync($"/portal/shop/{productId}");
            var pageHtml = await page.Content.ReadAsStringAsync();
            var token = TestSeed.ExtractAntiforgeryToken(pageHtml);
            var add = await customer.PostAsync($"/portal/shop/{productId}/add-to-cart",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["quantity"] = qty.ToString(),
                    ["__RequestVerificationToken"] = token
                }));
            add.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        var checkoutPage = await customer.GetAsync("/checkout");
        var checkoutHtml = await checkoutPage.Content.ReadAsStringAsync();
        var place = await customer.PostAsync("/checkout", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Fulfilment"] = "pickup",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(checkoutHtml)
            }));
        place.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var order = await db.Orders.Include(o => o.Items)
            .OrderByDescending(o => o.CreatedDateUtc).FirstAsync();

        var mug = order.Items.Single(i => i.ProductId == mugId);
        var banner = order.Items.Single(i => i.ProductId == bannerId);
        mug.UnitPrice.Amount.Should().Be(1200m);
        mug.Quantity.Should().Be(2);
        mug.LineTotal.Amount.Should().Be(2400m);
        banner.UnitPrice.Amount.Should().Be(4500.25m);
        banner.Quantity.Should().Be(1);
        banner.LineTotal.Amount.Should().Be(4500.25m);
        order.ItemsSubtotal.Amount.Should().Be(6900.25m);
        order.GrandTotal.Amount.Should().Be(order.ItemsSubtotal.Amount
            + order.DeliveryFee.Amount - order.DiscountAmount.Amount + order.TaxAmount.Amount);
    }
}
