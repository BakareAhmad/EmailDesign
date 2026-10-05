using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Share-link origin regression (owner report 2026-09-08): the copyable invoice /
/// receipt share URLs on the admin pages (and the public document bars) were bare
/// relative paths ("/r/{token}", "/i/{token}") built from Request.PathBase, so a
/// copied link did not resolve anywhere. They must carry the environment's base URL
/// (App:FrontendBaseUrl — same origin rule as email links, §53): https://localhost:7061
/// in Development, https://dechimexprinthub.com in Production.
/// </summary>
public class PublicShareLinkOriginTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PublicShareLinkOriginTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed class DevShareFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }
    }

    private sealed class ProdShareFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public void Development_share_links_carry_the_local_origin()
    {
        using var factory = new DevShareFactory();
        using var scope = factory.Services.CreateScope();
        var urls = scope.ServiceProvider.GetRequiredService<IAppUrlProvider>();

        urls.BuildPublicInvoiceLink("abc123").Should()
            .Be("https://localhost:7061/i/abc123",
                "copied invoice links must resolve on the developer's machine (https launch profile)");
        urls.BuildPublicReceiptLink("abc123").Should()
            .Be("https://localhost:7061/r/abc123",
                "copied receipt links must resolve on the developer's machine (https launch profile)");
    }

    [Fact]
    public void Production_share_links_carry_the_public_domain()
    {
        using var factory = new ProdShareFactory();
        using var scope = factory.Services.CreateScope();
        var urls = scope.ServiceProvider.GetRequiredService<IAppUrlProvider>();

        urls.BuildPublicInvoiceLink("abc123").Should().Be("https://dechimexprinthub.com/i/abc123");
        urls.BuildPublicReceiptLink("abc123").Should().Be("https://dechimexprinthub.com/r/abc123");
    }

    [Fact]
    public async Task Admin_invoice_view_and_public_page_show_absolute_share_urls()
    {
        var client = await LoginAsOwnerAsync();
        var serviceId = await CreateServiceAsync(client, "Share Invoice Service");

        // Quote invoice with a share token, created through the domain (setup, not the flow under test).
        // Invoice lines must reference a catalog product or service, hence the service above.
        Guid invoiceId;
        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var customer = Customer.Create("Share", "Link");
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var invoice = Invoice.CreateQuote("INV-SHARE-0001", customer.Id, "Share Link",
                new List<InvoiceLineSpec> { new(null, serviceId, "Banner print", 1500m, 1, null) }, 0m, 0m, null);
            token = Guid.NewGuid().ToString("N");
            invoice.SetShareToken(token);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            invoiceId = invoice.Id;
        }

        var adminPage = await (await client.GetAsync($"/admin/invoices/{invoiceId}")).Content.ReadAsStringAsync();
        adminPage.Should().Contain($"value=\"https://localhost:7061/i/{token}\"",
            "the admin copy-box must show a fully-qualified link, not a bare /i/ path");
        adminPage.Should().Contain($"href=\"https://localhost:7061/i/{token}\"", "the Open button must open the absolute link");

        var publicPage = await (await client.GetAsync($"/i/{token}")).Content.ReadAsStringAsync();
        publicPage.Should().Contain($"https://localhost:7061/i/{token}",
            "the public document bar must offer the fully-qualified link too");
    }

    [Fact]
    public async Task Admin_receipt_view_shows_absolute_share_url_after_cash_payment()
    {
        var client = await LoginAsOwnerAsync();
        var serviceId = await CreateServiceAsync(client, "Share Receipt Service");

        // A paid walk-in order runs the real confirmation pipeline, which issues the
        // receipt AND its share token (PaymentConfirmationService.SetShareToken).
        var page = await (await client.GetAsync("/admin/orders/new")).Content.ReadAsStringAsync();
        var formToken = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/orders/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Receipt",
            ["NewLastName"] = "Share",
            ["Fulfilment"] = "pickup",
            ["Services[0].ServiceId"] = serviceId.ToString(),
            ["Services[0].Quantity"] = "1",
            ["PaymentMethod"] = "Cash",
            ["PaymentNote"] = "cash received at the counter",
            ["Discount"] = "0",
            ["Tax"] = "0",
            ["__RequestVerificationToken"] = formToken
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the paid counter order must be created");

        Guid receiptId;
        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var receipt = await db.Receipts.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ShareToken != null);
            receipt.Should().NotBeNull("the payment confirmation pipeline issues a receipt with a share token");
            receiptId = receipt!.Id;
            token = receipt.ShareToken!;
        }

        var adminPage = await (await client.GetAsync($"/admin/receipts/{receiptId}")).Content.ReadAsStringAsync();
        adminPage.Should().Contain($"value=\"https://localhost:7061/r/{token}\"",
            "the admin copy-box must show a fully-qualified link, not the bare /r/ path from the owner's screenshot");
        adminPage.Should().Contain($"href=\"https://localhost:7061/r/{token}\"", "the Open button must open the absolute link");

        var publicPage = await (await client.GetAsync($"/r/{token}")).Content.ReadAsStringAsync();
        publicPage.Should().Contain($"https://localhost:7061/r/{token}");
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
