using System.Net;
using System.Text.Json;
using DeChimex.Application.Features.Auth.Commands;
using DeChimex.Application.Features.Marketing.Commands;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Contracts.Auth;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.IntegrationTests.Fakes;
using DeChimex.Persistence.Context;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// LIVE end-to-end email workflow test. Runs the REAL pipeline (command handlers ->
/// domain events -> background email queue -> Brevo HTTP API) so every email template
/// is actually delivered to the target inbox. Only executes when the E2E_EMAIL_TARGET
/// environment variable is set; the normal suite run never sends emails.
///
///   E2E_EMAIL_TARGET=hjkl00@yopmail.com dotnet test --filter FullyQualifiedName~EmailWorkflowE2E
///
/// Delivery is verified against the Brevo EVENTS API (real "delivered" events for the
/// target address) - the app logs go through Serilog, which cannot be captured from a
/// test logger provider, so the events API is the authoritative in-test proof.
/// The owner additionally confirms receipt from the Yopmail inbox.
/// </summary>
public class EmailWorkflowE2ETests(ITestOutputHelper output)
{
    private const string StaffCopyInbox = "chimexgraphics@gmail.com"; // business.email default

    public sealed class E2EEmailFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // The Brevo key and Marketing:AllowBroadcasts live in appsettings.Development.json.
            // Force the Development environment so the live host loads them.
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }
    }

    private readonly E2EEmailFactory _factory = new();

    // ------------------------------------------------------------------

    [Fact]
    public async Task All_email_workflows_deliver_to_target_inbox()
    {
        var target = Environment.GetEnvironmentVariable("E2E_EMAIL_TARGET");
        if (string.IsNullOrWhiteSpace(target))
        {
            output.WriteLine("E2E_EMAIL_TARGET not set - skipping (normal suite run, no emails sent).");
            return;
        }
        var startedUtc = DateTime.UtcNow.AddMinutes(-2); // skew safety for the events filter
        output.WriteLine($"LIVE E2E: every email workflow will be sent to {target}");

        string apiKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            apiKey = cfg["Brevo:ApiKey"]!;
            output.WriteLine($"Host environment: {env.EnvironmentName}");
            output.WriteLine($"Brevo configured: {!string.IsNullOrWhiteSpace(apiKey)} | " +
                             $"Marketing armed: {cfg["Marketing:AllowBroadcasts"]}");
        }
        apiKey.Should().NotBeNullOrWhiteSpace("the Brevo key must be loaded by the host");

        // Pacing: the email queue is fire-and-forget; give each dispatch time to reach Brevo.
        async Task PaceAsync(int ms = 2_500) => await Task.Delay(ms);

        // ==============================================================
        // PHASE 1 - account / security emails
        // ==============================================================
        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            // 1. Registration -> verification email (6-digit OTP).
            var auth = await sender.Send(new RegisterCommand(
                new RegisterRequest("Hjkl", "Tester", target, "+2348000000002", "E2eInitial!Pass9"), "127.0.0.1"));
            userId = auth.UserId;
        }
        await PaceAsync();
        output.WriteLine("1. SENT - registration verification email (OTP)");

        // 2. Resend -> second verification email.
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new ResendVerificationEmailCommand(new ResendVerificationEmailRequest(target)));
        }
        await PaceAsync();
        output.WriteLine("2. SENT - OTP resend");

        // 3. Verify the OTP read from the database -> welcome email.
        string otp;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            otp = (await users.GetByEmailAsync(target))!.EmailVerificationToken!;
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new VerifyEmailCommand(new VerifyEmailRequest(target, otp!)));
        }
        await PaceAsync();
        output.WriteLine("3. SENT - welcome email (after verification)");

        // 4. Forgot password -> reset link email.
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new ForgotPasswordCommand(new ForgotPasswordRequest(target)));
        }
        await PaceAsync();
        output.WriteLine("4. SENT - password reset email");

        // 5. Complete the reset -> confirmation email.
        string resetToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            resetToken = (await users.GetByEmailAsync(target))!.PasswordResetToken!;
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new ResetPasswordCommand(
                new ResetPasswordRequest(target, resetToken, "E2eReset!Pass9")));
        }
        await PaceAsync();
        output.WriteLine("5. SENT - password reset confirmation email");

        // 6. In-app (portal) password change -> security notification email.
        var portal = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        (await TestSeed.LoginMvcAsync(portal, target, "E2eReset!Pass9")).Should().BeTrue("customer login after reset");
        var pwPage = await portal.GetAsync("/portal/profile/password");
        pwPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var pwHtml = await pwPage.Content.ReadAsStringAsync();
        var pwChange = await portal.PostAsync("/portal/profile/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["CurrentPassword"] = "E2eReset!Pass9",
                ["NewPassword"] = "E2ePortal!Pass9",
                ["ConfirmPassword"] = "E2ePortal!Pass9",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(pwHtml)
            }));
        (pwChange.IsSuccessStatusCode || pwChange.StatusCode == HttpStatusCode.Redirect).Should()
            .BeTrue($"portal password change should succeed, got {pwChange.StatusCode}");
        await PaceAsync();
        output.WriteLine("6. SENT - in-app password change security email");

        // ==============================================================
        // PHASE 2 - order + payment emails
        // ==============================================================
        Guid customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var customers = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
            customerId = (await customers.GetByUserIdAsync(userId))!.Id;
        }
        var productId = await TestSeed.SeedProductAsync(_factory.Services, "E2E Email Test Banner", 5_000m);

        async Task<OrderDto> CreateOrderAsync(string notes)
        {
            using var scope = _factory.Services.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            return await sender.Send(new CreateOrderCommand(new CreateOrderRequest(
                customerId,
                new[] { new CreateOrderItemRequest(productId, 1) },
                Array.Empty<CreateOrderServiceRequest>(),
                null, null, notes, 0, 0, 0)));
        }

        var gateway = (FakePaymentGateway)_factory.Services.GetRequiredService<
            DeChimex.Application.Common.Interfaces.IPaymentGateway>();

        // Order A: confirmation -> failed payment -> manual receipt -> 4 status emails.
        var orderA = await CreateOrderAsync("E2E order A - full lifecycle");
        await PaceAsync();
        output.WriteLine($"7. SENT - order confirmation ({orderA.OrderNumber})");

        PaymentInitResponse initA;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            initA = await sender.Send(new InitializePaymentCommand(
                new InitializePaymentRequest(orderA.Id, "Paystack"), "https://localhost/callback"));
        }

        gateway.VerifySucceeds = false; // simulate a declined gateway verification
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var dto = await sender.Send(new VerifyPaymentCommand(initA.Reference));
            // Terminal failure outcome: the attempt is marked Failed (the PaymentFailedEvent
            // then drives the payment-failed email) instead of throwing.
            dto.Status.Should().Be("Failed");
        }
        await PaceAsync();
        output.WriteLine($"8. SENT - payment failed email ({orderA.OrderNumber})");

        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new ConfirmManualPaymentCommand(initA.PaymentId));
        }
        await PaceAsync();
        output.WriteLine($"9. SENT - payment receipt email ({orderA.OrderNumber}, staff manual confirmation)");

        foreach (var (status, step) in new[]
        {
            ("InProduction", "10"), ("Ready", "11"), ("Completed", "12"), ("Delivered", "13")
        })
        {
            using var scope = _factory.Services.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new UpdateOrderStatusCommand(orderA.Id, status));
            await PaceAsync(2_000);
            output.WriteLine($"{step}. SENT - status email {status} ({orderA.OrderNumber})");
        }

        // Order B: confirmation -> cancelled.
        var orderB = await CreateOrderAsync("E2E order B - cancellation");
        await PaceAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new CancelOrderCommand(orderB.Id, new CancelOrderRequest("E2E test cancellation")));
        }
        await PaceAsync();
        output.WriteLine($"14/15. SENT - order confirmation + Cancelled status email ({orderB.OrderNumber})");

        // Order C: confirmation -> paid via gateway verification (receipt) -> refunded.
        var orderC = await CreateOrderAsync("E2E order C - refund");
        await PaceAsync();
        gateway.VerifySucceeds = true;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var init = await sender.Send(new InitializePaymentCommand(
                new InitializePaymentRequest(orderC.Id, "Paystack"), "https://localhost/callback"));
            await sender.Send(new VerifyPaymentCommand(init.Reference));
        }
        await PaceAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new RefundOrderCommand(orderC.Id));
        }
        await PaceAsync();
        output.WriteLine($"16/17/18. SENT - confirmation + receipt + Refunded status email ({orderC.OrderNumber})");

        // ==============================================================
        // PHASE 3 - contact form (staff copy + owner reply)
        // ==============================================================
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var contactHtml = await (await anon.GetAsync("/contact")).Content.ReadAsStringAsync();
        var contactPost = await anon.PostAsync("/contact", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Name"] = "Hjkl Tester",
                ["Email"] = target,
                ["Details"] = "E2E live test: please reply to this enquiry.",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(contactHtml)
            }));
        contactPost.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the contact form redirects back with a success banner after sending");
        await PaceAsync();
        output.WriteLine($"19. SENT - contact staff copy (to {StaffCopyInbox})");

        Guid messageId, ownerUserId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            messageId = await db.ContactMessages.AsNoTracking()
                .OrderByDescending(m => m.CreatedDateUtc).Select(m => m.Id).FirstAsync();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            ownerUserId = (await users.GetByEmailAsync(TestSeed.OwnerEmail))!.Id;
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new DeChimex.Application.Features.Contacts.Commands
                .SendContactMessageReplyCommand(
                    messageId, "Your branding enquiry - E2E reply",
                    "Hi! This is a live test of the staff reply workflow.\nEverything you see in this email was generated by the real pipeline.",
                    ownerUserId, "Owner"));
        }
        await PaceAsync();
        output.WriteLine($"20. SENT - staff reply emailed to {target}");

        // ==============================================================
        // PHASE 4 - marketing broadcast (background worker)
        // ==============================================================
        Guid broadcastId;
        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            broadcastId = await sender.Send(new CreateMarketingBroadcastCommand(
                "E2E TEST - DE-CHIMEX marketing broadcast",
                "<h2>Hi {{FirstName}},</h2><p>This is a <b>live test</b> of the marketing broadcast system. " +
                "Real subject, real branding, real unsubscribe link below.</p>" +
                "<p><a href=\"https://dechimexprints.com\">Visit the store</a></p>",
                null, TestSeed.OwnerEmail));
            await sender.Send(new SendMarketingBroadcastCommand(broadcastId));
        }

        // The background poller (10s interval) picks it up and sends per-recipient.
        var workerDeadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < workerDeadline)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var b = await db.MarketingBroadcasts.AsNoTracking()
                .Include(x => x.Recipients)
                .SingleAsync(x => x.Id == broadcastId);
            if (b.Status == MarketingBroadcastStatus.Completed &&
                b.Recipients.All(r => r.Status != MarketingRecipientStatus.Pending))
            {
                b.Recipients.Should().ContainSingle("the only eligible recipient is the registered test customer")
                    .Which.Email.Should().Be(target);
                break;
            }
            await Task.Delay(2_000);
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var b = await db.MarketingBroadcasts.AsNoTracking()
                .Include(x => x.Recipients)
                .SingleAsync(x => x.Id == broadcastId);
            b.Status.Should().Be(MarketingBroadcastStatus.Completed,
                "the background worker should have delivered the broadcast within the window");
            b.Recipients.Should().ContainSingle().Which.Status.Should().Be(MarketingRecipientStatus.Sent);
        }
        output.WriteLine($"21. SENT - marketing broadcast to {target} (background worker, tracked as Sent in the ledger)");

        // ==============================================================
        // FINAL - verify DELIVERED events from the Brevo events API
        // ==============================================================
        async Task<List<(string Subject, string Email, string Date)>> GetDeliveredAsync(string email)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("api-key", apiKey);
            var results = new List<(string, string, string)>();
            for (var offset = 0; offset < 300; offset += 100)
            {
                var url = $"https://api.brevo.com/v3/smtp/statistics/events" +
                          $"?limit=100&offset={offset}&days=1&email={Uri.EscapeDataString(email)}";
                var json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var events = doc.RootElement.GetProperty("events").EnumerateArray();
                var page = 0;
                foreach (var e in events)
                {
                    page++;
                    if (e.GetProperty("event").GetString() != "delivered") continue;
                    var date = e.GetProperty("date").GetString()!;
                    if (DateTimeOffset.Parse(date).UtcDateTime < startedUtc) continue;
                    results.Add((e.GetProperty("subject").GetString() ?? "",
                        e.GetProperty("email").GetString() ?? "", date));
                }
                if (page < 100) break; // no further pages
            }
            return results;
        }

        var expectations = new (string Fragment, int Min, string Label)[]
        {
            ("Verify your DE-CHIMEX email", 2, "registration OTP + resend"),
            ("Welcome to DE-CHIMEX PRINT HUB", 1, "welcome"),
            ("Reset your DE-CHIMEX password", 1, "forgot password"),
            ("Your DE-CHIMEX password was changed", 2, "reset confirmation + in-app change"),
            (orderA.OrderNumber, 7, "order A: confirm + failed payment + receipt + 4 status"),
            (orderB.OrderNumber, 2, "order B: confirm + cancelled"),
            (orderC.OrderNumber, 3, "order C: confirm + receipt + refunded"),
            ("Your branding enquiry - E2E reply", 1, "staff reply"),
            ("E2E TEST - DE-CHIMEX marketing broadcast", 1, "marketing broadcast"),
        };

        output.WriteLine("Waiting for Brevo 'delivered' events to accumulate...");
        List<(string Subject, string Email, string Date)> delivered = new();
        var eventsDeadline = DateTime.UtcNow.AddSeconds(300);
        while (DateTime.UtcNow < eventsDeadline)
        {
            delivered = await GetDeliveredAsync(target);
            // Wait until EVERY expected email has a delivered event (the marketing
            // send happens last via the background worker; its event can lag ~30s).
            if (expectations.All(x => delivered.Count(d => d.Subject.Contains(x.Fragment)) >= x.Min))
                break;
            await Task.Delay(10_000);
        }

        int Count(string fragment) => delivered.Count(d => d.Subject.Contains(fragment));
        void Expect(string fragment, int min, string label)
        {
            var n = Count(fragment);
            output.WriteLine($"  delivered '{fragment}' x{n} (expected >= {min}) [{label}]");
            n.Should().BeGreaterThanOrEqualTo(min, $"email '{label}' should have been delivered to {target}");
        }

        output.WriteLine($"\n=== BREVO DELIVERED EVENTS for {target} (this run) ===");
        foreach (var d in delivered.OrderByDescending(d => d.Date))
            output.WriteLine($"  {d.Date}  {d.Subject}");

        Expect("Verify your DE-CHIMEX email", 2, "registration OTP + resend");
        Expect("Welcome to DE-CHIMEX PRINT HUB", 1, "welcome");
        Expect("Reset your DE-CHIMEX password", 1, "forgot password");
        Expect("Your DE-CHIMEX password was changed", 2, "reset confirmation + in-app change");
        Expect(orderA.OrderNumber, 7, "order A: confirm + failed + receipt + 4 status");
        Expect(orderB.OrderNumber, 2, "order B: confirm + cancelled");
        Expect(orderC.OrderNumber, 3, "order C: confirm + receipt + refunded");
        Expect("Your branding enquiry - E2E reply", 1, "staff reply");
        Expect("E2E TEST - DE-CHIMEX marketing broadcast", 1, "marketing broadcast");
        delivered.Count.Should().BeGreaterThanOrEqualTo(19);

        // Staff copy went to the business inbox.
        var staffDelivered = await GetDeliveredAsync(StaffCopyInbox);
        staffDelivered.Should().Contain(d => d.Subject.Contains("New contact form message from Hjkl Tester"),
            "the contact-form staff copy must reach the business inbox");
        output.WriteLine($"\nStaff copy delivered to {StaffCopyInbox}: " +
            string.Join("; ", staffDelivered.Where(d => d.Subject.Contains("Hjkl Tester")).Select(d => d.Date)));

        output.WriteLine($"\nTOTAL DELIVERED TO {target} THIS RUN: {delivered.Count} (plus 1 staff copy to {StaffCopyInbox})");
    }
}
