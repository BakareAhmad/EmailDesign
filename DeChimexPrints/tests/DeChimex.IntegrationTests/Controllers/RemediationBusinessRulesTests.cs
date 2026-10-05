using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Business-rule alignment (2026-08-29, updated 2026-09-01): the WhatsApp setting
/// stores a normalized number with the wa.me link generated on the consumer side,
/// invoices have a downloadable QuestPDF PDF, and pay on delivery is gone from every
/// surface. The quotation zero/negative-unit-price guard that lived here was removed
/// with the Quotation feature; the equivalent rule is now enforced on the invoice
/// "quote invoice" path by CreateQuoteInvoiceValidator (Lines.NotEmpty, Quantity > 0,
/// non-negative discount/tax) and by Invoice.CreateQuote (non-negative grand total).
/// </summary>
public class RemediationBusinessRulesTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RemediationBusinessRulesTests(CustomWebApplicationFactory factory) => _factory = factory;

    private const string OwnerEmail = "owner@dechimexprinthub.com";
    private const string BootstrapPassword = "ChangeMe123!";
    private const string ChangedPassword = "Owner!Pass9";

    [Fact]
    public async Task WhatsApp_Setting_StoresNormalizedNumber_And_PagesGenerateLink()
    {
        var owner = await LoginAsOwnerAsync();

        // Save a legacy wa.me URL; the server must store only the number.
        var settingsPage = await (await owner.GetAsync("/admin/settings")).Content.ReadAsStringAsync();
        var settingsToken = ExtractToken(settingsPage);
        var save = await owner.PostAsync("/admin/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["values[business.whatsapp]"] = "https://wa.me/2348123456789?text=hello",
            ["__RequestVerificationToken"] = settingsToken
        }));
        save.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var afterSave = await (await owner.GetAsync("/admin/settings")).Content.ReadAsStringAsync();
        var waInput = Regex.Match(afterSave, "<input[^>]*id=\"inp_wa\"[^>]*>").Value;
        waInput.Should().Contain("value=\"2348123456789\"", "the admin input should show the normalized number");
        waInput.Should().NotContain("wa.me", "the admin input must show a number, not a URL");

        // The public contact page generates the wa.me link from the stored number.
        var anon = _factory.CreateClient();
        var contact = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        contact.Should().Contain("https://wa.me/2348123456789");

        // A local 0-prefixed number is normalized to the 234 country code.
        var settingsPage2 = await (await owner.GetAsync("/admin/settings")).Content.ReadAsStringAsync();
        var settingsToken2 = ExtractToken(settingsPage2);
        await owner.PostAsync("/admin/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["values[business.whatsapp]"] = "0812 345 6789",
            ["__RequestVerificationToken"] = settingsToken2
        }));
        var contact2 = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        contact2.Should().Contain("https://wa.me/2348123456789");
    }

    [Fact]
    public async Task Invoice_Pdf_Downloads_For_SharedInvoice_And_404s_Unknown_Token()
    {
        var owner = await LoginAsOwnerAsync();

        // Build a paid order (service line) so an invoice with a share token exists.
        var serviceId = await CreateServiceAsync(owner, "PDF Test Service");
        var customerEmail = $"pdf-{Guid.NewGuid():N}@example.com";
        await RegisterCustomerAsync(customerEmail);

        var picker = await (await owner.GetAsync($"/admin/customers/picker?q={Uri.EscapeDataString(customerEmail)}")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(picker);
        var customerId = doc.RootElement[0].GetProperty("id").GetString();
        customerId.Should().NotBeNullOrWhiteSpace();

        var orderPage = await (await owner.GetAsync("/admin/orders/new")).Content.ReadAsStringAsync();
        var orderToken = ExtractToken(orderPage);
        var orderPost = await owner.PostAsync("/admin/orders/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CustomerMode"] = "existing",
            ["CustomerId"] = customerId,
            ["Fulfilment"] = "pickup",
            ["Services[0].ServiceId"] = serviceId.ToString(),
            ["Services[0].Quantity"] = "1",
            ["Discount"] = "0",
            ["Tax"] = "0",
            ["__RequestVerificationToken"] = orderToken
        }));
        orderPost.StatusCode.Should().Be(HttpStatusCode.Redirect, "the test order should be created");

        // Mark it paid so the invoice moves through the pipeline.
        var location = orderPost.Headers.Location!.ToString();
        var orderId = Guid.Parse(Regex.Match(location, "([0-9a-fA-F-]{36})").Groups[1].Value);
        var detailsPage = await (await owner.GetAsync($"/admin/orders/{orderId}")).Content.ReadAsStringAsync();
        var payToken = ExtractToken(detailsPage);
        var paid = await owner.PostAsync($"/admin/orders/{orderId}/mark-paid", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Method"] = "Cash",
            ["Note"] = "PDF test",
            ["__RequestVerificationToken"] = payToken
        }));
        paid.StatusCode.Should().Be(HttpStatusCode.Redirect, "the order should be marked paid");

        // Find the invoice through the admin list and grab the PDF link from its view.
        var invoices = await (await owner.GetAsync("/admin/invoices")).Content.ReadAsStringAsync();
        var invoiceHref = Regex.Match(invoices, "href=\"(/admin/invoices/[0-9a-fA-F-]{36})\"").Groups[1].Value;
        invoiceHref.Should().NotBeEmpty("a paid order should have produced an invoice");

        var invoiceView = await (await owner.GetAsync(invoiceHref)).Content.ReadAsStringAsync();
        // The PDF link rides on the absolute share URL (owner directive 2026-09-08);
        // the in-process client fetches the path part.
        var pdfHref = Regex.Match(invoiceView, "href=\"(https?://[^\"]+/pdf)\"").Groups[1].Value;
        pdfHref.Should().NotBeEmpty("the invoice view should expose a Download PDF link");

        var pdf = await owner.GetAsync(new Uri(pdfHref).PathAndQuery);
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(1000);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");

        var missing = await owner.GetAsync("/i/badtoken/pdf");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PayOnDelivery_Is_Gone_From_All_Surfaces()
    {
        var owner = await LoginAsOwnerAsync();

        var newOrder = await (await owner.GetAsync("/admin/orders/new")).Content.ReadAsStringAsync();
        newOrder.Should().NotContain("PayOnDelivery");
        newOrder.Should().NotContain("Pay on delivery");

        var anon = _factory.CreateClient();
        var terms = await (await anon.GetAsync("/terms")).Content.ReadAsStringAsync();
        terms.Should().NotContain("Pay on delivery");
        var privacy = await (await anon.GetAsync("/privacy")).Content.ReadAsStringAsync();
        privacy.Should().NotContain("pay on delivery");
    }

    // ---- helpers ----

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = CreateBrowser();
        var ok = await TryOwnerLoginAsync(client, BootstrapPassword);
        if (!ok)
            ok = await TryOwnerLoginAsync(client, ChangedPassword);
        ok.Should().BeTrue("owner login should succeed");
        return client;
    }

    private async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = ExtractToken(html);
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = OwnerEmail,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        if (response.StatusCode != HttpStatusCode.Redirect)
            return false;

        var location = response.Headers.Location?.ToString() ?? "";
        if (location.Contains("/admin/security/password"))
            await CompleteForcedPasswordChangeAsync(client, password);
        return true;
    }

    private static async Task CompleteForcedPasswordChangeAsync(HttpClient client, string currentPassword)
    {
        var html = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
        var token = ExtractToken(html);
        var response = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPassword"] = currentPassword,
            ["NewPassword"] = ChangedPassword,
            ["ConfirmPassword"] = ChangedPassword,
            ["__RequestVerificationToken"] = token
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    private async Task<Guid> CreateServiceAsync(HttpClient owner, string name)
    {
        var page = await (await owner.GetAsync("/admin/services/create")).Content.ReadAsStringAsync();
        var token = ExtractToken(page);
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

    private async Task RegisterCustomerAsync(string email)
        => await TestSeed.RegisterCustomerAsync(_factory.Services, email);

    private static string ExtractToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*__RequestVerificationToken[^>]*>").Value;
        var value = Regex.Match(tag, "value=\"([^\"]+)\"");
        return value.Success ? value.Groups[1].Value : throw new Xunit.Sdk.XunitException("Anti-forgery token not found.");
    }
}
