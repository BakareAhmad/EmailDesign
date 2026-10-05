using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Inventory-value-on-order regression (owner directive 2026-09-08: "confirm if
/// inventory value isn't affected upon a successful order and fix"). Before the fix
/// the dashboard KPI valued raw QuantityOnHand, which only deducts when staff advance
/// the order to InProduction - a successful (paid) order merely RESERVES stock, so the
/// ₦ value never moved at order success. The KPI now values Available stock
/// (OnHand minus Reserved), matching every other surface in the app.
/// </summary>
public class InventoryValueOnOrderTests : IClassFixture<CustomWebApplicationFactory>
{
    private const decimal SellingPrice = 2000m;
    private const int OpeningStock = 10;

    private readonly CustomWebApplicationFactory _factory;

    public InventoryValueOnOrderTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Successful_order_reduces_inventory_value_and_production_start_does_not_double_deduct()
    {
        // Setup: a product with a known price and an inventory record (10 units).
        Guid productId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var category = Category.Create("Value Test Category");
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            var product = Product.Create("Value Test Mug", "VT-MUG-001", null, category.Id, SellingPrice);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            db.Inventories.Add(Inventory.Create(product.Id, product.Name, OpeningStock));
            await db.SaveChangesAsync();
            productId = product.Id;
        }

        var client = await LoginAsOwnerAsync();

        var before = await InventoryValueAsync(client);
        before.Should().Be(OpeningStock * SellingPrice,
            "only the test product carries inventory in this fresh database");

        // A paid walk-in order for 3 units: the confirmation pipeline reserves the stock.
        var page = await (await client.GetAsync("/admin/orders/new")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/orders/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Value",
            ["NewLastName"] = "Check",
            ["Fulfilment"] = "pickup",
            ["Products[0].ProductId"] = productId.ToString(),
            ["Products[0].Quantity"] = "3",
            ["PaymentMethod"] = "Cash",
            ["PaymentNote"] = "cash at counter",
            ["Discount"] = "0",
            ["Tax"] = "0",
            ["__RequestVerificationToken"] = token
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the paid order must be created");
        var orderLocation = response.Headers.Location!.ToString();

        // The reservation committed 3 units at payment time.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
            inv.QuantityOnHand.Should().Be(OpeningStock, "on-hand only deducts at production start");
            inv.QuantityReserved.Should().Be(3, "the successful order reserved its units");
        }

        // THE FIX: the dashboard inventory value dropped by 3 x price at order success.
        var afterPayment = await InventoryValueAsync(client);
        afterPayment.Should().Be(before - 3 * SellingPrice,
            "a successful order must reduce the inventory value immediately (owner directive 2026-09-08)");

        // Advancing to InProduction deducts on-hand and releases the reservation:
        // Available (and therefore the value) must stay at the reduced level, not drop twice.
        var orderId = Regex.Match(orderLocation, "/admin/orders/([0-9a-fA-F-]{36})").Groups[1].Value;
        var orderPage = await (await client.GetAsync(orderLocation)).Content.ReadAsStringAsync();
        var statusToken = Regex.Match(orderPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var statusResp = await client.PostAsync($"/admin/orders/{orderId}/status", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Status"] = "InProduction",
            ["__RequestVerificationToken"] = statusToken
        }));
        statusResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "the status transition must succeed");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
            inv.QuantityOnHand.Should().Be(OpeningStock - 3, "fulfilment stock-out deducts on-hand");
            inv.QuantityReserved.Should().Be(0, "the reservation is released by the stock-out");
        }

        var afterProduction = await InventoryValueAsync(client);
        afterProduction.Should().Be(afterPayment,
            "production start must not deduct the value a second time");
    }

    private static async Task<decimal> InventoryValueAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/admin/dashboard")).Content.ReadAsStringAsync();
        var raw = Regex.Match(html, "Inventory value</span>[\\s\\S]*?&#8358;([\\d,]+\\.\\d{2})").Groups[1].Value;
        raw.Should().NotBeEmpty("the dashboard must render the inventory value KPI");
        return decimal.Parse(raw.Replace(",", ""));
    }

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var ok = await TryOwnerLoginAsync(client, "ChangeMe123!");
        if (!ok)
            ok = await TryOwnerLoginAsync(client, "Owner!Pass9");
        ok.Should().BeTrue("owner login should succeed with the bootstrap or the changed password");
        return client;
    }

    private async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "owner@dechimexprinthub.com",
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        if (response.StatusCode != HttpStatusCode.Redirect)
            return false;

        var location = response.Headers.Location?.ToString() ?? "";
        if (location.Contains("/admin/security/password"))
        {
            var pwHtml = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
            var pwToken = Regex.Match(pwHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var pwResponse = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["CurrentPassword"] = password,
                ["NewPassword"] = "Owner!Pass9",
                ["ConfirmPassword"] = "Owner!Pass9",
                ["__RequestVerificationToken"] = pwToken
            }));
            pwResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }
        return true;
    }
}
