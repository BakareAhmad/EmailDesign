using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Contracts.Auth;
using DeChimex.IntegrationTests.Fakes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Contact form persistence: submissions are validated, stored, and viewable by
/// authorized staff in /admin/messages. The flow must not depend on email/Brevo.
/// </summary>
public class ContactMessagesTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ContactMessagesTests(CustomWebApplicationFactory factory) => _factory = factory;

    private const string OwnerEmail = "owner@dechimexprinthub.com";
    private const string BootstrapPassword = "ChangeMe123!";
    private const string ChangedPassword = "Owner!Pass9";

    [Fact]
    public async Task ContactForm_PersistsMessage_VisibleToStaff()
    {
        var publicName = $"Enquirer {Guid.NewGuid():N}".Substring(0, 20);
        var publicEmail = $"enq-{Guid.NewGuid():N}@example.com";
        var details = "I would like a quote for 200 branded flasks.";

        // 1. Anonymous visitor submits the public contact form.
        var anon = CreateBrowser();
        var formHtml = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        var formToken = ExtractToken(formHtml);
        var post = await anon.PostAsync("/contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = publicName,
            ["Email"] = publicEmail,
            ["Phone"] = "+2348000000001",
            ["Service"] = "Flasks",
            ["Details"] = details,
            ["__RequestVerificationToken"] = formToken
        }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect, "a valid contact submission should succeed without any email provider");

        // 2. Staff can read it in the admin messages list.
        var owner = await LoginAsOwnerAsync();
        var list = await (await owner.GetAsync("/admin/messages")).Content.ReadAsStringAsync();
        list.Should().Contain(publicName);
        list.Should().Contain(publicEmail);

        // 3. The detail view shows the full message.
        var detailHref = Regex.Match(list, "href=\"(/admin/messages/[0-9a-fA-F-]{36})\"").Groups[1].Value;
        detailHref.Should().NotBeEmpty("the list should link to a message detail page");
        var detail = await (await owner.GetAsync(detailHref)).Content.ReadAsStringAsync();
        detail.Should().Contain(details);
    }

    [Fact]
    public async Task ContactForm_ValidatesRequiredFields()
    {
        var anon = CreateBrowser();
        var formHtml = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        var formToken = ExtractToken(formHtml);

        var post = await anon.PostAsync("/contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "",
            ["Email"] = "not-an-email",
            ["Phone"] = "",
            ["Service"] = "",
            ["Details"] = "",
            ["__RequestVerificationToken"] = formToken
        }));
        post.StatusCode.Should().Be(HttpStatusCode.OK, "an invalid submission re-renders the form");
        var body = await post.Content.ReadAsStringAsync();
        body.Should().Contain("Please enter your full name.");
    }

    [Fact]
    public async Task Resolve_Toggle_MarksMessageResolvedAndReopens()
    {
        var owner = await LoginAsOwnerAsync();
        var email = $"resolve-{Guid.NewGuid():N}@example.com";

        var formHtml = await (await owner.GetAsync("/contact")).Content.ReadAsStringAsync();
        var formToken = ExtractToken(formHtml);
        await owner.PostAsync("/contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Resolve Tester",
            ["Email"] = email,
            ["Details"] = "Check the resolve flow.",
            ["__RequestVerificationToken"] = formToken
        }));

        var list = await (await owner.GetAsync("/admin/messages")).Content.ReadAsStringAsync();
        var detailHref = Regex.Match(list, "href=\"(/admin/messages/[0-9a-fA-F-]{36})\"").Groups[1].Value;
        detailHref.Should().NotBeEmpty();

        var detail = await (await owner.GetAsync(detailHref)).Content.ReadAsStringAsync();
        detail.Should().Contain("Open");

        // Mark resolved.
        var resolveToken = ExtractToken(detail);
        var resolved = await owner.PostAsync($"{detailHref}/resolve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["resolve"] = "true",
            ["__RequestVerificationToken"] = resolveToken
        }));
        resolved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var afterResolve = await (await owner.GetAsync(detailHref)).Content.ReadAsStringAsync();
        afterResolve.Should().Contain("Resolved");

        // Reopen.
        var reopenToken = ExtractToken(afterResolve);
        await owner.PostAsync($"{detailHref}/resolve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["resolve"] = "false",
            ["__RequestVerificationToken"] = reopenToken
        }));
        var afterReopen = await (await owner.GetAsync(detailHref)).Content.ReadAsStringAsync();
        afterReopen.Should().Contain("Open");
    }

    [Fact]
    public async Task Customer_Cannot_View_Staff_Messages()
    {
        // Register a customer through the shared Application layer (the old
        // /api/v1/auth/register has been removed), then log in over the MVC cookie flow.
        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email);
        await TestSeed.VerifyCustomerAsync(_factory.Services, email); // gated signup: verify before login

        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var denied = await browser.GetAsync("/admin/messages");
        denied.StatusCode.Should().Be(HttpStatusCode.Redirect);
        denied.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");
    }

    [Fact]
    public async Task ContactForm_CreatesStaffNotification_LinkedToMessage()
    {
        var publicName = $"Notif {Guid.NewGuid():N}"[..12];
        var publicEmail = $"notif-{Guid.NewGuid():N}@example.com";

        // 1. Visitor submits the contact form.
        var anon = CreateBrowser();
        var formHtml = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        var post = await anon.PostAsync("/contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = publicName,
            ["Email"] = publicEmail,
            ["Details"] = "Notification persistence check.",
            ["__RequestVerificationToken"] = ExtractToken(formHtml)
        }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // 2. Authorised staff see it in their notification feed, deep-linked to the message.
        var owner = await LoginAsOwnerAsync();
        var feed = await (await owner.GetAsync("/admin/notifications")).Content.ReadAsStringAsync();
        feed.Should().Contain("New contact message");
        feed.Should().Contain(publicName);
        var link = Regex.Match(feed, "href=\"(/admin/messages/[0-9a-fA-F-]{36})\"").Groups[1].Value;
        link.Should().NotBeEmpty("the notification should deep-link to the message");
        var detail = await (await owner.GetAsync(link)).Content.ReadAsStringAsync();
        detail.Should().Contain(publicEmail);

        // 3. The unread badge counts it.
        var countJson = await (await owner.GetAsync("/admin/notifications/unread-count")).Content.ReadAsStringAsync();
        countJson.Should().MatchRegex("\"count\":[1-9]");
    }

    [Fact]
    public async Task ContactNotification_NotVisibleToUsersWithoutMessagesPermission()
    {
        var publicName = $"Scoped {Guid.NewGuid():N}"[..12];
        var anon = CreateBrowser();
        var formHtml = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        await anon.PostAsync("/contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = publicName,
            ["Email"] = $"scoped-{Guid.NewGuid():N}@example.com",
            ["Details"] = "Permission scoping check.",
            ["__RequestVerificationToken"] = ExtractToken(formHtml)
        }));

        // A customer (no messages.view permission) must not see the staff notification.
        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email);
        await TestSeed.VerifyCustomerAsync(_factory.Services, email);
        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var feed = await (await browser.GetAsync("/admin/notifications")).Content.ReadAsStringAsync();
        feed.Should().NotContain(publicName);
    }

    [Fact]
    public async Task Reply_PersistsThread_OnMessageDetails()
    {
        var publicEmail = $"reply-{Guid.NewGuid():N}@example.com";
        var anon = CreateBrowser();
        var formHtml = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        await anon.PostAsync("/contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Reply Tester",
            ["Email"] = publicEmail,
            ["Details"] = "Please reply to me here.",
            ["__RequestVerificationToken"] = ExtractToken(formHtml)
        }));

        var owner = await LoginAsOwnerAsync();
        var list = await (await owner.GetAsync("/admin/messages")).Content.ReadAsStringAsync();
        var detailHref = Regex.Match(list, "href=\"(/admin/messages/[0-9a-fA-F-]{36})\"").Groups[1].Value;
        detailHref.Should().NotBeEmpty();

        var detail = await (await owner.GetAsync(detailHref)).Content.ReadAsStringAsync();
        var replyToken = ExtractToken(detail);
        var subject = $"Re: enquiry {Guid.NewGuid():N}"[..40];
        var body = "Thank you for reaching out - your quote is attached to this email.";

        var post = await owner.PostAsync($"{detailHref}/reply", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Subject"] = subject,
            ["Body"] = body,
            ["__RequestVerificationToken"] = replyToken
        }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect, "a valid staff reply should be accepted");

        var after = await (await owner.GetAsync(detailHref)).Content.ReadAsStringAsync();
        after.Should().Contain(subject, "the reply thread should show the subject");
        after.Should().Contain(body, "the reply thread should show the response body");
        after.Should().Contain("Reply history");
        after.Should().Contain(publicEmail, "the reply is addressed to the enquirer");
        after.Should().Contain($"Reply emailed to {publicEmail}", "a delivered email must be reported as such");

        var fakeEmail = (FakeEmailService)_factory.Services.GetRequiredService<IEmailService>();
        fakeEmail.Sent.Should().Contain(m => m.ToEmail == publicEmail && m.Subject == subject,
            "the reply email must actually be dispatched to the enquirer");
    }

    [Fact]
    public async Task Reply_WhenEmailFails_ReportsFailure_AndStillPersistsThread()
    {
        // The staff reply must never claim a delivery that did not happen (this is the
        // "message response doesn't send email" report: Brevo 401s were swallowed by the
        // old fire-and-forget queue while the UI said "Reply emailed").
        var fakeEmail = (FakeEmailService)_factory.Services.GetRequiredService<IEmailService>();
        fakeEmail.DeliverySucceeds = false;
        try
        {
            var publicEmail = $"failreply-{Guid.NewGuid():N}@example.com";
            var anon = CreateBrowser();
            var formHtml = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
            await anon.PostAsync("/contact", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Name"] = "Fail Reply Tester",
                ["Email"] = publicEmail,
                ["Details"] = "Reply delivery failure test.",
                ["__RequestVerificationToken"] = ExtractToken(formHtml)
            }));

            var owner = await LoginAsOwnerAsync();
            var list = await (await owner.GetAsync("/admin/messages")).Content.ReadAsStringAsync();
            var detailHref = Regex.Match(list, "href=\"(/admin/messages/[0-9a-fA-F-]{36})\"").Groups[1].Value;
            var detail = await (await owner.GetAsync(detailHref)).Content.ReadAsStringAsync();

            var subject = $"Re: failed {Guid.NewGuid():N}"[..40];
            var post = await owner.PostAsync($"{detailHref}/reply", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Subject"] = subject,
                ["Body"] = "This reply is saved but cannot be emailed.",
                ["__RequestVerificationToken"] = ExtractToken(detail)
            }));
            post.StatusCode.Should().Be(HttpStatusCode.Redirect, "the reply itself is valid and saved");

            var after = await (await owner.GetAsync(detailHref)).Content.ReadAsStringAsync();
            after.Should().Contain("did NOT go out", "the flash must tell the truth about the failed delivery");
            after.Should().Contain(subject, "the reply thread is persisted regardless of delivery");
        }
        finally
        {
            fakeEmail.DeliverySucceeds = true;
        }
    }

    [Fact]
    public async Task Reply_Rejected_ForUsersWithoutMessagesPermission()
    {
        var anon = CreateBrowser();
        var formHtml = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        await anon.PostAsync("/contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Scoped Reply",
            ["Email"] = $"scoped-reply-{Guid.NewGuid():N}@example.com",
            ["Details"] = "Permission scope check for replies.",
            ["__RequestVerificationToken"] = ExtractToken(formHtml)
        }));
        var owner = await LoginAsOwnerAsync();
        var list = await (await owner.GetAsync("/admin/messages")).Content.ReadAsStringAsync();
        var detailHref = Regex.Match(list, "href=\"(/admin/messages/[0-9a-fA-F-]{36})\"").Groups[1].Value;

        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email);
        await TestSeed.VerifyCustomerAsync(_factory.Services, email);
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var denied = await customer.PostAsync($"{detailHref}/reply", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Subject"] = "Nope",
            ["Body"] = "Should never send.",
            ["__RequestVerificationToken"] = ExtractToken(await (await customer.GetAsync("/contact")).Content.ReadAsStringAsync())
        }));
        denied.StatusCode.Should().Be(HttpStatusCode.Redirect);
        denied.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");
    }

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

    private static string ExtractToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*__RequestVerificationToken[^>]*>").Value;
        var value = Regex.Match(tag, "value=\"([^\"]+)\"");
        return value.Success ? value.Groups[1].Value : throw new Xunit.Sdk.XunitException("Anti-forgery token not found.");
    }
}
