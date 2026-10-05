using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeChimex.Domain.Entities;
using DeChimex.Infrastructure.Persistence;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Walk-in / counter order flows (2026-08-28):
/// guest customers without an email, inline payment at order creation, the
/// uncapped searchable customer picker, and portal visibility of counter orders
/// placed for existing customers.
/// </summary>
public class WalkInOrderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public WalkInOrderTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task WalkInOrder_NewCustomerWithoutEmail_CreatesOrderAndGuestCustomer()
    {
        var client = await LoginAsOwnerAsync();
        var serviceId = await CreateServiceAsync(client, "Guest Counter Service");

        // Counter order for a brand-new customer: names only, no email, pickup.
        var (status, location) = await PostOrderAsync(client, new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Walk",
            ["NewLastName"] = "In",
            ["NewPhone"] = "08030001111",
            ["Fulfilment"] = "pickup",
            ["Services[0].ServiceId"] = serviceId.ToString(),
            ["Services[0].Quantity"] = "1"
        });
        status.Should().Be(HttpStatusCode.Redirect, "a valid counter order must be created");
        location.Should().Contain("/admin/orders/");
        var details = await (await client.GetAsync(location)).Content.ReadAsStringAsync();
        details.Should().Contain("Payment Required", "no payment was taken, so the order waits for Mark as paid");
        details.Should().Contain("Walk In", "the guest customer is linked to the order");

        // The customer exists and is flagged as a guest (no email, no portal account).
        var customers = await (await client.GetAsync("/admin/customers?search=Walk")).Content.ReadAsStringAsync();
        customers.Should().Contain("Walk In");
        customers.Should().Contain("badge badge-neutral\">Guest<");
    }

    [Fact]
    public async Task WalkInOrder_WithInlineCashOrder_PaysAndIssuesInvoice()
    {
        var client = await LoginAsOwnerAsync();
        var serviceId = await CreateServiceAsync(client, "Cash Counter Service");

        var (status, location) = await PostOrderAsync(client, new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Cash",
            ["NewLastName"] = "Walk",
            ["Fulfilment"] = "pickup",
            ["Services[0].ServiceId"] = serviceId.ToString(),
            ["Services[0].Quantity"] = "1",
            ["PaymentMethod"] = "Cash",
            ["PaymentNote"] = "cash received at the counter"
        });
        status.Should().Be(HttpStatusCode.Redirect);

        var details = await (await client.GetAsync(location)).Content.ReadAsStringAsync();
        details.Should().Contain("In Production", "the inline payment takes the order straight to Paid");
        details.Should().Contain("Succeeded", "the payment record is marked succeeded");
        details.Should().Contain("cash received at the counter");
        details.Should().NotContain("/mark-paid", "no further Mark-as-paid step is offered");

        // The invoice was issued by the same confirmation pipeline.
        var orderNumber = Regex.Match(details, "DCX-\\d{4}-\\d{6}").Value;
        var invoices = await (await client.GetAsync($"/admin/invoices?search={orderNumber}")).Content.ReadAsStringAsync();
        invoices.Should().Contain("INV-", "the paid counter order must have an invoice");
    }

    [Fact]
    public async Task CustomerPicker_FindsCustomersBeyondTheFirstHundred()
    {
        // Seed 120 customers directly in the database (test setup, not the flow under test):
        // N110 is a guest (no email), the rest have emails.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            for (var i = 1; i <= 120; i++)
            {
                var isGuest = i == 110;
                db.Customers.Add(Customer.Create("Cust", $"N{i:000}", isGuest ? null : $"cust{i:000}@example.com"));
            }
            await db.SaveChangesAsync();
        }

        var client = await LoginAsOwnerAsync();

        // A customer well past the old 100-record cap is found by search.
        var json = await (await client.GetAsync("/admin/customers/picker?q=N115")).Content.ReadAsStringAsync();
        json.Should().Contain("Cust N115");

        // The guest (no email) is found by name, and flagged as guest in the feed.
        var guestJson = await (await client.GetAsync("/admin/customers/picker?q=N110")).Content.ReadAsStringAsync();
        guestJson.Should().Contain("Cust N110");
        guestJson.Should().Contain("\"guest\":true");

        // An unqualified query returns a bounded preview (25), not the whole directory.
        using var doc = JsonDocument.Parse(await (await client.GetAsync("/admin/customers/picker")).Content.ReadAsStringAsync());
        doc.RootElement.GetArrayLength().Should().BeLessOrEqualTo(25);
    }

    [Fact]
    public async Task WalkInOrder_ForExistingCustomer_AppearsInTheirPortal()
    {
        // 1. Register a customer through the public portal (clean browser context).
        var shopper = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        const string shopperEmail = "portal.walkin@example.com";
        var regPage = await (await shopper.GetAsync("/Account/Register")).Content.ReadAsStringAsync();
        var regToken = Regex.Match(regPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var reg = await shopper.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["FirstName"] = "Portal",
            ["LastName"] = "Walkin",
            ["Email"] = shopperEmail,
            ["Phone"] = "08012345678",
            ["Password"] = "Pass1234!",
            ["ConfirmPassword"] = "Pass1234!",
            ["__RequestVerificationToken"] = regToken
        }));
        reg.StatusCode.Should().Be(HttpStatusCode.Redirect, "registration should succeed");
        await TestSeed.VerifyCustomerAsync(_factory.Services, shopperEmail); // gated signup: token from the Brevo email

        // 2. The owner finds that customer through the picker and creates a counter order.
        var owner = await LoginAsOwnerAsync();
        var ownerServiceId = await CreateServiceAsync(owner, "Portal Counter Service");
        var picker = await (await owner.GetAsync($"/admin/customers/picker?q={shopperEmail}")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(picker);
        doc.RootElement.GetArrayLength().Should().Be(1);
        var customerId = doc.RootElement[0].GetProperty("id").GetString();
        customerId.Should().NotBeNullOrWhiteSpace();

        var (status, location) = await PostOrderAsync(owner, new Dictionary<string, string>
        {
            ["CustomerMode"] = "existing",
            ["CustomerId"] = customerId,
            ["Fulfilment"] = "pickup",
            ["Services[0].ServiceId"] = ownerServiceId.ToString(),
            ["Services[0].Quantity"] = "2"
        });
        status.Should().Be(HttpStatusCode.Redirect);
        var orderNumber = Regex.Match(await (await owner.GetAsync(location)).Content.ReadAsStringAsync(),
            "DCX-\\d{4}-\\d{6}").Value;
        orderNumber.Should().NotBeEmpty();

        // 3. The customer signs in (or is already signed in from registration) and
        //    sees the counter order in their portal.
        var portalResponse = await shopper.GetAsync("/portal/orders");
        var portalOrders = await portalResponse.Content.ReadAsStringAsync();
        if (portalResponse.StatusCode != HttpStatusCode.OK)
        {
            var loginPage = await (await shopper.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
            var loginToken = Regex.Match(loginPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var login = await shopper.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = shopperEmail,
                ["Password"] = "Pass1234!",
                ["__RequestVerificationToken"] = loginToken
            }));
            login.StatusCode.Should().Be(HttpStatusCode.Redirect);
            portalOrders = await (await shopper.GetAsync("/portal/orders")).Content.ReadAsStringAsync();
        }
        portalOrders.Should().Contain(orderNumber, "a counter order for an existing customer must show in their portal");
    }

    // ---- helpers ----

    private async Task<(HttpStatusCode, string)> PostOrderAsync(HttpClient client, Dictionary<string, string> fields)
    {
        var page = await (await client.GetAsync("/admin/orders/new")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        fields["__RequestVerificationToken"] = token;
        fields["Discount"] = "0";
        fields["Tax"] = "0";
        var response = await client.PostAsync("/admin/orders/new", new FormUrlEncodedContent(fields));
        return (response.StatusCode, response.Headers.Location?.ToString() ?? "");
    }

    private async Task<Guid> CreateServiceAsync(HttpClient owner, string name)
    {
        var page = await (await owner.GetAsync("/admin/services/create")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await owner.PostAsync("/admin/services/create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = name,
            ["BasePrice"] = "1500",
            ["__RequestVerificationToken"] = token
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, $"service {name} must be created");

        var list = await (await owner.GetAsync("/admin/services")).Content.ReadAsStringAsync();
        var row = Regex.Split(list, "<tr").FirstOrDefault(p => p.Contains($">{name}</td>"));
        row.Should().NotBeNull($"the services list should contain {name}");
        return Guid.Parse(Regex.Match(row!, "href=\"/admin/services/edit/([0-9a-fA-F-]{36})\"").Groups[1].Value);
    }

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // The seeded owner is forced to change the bootstrap password on first login.
        // An earlier test in this class may already have done that, so fall back to the
        // known changed password when the bootstrap one no longer works.
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
        if (response.StatusCode != System.Net.HttpStatusCode.Redirect)
            return false;

        var location = response.Headers.Location?.ToString() ?? "";
        if (location.Contains("/admin/security/password"))
            await CompleteForcedPasswordChangeAsync(client, password);
        return true;
    }

    private static async Task CompleteForcedPasswordChangeAsync(HttpClient client, string currentPassword)
    {
        var html = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPassword"] = currentPassword,
            ["NewPassword"] = "Owner!Pass9",
            ["ConfirmPassword"] = "Owner!Pass9",
            ["__RequestVerificationToken"] = token
        }));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
    }
}
