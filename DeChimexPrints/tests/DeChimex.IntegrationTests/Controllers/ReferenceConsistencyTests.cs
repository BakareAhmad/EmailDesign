using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Activity;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Invoices;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using DeChimex.Persistence.Queries;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Reference-format consistency fixes (placeholder audit follow-up, 2026-10-04):
///  * quotes run on their own QTE- sequence and are renumbered onto INV- when an order
///    adopts them (a paid document must always read INV-…);
///  * procurement links an order by its business number (DCX-…) or a raw GUID — never
///    asking staff to paste database ids;
///  * the business-activity free-text search matches the linked order number, not just
///    title/summary text.
/// </summary>
public class ReferenceConsistencyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ReferenceConsistencyTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient OwnerBrowser() =>
        TestSeed.LoginOwnerMvcAsync(_factory.Services, () => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }))
            .GetAwaiter().GetResult();

    private static string FormToken(string html) =>
        Regex.Match(html, "name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"([^\\\"]+)\\\"").Groups[1].Value;

    private async Task<(HttpStatusCode Status, string Location)> PostFormAsync(
        HttpClient client, string path, Dictionary<string, string> fields)
    {
        var page = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
        fields["__RequestVerificationToken"] = FormToken(page);
        var res = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        return (res.StatusCode, res.Headers.Location?.ToString() ?? "");
    }

    private async Task<Guid> SeedProductAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create($"Ref Cat {Guid.NewGuid():N}");
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var product = Product.Create(name, "REF-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            "reference-consistency test product", category.Id, 2500m);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    [Fact]
    public async Task Quote_RunsOnQteSequence_AndAdoptionRenumbersOntoInv()
    {
        var owner = OwnerBrowser();
        var productId = await SeedProductAsync("Quote Seq Product");

        var (status, quoteUrl) = await PostFormAsync(owner, "/admin/invoices/new", new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Sequence",
            ["NewLastName"] = "Quote",
            ["Lines[0].ProductId"] = productId.ToString(),
            ["Lines[0].Quantity"] = "1",
            ["Discount"] = "0",
            ["Tax"] = "0"
        });
        status.Should().Be(HttpStatusCode.Redirect, "a valid quote must be created");
        var quoteId = Regex.Match(quoteUrl, @"/admin/invoices/([0-9a-fA-F-]{36})").Groups[1].Value;

        var quoteDoc = await (await owner.GetAsync(quoteUrl)).Content.ReadAsStringAsync();
        var qteNumber = Regex.Match(quoteDoc, "QTE-\\d{4}-\\d{6}").Value;
        qteNumber.Should().NotBeEmpty("quote-only documents must carry the QTE- sequence");
        quoteDoc.Should().Contain("/admin/orders/new?invoice=", "the quote offers conversion");

        // Convert: the adopted document becomes the order's invoice and must renumber to INV-.
        var customerId = (await owner.GetAsync("/admin/customers/picker?q=Sequence")).Content
            .ReadAsStringAsync().GetAwaiter().GetResult() is var pickerJson ? pickerJson : "";
        using (var doc = System.Text.Json.JsonDocument.Parse(customerId))
            customerId = doc.RootElement[0].GetProperty("id").GetString()!;

        var (orderStatus, orderLocation) = await PostFormAsync(owner, "/admin/orders/new", new Dictionary<string, string>
        {
            ["InvoiceId"] = quoteId,
            ["CustomerMode"] = "existing",
            ["CustomerId"] = customerId,
            ["Fulfilment"] = "pickup",
            ["Products[0].ProductId"] = productId.ToString(),
            ["Products[0].Quantity"] = "1",
            ["Discount"] = "0",
            ["Tax"] = "0"
        });
        orderStatus.Should().Be(HttpStatusCode.Redirect, "placing the order from the quote must succeed");

        var orderDoc = await (await owner.GetAsync(orderUrl(orderLocation))).Content.ReadAsStringAsync();
        var orderNumber = Regex.Match(orderDoc, "DCX-\\d{4}-\\d{6}").Value;
        orderNumber.Should().NotBeEmpty();

        var invoiceDoc = await (await owner.GetAsync(quoteUrl)).Content.ReadAsStringAsync();
        invoiceDoc.Should().MatchRegex("INV-\\d{4}-\\d{6}",
            "the adopted quote is renumbered onto the invoice sequence");
        invoiceDoc.Should().NotContain(qteNumber,
            "a paid-invoice document must no longer read as a QTE- quote");
        invoiceDoc.Should().Contain(orderNumber, "the invoice links its order");

        // The INV counter must not have been consumed by the quote: the next plain order's
        // invoice number continues the INV- sequence without a gap for the quote.
        var (plainStatus, plainLocation) = await PostFormAsync(owner, "/admin/orders/new", new Dictionary<string, string>
        {
            ["CustomerMode"] = "existing",
            ["CustomerId"] = customerId,
            ["Fulfilment"] = "pickup",
            ["Products[0].ProductId"] = productId.ToString(),
            ["Products[0].Quantity"] = "1",
            ["Discount"] = "0",
            ["Tax"] = "0"
        });
        plainStatus.Should().Be(HttpStatusCode.Redirect);
        var plainDoc = await (await owner.GetAsync(orderUrl(plainLocation))).Content.ReadAsStringAsync();
        var plainInvoice = Regex.Match(plainDoc, "INV-\\d{4}-\\d{6}").Value;
        plainInvoice.Should().NotBeEmpty();
        var adoptedInvoice = Regex.Match(invoiceDoc, "INV-\\d{4}-\\d{6}").Value;
        int.Parse(plainInvoice[^6..]).Should().Be(int.Parse(adoptedInvoice[^6..]) + 1,
            "quotes must not consume invoice numbers (gap-free INV sequence)");
    }

    [Fact]
    public async Task Procurement_LinksOrderByBusinessNumber_OrGuid_AndRejectsUnknown()
    {
        var owner = OwnerBrowser();
        var productId = await SeedProductAsync("Procurement Link Product");

        var (_, orderLocation) = await PostFormAsync(owner, "/admin/orders/new", new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Procure",
            ["NewLastName"] = "Link",
            ["Fulfilment"] = "pickup",
            ["Products[0].ProductId"] = productId.ToString(),
            ["Products[0].Quantity"] = "2",
            ["Discount"] = "0",
            ["Tax"] = "0"
        });
        var orderDoc = await (await owner.GetAsync(orderLocation)).Content.ReadAsStringAsync();
        var orderNumber = Regex.Match(orderDoc, "DCX-\\d{4}-\\d{6}").Value;
        var orderId = Regex.Match(orderLocation, @"/admin/orders/([0-9a-fA-F-]{36})").Groups[1].Value;

        // 1. By business number — what every surface shows.
        var byNumber = await PostFormAsync(owner, "/admin/procurement/create", new Dictionary<string, string>
        {
            ["SourceOrderNumber"] = orderNumber,
            ["Items[0].ProductId"] = productId.ToString(),
            ["Items[0].Quantity"] = "5",
            ["Items[0].UnitCost"] = "300"
        });
        byNumber.Status.Should().Be(HttpStatusCode.Redirect, "the DCX- order number must link the order");
        var details = await (await owner.GetAsync(byNumber.Location)).Content.ReadAsStringAsync();
        details.Should().Contain(orderNumber, "the procurement document shows the linked order's number");
        details.Should().Contain($"/admin/orders/{orderId}", "the linked order is one click away");

        // 2. By raw GUID — back-compat for anything that saved the old format.
        var byGuid = await PostFormAsync(owner, "/admin/procurement/create", new Dictionary<string, string>
        {
            ["SourceOrderNumber"] = orderId,
            ["Items[0].ProductId"] = productId.ToString(),
            ["Items[0].Quantity"] = "1",
            ["Items[0].UnitCost"] = "300"
        });
        byGuid.Status.Should().Be(HttpStatusCode.Redirect, "a raw order GUID must still resolve");

        // 3. Unknown reference is a friendly validation error, not a crash.
        var unknown = await PostFormAsync(owner, "/admin/procurement/create", new Dictionary<string, string>
        {
            ["SourceOrderNumber"] = "NOPE-123",
            ["Items[0].ProductId"] = productId.ToString(),
            ["Items[0].Quantity"] = "1",
            ["Items[0].UnitCost"] = "300"
        });
        unknown.Status.Should().Be(HttpStatusCode.OK, "invalid input re-renders the form");
        var unknownPage = await (await owner.GetAsync("/admin/procurement/create")).Content.ReadAsStringAsync();
        unknownPage.Should().Contain("e.g. DCX-2026-000042",
            "the placeholder teaches the business order-number format");
    }

    [Fact]
    public async Task ActivitySearch_MatchesTheLinkedOrderNumber()
    {
        var owner = OwnerBrowser();
        var productId = await SeedProductAsync("Activity Search Product");

        var (_, orderLocation) = await PostFormAsync(owner, "/admin/orders/new", new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Activity",
            ["NewLastName"] = "Search",
            ["Fulfilment"] = "pickup",
            ["Products[0].ProductId"] = productId.ToString(),
            ["Products[0].Quantity"] = "1",
            ["Discount"] = "0",
            ["Tax"] = "0"
        });
        var orderId = Regex.Match(orderLocation, @"/admin/orders/([0-9a-fA-F-]{36})").Groups[1].Value;
        var orderDoc = await (await owner.GetAsync(orderLocation)).Content.ReadAsStringAsync();
        var orderNumber = Regex.Match(orderDoc, "DCX-\\d{4}-\\d{6}").Value;
        orderNumber.Should().NotBeEmpty();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var queries = new ActivityQueries(db);

        var byNumber = await queries.SearchAsync(new ActivityFilter { Search = orderNumber }, default);
        byNumber.Items.Should().Contain(i => i.EntityId == orderId,
            "the free-text search matches the linked order's number, not just title/summary text");

        // An unambiguous non-matching number finds nothing.
        var byNone = await queries.SearchAsync(new ActivityFilter { Search = "DCX-1999-999999" }, default);
        byNone.Items.Should().NotContain(i => i.EntityId == orderId);
    }

    [Fact]
    public async Task RemovedPrototypeUiScript_IsNoLongerServed()
    {
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/js/ui.js")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the unreferenced dev-prototype ui.js was removed; nothing loads it");
    }

    private static string orderUrl(string location) => location.StartsWith("http")
        ? new Uri(location).PathAndQuery
        : location;
}
