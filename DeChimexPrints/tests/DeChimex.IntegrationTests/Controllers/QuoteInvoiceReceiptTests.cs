using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Quote invoices and receipts (2026-08-28):
/// staff create draft quote invoices from a product/service selection (no order placed),
/// invoices are issued at order placement (before payment), receipts are generated
/// after payment, share links serve documents publicly, and customers can never reach
/// the staff counter forms even when they hold the orders.create permission.
/// </summary>
public class QuoteInvoiceReceiptTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public QuoteInvoiceReceiptTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task QuoteInvoice_CreatedAsDraft_WithProductAndServiceLines()
    {
        var owner = await LoginAsOwnerAsync();
        var productId = await SeedProductAsync("Quote Mug 250ml");
        var serviceId = await CreateServiceAsync(owner, "Quote Engraving Service");

        var (status, location) = await PostQuoteAsync(owner, new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Quote",
            ["NewLastName"] = "Customer",
            ["NewPhone"] = "08030002222",
            ["Lines[0].ProductId"] = productId.ToString(),
            ["Lines[0].Quantity"] = "2",
            ["Lines[1].ServiceId"] = serviceId.ToString(),
            ["Lines[1].Quantity"] = "1",
            ["Discount"] = "0",
            ["Tax"] = "0",
            ["Notes"] = "deposit expected before production"
        });
        status.Should().Be(HttpStatusCode.Redirect, "a valid quote must be created");
        location.Should().MatchRegex(@"^/admin/invoices/[0-9a-fA-F-]{36}$");

        var doc = await (await owner.GetAsync(location)).Content.ReadAsStringAsync();
        doc.Should().Contain("Draft", "a quote invoice starts as a draft");
        doc.Should().Contain("Quote Invoice", "the document title marks it as a quote");
        doc.Should().Contain("Quote Customer");
        doc.Should().Contain("Quote Mug 250ml");
        doc.Should().Contain("Quote Engraving Service");
        doc.Should().Contain("deposit expected before production");
        doc.Should().Contain("No order placed yet");
        doc.Should().Contain("/admin/orders/new?invoice=", "the quote offers one-click order creation");

        // The invoices list shows it with the Quote badge and no order number.
        var list = await (await owner.GetAsync("/admin/invoices?search=Quote%20Customer")).Content.ReadAsStringAsync();
        list.Should().Contain("Quote", "quote invoices are listed");
        list.Should().Contain("badge badge-primary\">Quote<", "the list row carries the Quote badge");
    }

    [Fact]
    public async Task StaffCounterForms_AreBlockedForCustomers_EvenWithOrdersView()
    {
        // 1. A regular portal customer (only the orders.create permission) is blocked.
        var shopper = await RegisterCustomerAsync("quote.blocked1@example.com");
        (await shopper.GetAsync("/admin/orders/new")).Headers.Location?.ToString()
            .Should().Contain("/Account/AccessDenied", "a customer must not reach the POS form");
        (await shopper.GetAsync("/admin/invoices/new")).Headers.Location?.ToString()
            .Should().Contain("/Account/AccessDenied", "a customer must not reach quote creation");

        // 2. Even if an admin mistakenly granted the customer role orders.view,
        //    the not-Customer gate still blocks the counter forms.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var role = await db.Roles.Include(r => r.Permissions)
                .FirstOrDefaultAsync(r => r.NormalizedName == "CUSTOMER");
            role.Should().NotBeNull();
            var viewPerm = await db.Permissions.FirstOrDefaultAsync(p => p.Name == "orders.view");
            viewPerm.Should().NotBeNull();
            if (viewPerm is not null && role!.Permissions.All(rp => rp.PermissionId != viewPerm.Id))
                role.SetPermissions(role.Permissions.Select(rp => rp.PermissionId).Append(viewPerm.Id));
            await db.SaveChangesAsync();
        }

        // A fresh account picks up the updated role claims on registration.
        var shopper2 = await RegisterCustomerAsync("quote.blocked2@example.com");
        (await shopper2.GetAsync("/admin/orders/new")).Headers.Location?.ToString()
            .Should().Contain("/Account/AccessDenied", "the role gate must not be bypassed by extra permissions");
        (await shopper2.GetAsync("/admin/invoices/new")).Headers.Location?.ToString()
            .Should().Contain("/Account/AccessDenied", "quote creation is sales/manager/owner territory");
    }

    [Fact]
    public async Task InvoiceShareLink_ServesDocumentPublicly_And404sUnknownTokens()
    {
        var owner = await LoginAsOwnerAsync();
        var serviceId = await CreateServiceAsync(owner, "Share Link Service");
        var (_, location) = await PostQuoteAsync(owner, new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Shared",
            ["NewLastName"] = "Quote",
            ["Lines[0].ServiceId"] = serviceId.ToString(),
            ["Lines[0].Quantity"] = "1",
            ["Discount"] = "0",
            ["Tax"] = "0"
        });

        // The quote is shareable immediately: the document page renders the /i/{token} link.
        var pageResp = await owner.GetAsync(location);
        var page = await pageResp.Content.ReadAsStringAsync();
        pageResp.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Should().Contain("/i/", "a quote invoice is shareable from the moment it is created");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        // The Share action is idempotent: it keeps the existing token.
        var share = await owner.PostAsync(location + "/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));
        share.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var page2 = await (await owner.GetAsync(location)).Content.ReadAsStringAsync();
        // Share links carry the environment's base URL (owner directive 2026-09-08):
        // absolute, not a bare /i/ path. The in-process client fetches the path part.
        var shareUrl = Regex.Match(page2, "value=\"(https?://[^\"]+?/i/[a-f0-9]{32})\"").Groups[1].Value;
        shareUrl.Should().NotBeEmpty("a public absolute /i/{token} link must be rendered");
        shareUrl.Should().StartWith("http", "share links must be fully qualified so copied links resolve");

        // Any visitor without a login can open the document.
        var anon = _factory.CreateClient();
        var publicDoc = await anon.GetAsync(new Uri(shareUrl).PathAndQuery);
        publicDoc.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await publicDoc.Content.ReadAsStringAsync();
        html.Should().Contain("Shared Quote");
        html.Should().Contain("Quote Invoice");
        html.Should().Contain("window.print", "the shared page offers download/print");
        html.Should().Contain("class=\"doc-logo\" src=\"/img/logo.png\"", "the document header carries the brand logo");
        html.Should().Contain("rel=\"icon\"", "shared documents carry the brand favicon");

        // Unknown tokens get a plain 404, no document leak.
        (await anon.GetAsync("/i/not-a-real-token")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await anon.GetAsync("/r/not-a-real-token")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task QuoteInvoice_Conversion_AttachesExistingInvoice_WithoutDuplicatingIt()
    {
        var owner = await LoginAsOwnerAsync();
        var serviceId = await CreateServiceAsync(owner, "Conversion Service");
        var (_, quoteUrl) = await PostQuoteAsync(owner, new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Convert",
            ["NewLastName"] = "Me",
            ["Lines[0].ServiceId"] = serviceId.ToString(),
            ["Lines[0].Quantity"] = "1",
            ["Discount"] = "0",
            ["Tax"] = "0"
        });
        var quoteId = Regex.Match(quoteUrl, @"/admin/invoices/([0-9a-fA-F-]{36})").Groups[1].Value;

        // The prefill page carries the quote banner and the hidden invoice id.
        var prefill = await (await owner.GetAsync($"/admin/orders/new?invoice={quoteId}")).Content.ReadAsStringAsync();
        prefill.Should().Contain("Creating order from quote invoice");
        prefill.Should().Contain("name=\"InvoiceId\" value=\"" + quoteId.ToLowerInvariant() + "\"");

        var customerId = await FindCustomerIdAsync(owner, "Convert");
        var (status, orderLocation) = await PostOrderAsync(owner, new Dictionary<string, string>
        {
            ["InvoiceId"] = quoteId,
            ["CustomerMode"] = "existing",
            ["CustomerId"] = customerId,
            ["Fulfilment"] = "pickup",
            ["Services[0].ServiceId"] = serviceId.ToString(),
            ["Services[0].Quantity"] = "1"
        });
        status.Should().Be(HttpStatusCode.Redirect, "placing the order from a quote must succeed");

        var details = await (await owner.GetAsync(orderLocation)).Content.ReadAsStringAsync();
        var orderNumber = Regex.Match(details, "DCX-\\d{4}-\\d{6}").Value;
        orderNumber.Should().NotBeEmpty();
        details.Should().Contain("Payment Required", "the converted order waits for payment");
        details.Should().Contain($"/admin/invoices/{quoteId}", "the order reuses the quote invoice, no new one");

        // Exactly one invoice exists for the order number (the quote became the order invoice).
        var list = await (await owner.GetAsync($"/admin/invoices?search={orderNumber}")).Content.ReadAsStringAsync();
        list.Should().Contain(Regex.Match(quoteUrl, @"/admin/invoices/([0-9a-fA-F-]{36})").Groups[1].Value,
            "the quote invoice row must still be the one serving the order");
        list.Should().NotContain("No invoices yet");
        var invoiceCount = Regex.Matches(list, "badge badge-primary\">Quote<").Count;
        invoiceCount.Should().Be(0, "a converted invoice is no longer a quote");

        // The document now links the order number.
        var doc = await (await owner.GetAsync(quoteUrl)).Content.ReadAsStringAsync();
        doc.Should().Contain(orderNumber, "the invoice document must show its order");
        doc.Should().NotContain("No order placed yet");
    }

    [Fact]
    public async Task OrderPlacement_IssuesInvoiceBeforePayment_AndPayment_GeneratesReceipt()
    {
        var owner = await LoginAsOwnerAsync();
        var serviceId = await CreateServiceAsync(owner, "Invoice Timing Service");

        // 1. Place a plain counter order without payment.
        var (_, orderLocation) = await PostOrderAsync(owner, new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Timing",
            ["NewLastName"] = "Order",
            ["Fulfilment"] = "pickup",
            ["Services[0].ServiceId"] = serviceId.ToString(),
            ["Services[0].Quantity"] = "1"
        });
        var details = await (await owner.GetAsync(orderLocation)).Content.ReadAsStringAsync();
        var orderNumber = Regex.Match(details, "DCX-\\d{4}-\\d{6}").Value;
        details.Should().Contain("Payment Required");

        // 2. The invoice already exists, issued at placement (before any payment).
        var list = await (await owner.GetAsync($"/admin/invoices?search={orderNumber}")).Content.ReadAsStringAsync();
        list.Should().Contain("INV-2026-", "the invoice is issued when the order is placed, not at payment");
        list.Should().Contain(">Issued<", "an unpaid order invoice is Issued, not Draft");
        list.Should().NotContain("RCT-", "no receipt exists before payment");

        // 3. Mark the order paid: the invoice flips to Paid and a receipt is generated.
        var orderId = Regex.Match(orderLocation, @"/admin/orders/([0-9a-fA-F-]{36})").Groups[1].Value;
        var pageToken = Regex.Match(details, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var paid = await owner.PostAsync($"/admin/orders/{orderId}/mark-paid", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Method"] = "Cash",
            ["Note"] = "cash at counter",
            ["__RequestVerificationToken"] = pageToken
        }));
        paid.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var details2 = await (await owner.GetAsync(orderLocation)).Content.ReadAsStringAsync();
        details2.Should().Contain("In Production", "the order is paid");
        details2.Should().Contain("RCT-2026-", "a receipt was generated after payment");
        details2.Should().Contain("Documents", "the order page links its documents");

        var list2 = await (await owner.GetAsync($"/admin/invoices?search={orderNumber}")).Content.ReadAsStringAsync();
        list2.Should().Contain(">Paid<", "the invoice is marked paid");

        var receipts = await (await owner.GetAsync("/admin/receipts?search=" + orderNumber)).Content.ReadAsStringAsync();
        receipts.Should().Contain(orderNumber);
        receipts.Should().Contain("RCT-2026-");

        // 4. The receipt share link works publicly.
        var receiptId = Regex.Match(receipts, @"/admin/receipts/([0-9a-fA-F-]{36})").Groups[1].Value;
        var receiptPage = await (await owner.GetAsync($"/admin/receipts/{receiptId}")).Content.ReadAsStringAsync();
        var shareToken = Regex.Match(receiptPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var shareResp = await owner.PostAsync($"/admin/receipts/{receiptId}/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = shareToken
        }));
        shareResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "the receipt share action must be idempotent");
        var receiptShare = Regex.Match(receiptPage, "value=\"(https?://[^\"]+?/r/[a-f0-9]{32})\"").Groups[1].Value;
        receiptShare.Should().NotBeEmpty("a public absolute /r/{token} link must be rendered");
        receiptShare.Should().StartWith("http", "share links must be fully qualified so copied links resolve");
        var anon = _factory.CreateClient();
        var publicReceipt = await anon.GetAsync(new Uri(receiptShare).PathAndQuery);
        publicReceipt.StatusCode.Should().Be(HttpStatusCode.OK);
        var receiptHtml = await publicReceipt.Content.ReadAsStringAsync();
        receiptHtml.Should().Contain("Receipt");
        receiptHtml.Should().Contain("class=\"doc-logo\" src=\"/img/logo.png\"", "the receipt header carries the brand logo");
    }

    [Fact]
    public async Task Refund_MarksReceiptAndInvoiceRefunded()
    {
        var owner = await LoginAsOwnerAsync();
        var serviceId = await CreateServiceAsync(owner, "Refund Service");

        var (_, orderLocation) = await PostOrderAsync(owner, new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Refund",
            ["NewLastName"] = "Case",
            ["Fulfilment"] = "pickup",
            ["Services[0].ServiceId"] = serviceId.ToString(),
            ["Services[0].Quantity"] = "1",
            ["PaymentMethod"] = "Cash",
            ["PaymentNote"] = "cash, to be refunded"
        });
        var details = await (await owner.GetAsync(orderLocation)).Content.ReadAsStringAsync();
        var orderNumber = Regex.Match(details, "DCX-\\d{4}-\\d{6}").Value;
        var orderId = Regex.Match(orderLocation, @"/admin/orders/([0-9a-fA-F-]{36})").Groups[1].Value;
        details.Should().Contain("RCT-2026-", "paying at placement generates the receipt");

        var refundToken = Regex.Match(details, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var refund = await owner.PostAsync($"/admin/orders/{orderId}/refund", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = refundToken
        }));
        refund.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var details2 = await (await owner.GetAsync(orderLocation)).Content.ReadAsStringAsync();
        details2.Should().Contain("Refunded", "the order is refunded");

        var receipts = await (await owner.GetAsync("/admin/receipts?search=" + orderNumber)).Content.ReadAsStringAsync();
        receipts.Should().Contain("badge badge-error\">Refunded<", "the receipt is flagged refunded");

        var list = await (await owner.GetAsync($"/admin/invoices?search={orderNumber}")).Content.ReadAsStringAsync();
        list.Should().Contain(">Refunded<", "the invoice is flagged refunded");
    }

    // ---- helpers ----

    private async Task<(HttpStatusCode, string)> PostQuoteAsync(HttpClient client, Dictionary<string, string> fields)
    {
        var page = await (await client.GetAsync("/admin/invoices/new")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        fields["__RequestVerificationToken"] = token;
        var response = await client.PostAsync("/admin/invoices/new", new FormUrlEncodedContent(fields));
        return (response.StatusCode, response.Headers.Location?.ToString() ?? "");
    }

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

    /// <summary>Creates a category and product directly in the database (test setup, not the flow under test).</summary>
    private async Task<Guid> SeedProductAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Name == "Quote Test Category");
        var category = existing ?? Category.Create("Quote Test Category");
        if (existing is null)
        {
            db.Categories.Add(category);
            await db.SaveChangesAsync();
        }
        var product = Product.Create(name, "Q-" + name.Replace(" ", "").Substring(0, Math.Min(8, name.Length)).ToUpperInvariant(),
            "quote test product", category.Id, 2500m);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<string> FindCustomerIdAsync(HttpClient owner, string name)
    {
        var picker = await (await owner.GetAsync("/admin/customers/picker?q=" + Uri.EscapeDataString(name))).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(picker);
        doc.RootElement.GetArrayLength().Should().BeGreaterThanOrEqualTo(1, $"customer {name} must exist");
        return doc.RootElement[0].GetProperty("id").GetString()!;
    }

    private async Task<HttpClient> RegisterCustomerAsync(string email)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var regPage = await (await client.GetAsync("/Account/Register")).Content.ReadAsStringAsync();
        var regToken = Regex.Match(regPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var reg = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["FirstName"] = "Block",
            ["LastName"] = "Me",
            ["Email"] = email,
            ["Phone"] = "08012340000",
            ["Password"] = "Pass1234!",
            ["ConfirmPassword"] = "Pass1234!",
            ["__RequestVerificationToken"] = regToken
        }));
        reg.StatusCode.Should().Be(HttpStatusCode.Redirect, "customer registration should succeed");
        // Gated signup: finish it by submitting the emailed token, which issues the auth cookie.
        var verifyPage = await (await client.GetAsync("/Account/VerifyEmail")).Content.ReadAsStringAsync();
        var vAntiforgery = Regex.Match(verifyPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var vToken = await TestSeed.GetVerificationTokenAsync(_factory.Services, email);
        var verify = await client.PostAsync("/Account/VerifyEmail", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Token"] = vToken!,
            ["__RequestVerificationToken"] = vAntiforgery
        }));
        verify.StatusCode.Should().Be(HttpStatusCode.Redirect, "verification should sign the customer in");
        return client;
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
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPassword"] = currentPassword,
            ["NewPassword"] = "Owner!Pass9",
            ["ConfirmPassword"] = "Owner!Pass9",
            ["__RequestVerificationToken"] = token
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
}
