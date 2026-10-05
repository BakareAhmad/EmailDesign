using System.Net;
using DeChimex.Application.Common.Interfaces;
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
/// End-to-end coverage of the custom-order flow (CUSTOM_ORDER_FLOW_PLAN.md):
/// a product in a category with ordering options routes to the order-request form,
/// submission lands the order in AwaitingReview with snapshotted responses, the staff
/// workspace shows the structured request, the two sides exchange chat messages, and
/// staff finalization moves the order to PendingPayment for the existing Paystack flow.
/// </summary>
public class CustomOrderFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CustomOrderFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<Guid> SeedCustomizableProductAsync(IServiceProvider services, string question)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var category = Category.Create($"Custom {question} Category", "test seed", 99);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        await sender.Send(new SaveCategoryOptionsCommand(category.Id, new SaveCategoryOptionsRequest(
            new List<SaveCategoryOptionDto>
            {
                new("colour", question, "Text", true, null, null, new List<string>())
            })));

        var product = Product.Create("Custom Jersey", "SKU-CUSTOM-" + Guid.NewGuid().ToString("N")[..8],
            "Seeded customizable product", category.Id, 15000m, isCustomizable: true);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    [Fact]
    public async Task CustomOrderFlow_SubmitReviewMessageAndFinalize_WorksEndToEnd()
    {
        var services = _factory.Services;
        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email, firstName: "Order", lastName: "Requester");
        await TestSeed.VerifyCustomerAsync(services, email);

        var productId = await SeedCustomizableProductAsync(services, "What colour do you want?");

        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        // 1. The customizable product routes to the order form and renders the option.
        var form = await customer.GetAsync($"/portal/shop/{productId}/order");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var formHtml = await form.Content.ReadAsStringAsync();
        formHtml.Should().Contain("What colour do you want?");

        // 2. Submit the request with an answer; it redirects into the workspace. The option
        // answer is keyed by option id ("opt_{id}"), captured from the rendered form.
        var token = TestSeed.ExtractAntiforgeryToken(formHtml);
        var optionId = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\"opt_([0-9a-f-]{36})\"").Groups[1].Value;
        optionId.Should().NotBeEmpty();
        var submit = await customer.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "2",
                ["notes"] = "Need it by Friday",
                [$"opt_{optionId}"] = "Royal blue",
                ["__RequestVerificationToken"] = token
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var workspaceUrl = submit.Headers.Location!.ToString();
        workspaceUrl.Should().Contain("/portal/orders/");

        // 3. The order exists in AwaitingReview with the snapshotted response.
        Guid orderId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var order = await db.Orders.Include(o => o.Responses)
                .OrderByDescending(o => o.CreatedDateUtc).FirstAsync();
            orderId = order.Id;
            order.Status.Should().Be(OrderStatus.AwaitingReview);
            order.Responses.Should().ContainSingle(r => r.Value == "Royal blue");
        }

        // 4. The customer workspace shows the request and the answer.
        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        workspace.StatusCode.Should().Be(HttpStatusCode.OK);
        var workspaceHtml = await workspace.Content.ReadAsStringAsync();
        workspaceHtml.Should().Contain("Royal blue");
        workspaceHtml.Should().Contain("Order Received");

        // 5. The customer sends a chat message.
        var msgToken = TestSeed.ExtractAntiforgeryToken(workspaceHtml);
        var msg = await customer.PostAsync($"/portal/orders/{orderId}/workspace/message",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["body"] = "Please use white collar for the jerseys.",
                ["__RequestVerificationToken"] = msgToken
            }));
        msg.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var after = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        (await after.Content.ReadAsStringAsync()).Should().Contain("white collar");

        // 6. Staff (owner) open the workspace and see the structured request + chat.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var admin = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminHtml = await admin.Content.ReadAsStringAsync();
        adminHtml.Should().Contain("Royal blue");
        adminHtml.Should().Contain("white collar");

        // 7. Staff finalize requirements + pricing; the order becomes PendingPayment.
        var adminToken = TestSeed.ExtractAntiforgeryToken(adminHtml);
        var finalize = await owner.PostAsync($"/admin/orders/{orderId}/workspace/finalize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["deliveryFee"] = "1500",
                ["discount"] = "0",
                ["tax"] = "0",
                ["custom_desc"] = "Design preparation",
                ["custom_price"] = "5000",
                ["custom_qty"] = "1",
                ["__RequestVerificationToken"] = adminToken
            }));
        finalize.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var order = await db.Orders.Include(o => o.CustomLines).FirstAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.PendingPayment);
            order.CustomLines.Should().ContainSingle(c => c.Description == "Design preparation");
        }

        // 8. The customer now sees the finalized quote with a pay action.
        var afterFinalize = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var finalHtml = await afterFinalize.Content.ReadAsStringAsync();
        finalHtml.Should().Contain("Payment Required");
        finalHtml.Should().Contain("Review &amp; pay");
    }

    [Fact]
    public async Task CustomOrderFlow_PaymentAmount_IncludesCustomLineAndCharge()
    {
        // Regression: the payment path read the order via OrderRepository.GetWithLinesAsync,
        // which did not include CustomLines, so GrandTotal (and therefore the amount sent to
        // Paystack) silently dropped any custom line — the customer was undercharged. The
        // initialize call must charge the full finalized total (products + custom lines +
        // delivery) plus the customer-borne Paystack charge.
        var services = _factory.Services;
        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email, firstName: "Pay", lastName: "Amount");
        await TestSeed.VerifyCustomerAsync(services, email);

        var productId = await SeedCustomizableProductAsync(services, "Which name goes on it?");

        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var form = await customer.GetAsync($"/portal/shop/{productId}/order");
        var formHtml = await form.Content.ReadAsStringAsync();
        var optionId = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\"opt_([0-9a-f-]{36})\"").Groups[1].Value;
        var submit = await customer.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "2",
                [$"opt_{optionId}"] = "CHIOMA",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(formHtml)
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);

        Guid orderId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            orderId = (await db.Orders.OrderByDescending(o => o.CreatedDateUtc).FirstAsync()).Id;
        }

        // Finalize: product line 2 x 15,000 = 30,000 + custom line 5,000 + delivery 1,500 = 36,500.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var admin = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var finalize = await owner.PostAsync($"/admin/orders/{orderId}/workspace/finalize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [$"line_{productId}_qty"] = "2",
                [$"line_{productId}_price"] = "15000",
                ["deliveryFee"] = "1500",
                ["discount"] = "0",
                ["tax"] = "0",
                ["custom_desc"] = "Design preparation",
                ["custom_price"] = "5000",
                ["custom_qty"] = "1",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await admin.Content.ReadAsStringAsync())
            }));
        finalize.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Pay: initialize the Paystack transaction as the customer.
        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var pay = await customer.PostAsync($"/checkout/{orderId}/pay", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await workspace.Content.ReadAsStringAsync())
            }));
        pay.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var gateway = (Fakes.FakePaymentGateway)services.GetRequiredService<IPaymentGateway>();
        gateway.LastInitialization.Should().NotBeNull();
        var charge = services.GetRequiredService<IPaymentChargeCalculator>();
        var expected = 36_500m + charge.ComputeCustomerBorneCharge(36_500m);
        gateway.LastInitialization!.Amount.Should().Be(expected);
    }

    [Fact]
    public async Task DesignProof_StaffUploadsCustomerApproves_RecordsApprovedVersion()
    {
        var services = _factory.Services;
        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email, firstName: "Proof", lastName: "Approver");
        await TestSeed.VerifyCustomerAsync(services, email);

        var productId = await SeedCustomizableProductAsync(services, "Slogan or design?");

        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var form = await customer.GetAsync($"/portal/shop/{productId}/order");
        var formHtml = await form.Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(formHtml);
        var optionId = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\"opt_([0-9a-f-]{36})\"").Groups[1].Value;
        var submit = await customer.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "1",
                [$"opt_{optionId}"] = "Logo on the back",
                ["__RequestVerificationToken"] = token
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);

        Guid orderId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            orderId = await db.Orders.OrderByDescending(o => o.CreatedDateUtc).Select(o => o.Id).FirstAsync();
        }

        // Staff finalize requirements (which implicitly confirms them), then upload a design proof.
        // Finalize is the natural workflow that replaces the explicit "Confirm Requirements" transition.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var workspacePage = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var adminHtml = await workspacePage.Content.ReadAsStringAsync();
        var adminToken = TestSeed.ExtractAntiforgeryToken(adminHtml);

        var finalize = await owner.PostAsync($"/admin/orders/{orderId}/workspace/finalize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["deliveryFee"] = "0",
                ["discount"] = "0",
                ["tax"] = "0",
                ["__RequestVerificationToken"] = adminToken
            }));
        finalize.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new DeChimex.Application.Features.Orders.Commands.UploadDesignProofCommand(
                orderId, new UploadDesignProofRequest(
                    "proofs/mock.png", "mock.png", "https://example.test/proofs/mock.png", "image/png", "Please check the layout")));
        }

        // The customer sees the proof awaiting approval.
        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var workspaceHtml = await workspace.Content.ReadAsStringAsync();
        workspaceHtml.Should().Contain("Awaiting Your Approval");
        workspaceHtml.Should().Contain("Please check the layout");

        // Customer approves it via the workspace form.
        Guid proofId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            proofId = await db.OrderDesignProofs.Where(p => p.OrderId == orderId).Select(p => p.Id).FirstAsync();
        }
        var custToken = TestSeed.ExtractAntiforgeryToken(workspaceHtml);
        var approve = await customer.PostAsync($"/portal/orders/{orderId}/workspace/proof/{proofId}/approve",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = custToken
            }));
        approve.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var proof = await db.OrderDesignProofs.FirstAsync(p => p.Id == proofId);
            proof.Status.Should().Be(DesignProofStatus.Approved);
            proof.ApprovedAtUtc.Should().NotBeNull();
            var order = await db.Orders.FirstAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.RequirementsConfirmed);
        }
    }

    /// <summary>Standard preamble for proof-lifecycle tests: a submitted request
    /// with staff-confirmed requirements, ready for the first proof upload.</summary>
    private async Task<(HttpClient Customer, Guid OrderId)> SubmitAndConfirmRequestAsync(string tag)
    {
        var services = _factory.Services;
        var email = $"proof-{tag}-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email, firstName: "Proof", lastName: tag);
        await TestSeed.VerifyCustomerAsync(services, email);
        var productId = await SeedCustomizableProductAsync(services, "Slogan or design?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var form = await customer.GetAsync($"/portal/shop/{productId}/order");
        var formHtml = await form.Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(formHtml);
        var optionId = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\\\"opt_([0-9a-f-]{36})\\\"").Groups[1].Value;
        var submit = await customer.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "1",
                [$"opt_{optionId}"] = "Logo on the back",
                ["__RequestVerificationToken"] = token
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);

        Guid orderId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            orderId = await db.Orders.OrderByDescending(o => o.CreatedDateUtc).Select(o => o.Id).FirstAsync();
        }

        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var workspacePage = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var adminToken = TestSeed.ExtractAntiforgeryToken(await workspacePage.Content.ReadAsStringAsync());
        var finalize = await owner.PostAsync($"/admin/orders/{orderId}/workspace/finalize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["deliveryFee"] = "0",
                ["discount"] = "0",
                ["tax"] = "0",
                ["__RequestVerificationToken"] = adminToken
            }));
        finalize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return (customer, orderId);
    }

    private async Task UploadProofAsync(Guid orderId, string file, string? notes = null)
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await sender.Send(new DeChimex.Application.Features.Orders.Commands.UploadDesignProofCommand(
            orderId, new UploadDesignProofRequest(
                $"proofs/{file}", file, $"https://example.test/proofs/{file}", "image/png", notes)));
    }

    [Fact]
    public async Task DesignProof_ChangeRequest_NotifiesStaffAndKeepsFullHistory()
    {
        var services = _factory.Services;
        var (customer, orderId) = await SubmitAndConfirmRequestAsync("history");
        await UploadProofAsync(orderId, "v1.png", "First draft");

        Guid v1;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            v1 = await db.OrderDesignProofs.Where(p => p.OrderId == orderId).Select(p => p.Id).SingleAsync();
        }

        // Customer requests changes via the workspace form.
        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var custToken = TestSeed.ExtractAntiforgeryToken(await workspace.Content.ReadAsStringAsync());
        var changes = await customer.PostAsync($"/portal/orders/{orderId}/workspace/proof/{v1}/changes",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["feedback"] = "Make the logo bigger and darker",
                ["__RequestVerificationToken"] = custToken
            }));
        changes.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var proof = await db.OrderDesignProofs.FirstAsync(p => p.Id == v1);
            proof.Status.Should().Be(DesignProofStatus.ChangesRequested);
            proof.CustomerFeedback.Should().Be("Make the logo bigger and darker");
            var order = await db.Orders.Include(o => o.Timeline).FirstAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.InDiscussion);
            order.Timeline.Should().Contain(t => t.Type == "proof-changes-requested");
            var note = await db.Notifications
                .Where(n => n.Type == NotificationType.DesignProofChangesRequested
                    && n.Link == $"/admin/orders/{orderId}/workspace")
                .SingleAsync();
            note.Message.Should().Contain("logo bigger");
        }

        // The customer workspace shows the rejected version with its reason and timestamps.
        var afterChanges = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var afterHtml = await afterChanges.Content.ReadAsStringAsync();
        afterHtml.Should().Contain("Make the logo bigger and darker");
        afterHtml.Should().Contain("Submitted");

        // Staff upload v2; the rejected v1 keeps its status and reason.
        await UploadProofAsync(orderId, "v2.png", "Revised draft");
        Guid v2;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var proofs = await db.OrderDesignProofs.Where(p => p.OrderId == orderId).ToListAsync();
            proofs.Should().HaveCount(2);
            proofs.First(p => p.Id == v1).Status.Should().Be(DesignProofStatus.ChangesRequested);
            proofs.First(p => p.Id == v1).CustomerFeedback.Should().Be("Make the logo bigger and darker");
            v2 = proofs.Single(p => p.Version == 2).Id;
            proofs.Single(p => p.Version == 2).Status.Should().Be(DesignProofStatus.PendingApproval);
        }

        // Customer approves v2; the full version/decision history is retained.
        var workspace2 = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var custToken2 = TestSeed.ExtractAntiforgeryToken(await workspace2.Content.ReadAsStringAsync());
        var approve = await customer.PostAsync($"/portal/orders/{orderId}/workspace/proof/{v2}/approve",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = custToken2
            }));
        approve.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var proofs = await db.OrderDesignProofs.Where(p => p.OrderId == orderId).ToListAsync();
            proofs.First(p => p.Id == v1).Status.Should().Be(DesignProofStatus.ChangesRequested);
            var approved = proofs.Single(p => p.Id == v2);
            approved.Status.Should().Be(DesignProofStatus.Approved);
            approved.ApprovedAtUtc.Should().NotBeNull();
            (await db.Orders.Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync())
                .Should().Be(OrderStatus.RequirementsConfirmed);
            (await db.Notifications.AnyAsync(n => n.Type == NotificationType.DesignProofApproved
                && n.Link == $"/admin/orders/{orderId}/workspace")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task DesignProof_ApproveAfterChangeRequest_StillNotifiesStaff()
    {
        var services = _factory.Services;
        var (customer, orderId) = await SubmitAndConfirmRequestAsync("lateapprove");
        await UploadProofAsync(orderId, "v1.png");

        Guid proofId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            proofId = await db.OrderDesignProofs.Where(p => p.OrderId == orderId).Select(p => p.Id).SingleAsync();
        }

        // Change request moves the order back to discussion; the customer then
        // approves the same version anyway. The decision must still reach staff.
        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var token1 = TestSeed.ExtractAntiforgeryToken(await workspace.Content.ReadAsStringAsync());
        var changes = await customer.PostAsync($"/portal/orders/{orderId}/workspace/proof/{proofId}/changes",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["feedback"] = "Actually the first draft is fine",
                ["__RequestVerificationToken"] = token1
            }));
        changes.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var workspace2 = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var token2 = TestSeed.ExtractAntiforgeryToken(await workspace2.Content.ReadAsStringAsync());
        var approve = await customer.PostAsync($"/portal/orders/{orderId}/workspace/proof/{proofId}/approve",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token2
            }));
        approve.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var proof = await db.OrderDesignProofs.FirstAsync(p => p.Id == proofId);
            proof.Status.Should().Be(DesignProofStatus.Approved);
            proof.CustomerFeedback.Should().Be("Actually the first draft is fine",
                "approval must not wipe the recorded change-request reason");
            (await db.Orders.Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync())
                .Should().Be(OrderStatus.InDiscussion);
            (await db.Notifications.AnyAsync(n => n.Type == NotificationType.DesignProofApproved
                && n.Link == $"/admin/orders/{orderId}/workspace")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Workspace_OtherCustomerCannotViewOrder()
    {
        var services = _factory.Services;

        var ownerEmail = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, ownerEmail, firstName: "Real", lastName: "Owner");
        await TestSeed.VerifyCustomerAsync(services, ownerEmail);

        var productId = await SeedCustomizableProductAsync(services, "Any special instructions?");

        var ownerBrowser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(ownerBrowser, ownerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var form = await ownerBrowser.GetAsync($"/portal/shop/{productId}/order");
        var formHtml = await form.Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(formHtml);
        var optionId = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\"opt_([0-9a-f-]{36})\"").Groups[1].Value;
        await ownerBrowser.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "1",
                [$"opt_{optionId}"] = "Matte finish",
                ["__RequestVerificationToken"] = token
            }));

        Guid orderId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            orderId = await db.Orders.OrderByDescending(o => o.CreatedDateUtc).Select(o => o.Id).FirstAsync();
        }

        // A different customer cannot open the workspace of someone else's order.
        var intruderEmail = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, intruderEmail, firstName: "Nosy", lastName: "Neighbour");
        await TestSeed.VerifyCustomerAsync(services, intruderEmail);
        var intruder = CreateBrowser();
        (await TestSeed.LoginMvcAsync(intruder, intruderEmail, TestSeed.DefaultPassword)).Should().BeTrue();

        var forbidden = await intruder.GetAsync($"/portal/orders/{orderId}/workspace");
        forbidden.StatusCode.Should().Be(HttpStatusCode.NotFound, "foreign orders are reported as not found, not leaked");
    }

    [Fact]
    public async Task ChatMessages_RenderSymbolsWithoutDoubleEncoding()
    {
        // Regression: the workspace views wrapped message bodies in
        // WebUtility.HtmlEncode(...) INSIDE a Razor expression, so Razor encoded the
        // already-encoded text a second time and every apostrophe/quote/ampersand was
        // displayed to the user as a literal entity ("I&#39;m" instead of "I'm").
        // Razor's @ already HTML-encodes once — the bodies must render single-encoded.
        var services = _factory.Services;
        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email, firstName: "Sym", lastName: "Bol");
        await TestSeed.VerifyCustomerAsync(services, email);

        var productId = await SeedCustomizableProductAsync(services, "What colour do you want?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var form = await customer.GetAsync($"/portal/shop/{productId}/order");
        var formHtml = await form.Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(formHtml);
        var optionId = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\"opt_([0-9a-f-]{36})\"").Groups[1].Value;

        var submit = await customer.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "1",
                [$"opt_{optionId}"] = "Black",
                ["__RequestVerificationToken"] = token
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);

        Guid orderId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            orderId = await db.Orders.OrderByDescending(o => o.CreatedDateUtc).Select(o => o.Id).FirstAsync();
        }

        const string body = "It's 100% \"ready\" & <b>bold</b> + it's a go";
        var ws = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var wsHtml = await ws.Content.ReadAsStringAsync();
        var msgToken = TestSeed.ExtractAntiforgeryToken(wsHtml);
        var send = await customer.PostAsync($"/portal/orders/{orderId}/workspace/message",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["body"] = body,
                ["__RequestVerificationToken"] = msgToken
            }));
        send.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Both sides re-render: entities appear SINGLE-encoded (Razor escapes ' as
        // &#x27; and + as &#x2B;), never double-encoded (&amp;#39; / &amp;quot;).
        var customerAfter = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var customerHtml = await customerAfter.Content.ReadAsStringAsync();
        customerHtml.Should().Contain("It&#x27;s 100% &quot;ready&quot; &amp; &lt;b&gt;bold&lt;/b&gt; &#x2B; it&#x27;s a go");
        customerHtml.Should().NotContain("&amp;#x27;").And.NotContain("&amp;#39;").And.NotContain("&amp;quot;");

        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var admin = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var adminHtml = await admin.Content.ReadAsStringAsync();
        adminHtml.Should().Contain("It&#x27;s 100% &quot;ready&quot; &amp; &lt;b&gt;bold&lt;/b&gt; &#x2B; it&#x27;s a go");
        adminHtml.Should().NotContain("&amp;#x27;").And.NotContain("&amp;#39;").And.NotContain("&amp;quot;");
    }

    [Fact]
    public async Task Finalize_AcceptsMultipleExtraChargeLines_AndWorkspaceOffersAddRemove()
    {
        // Regression: staff must be able to price SEVERAL extra charges (design fee,
        // engraving, rush fee...) on one quote. The finalize action already parses the
        // repeated custom_desc/custom_price/custom_qty triplets as a full replacement
        // set; the workspace UI now offers "+ Add another line" and per-line remove
        // buttons so more than one line can actually be entered.
        var services = _factory.Services;
        var email = $"cust-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email, firstName: "Multi", lastName: "Line");
        await TestSeed.VerifyCustomerAsync(services, email);

        var productId = await SeedCustomizableProductAsync(services, "What colour do you want?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var form = await customer.GetAsync($"/portal/shop/{productId}/order");
        var formHtml = await form.Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(formHtml);
        var optionId = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\"opt_([0-9a-f-]{36})\"").Groups[1].Value;

        var submit = await customer.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "2",
                [$"opt_{optionId}"] = "Royal blue",
                ["__RequestVerificationToken"] = token
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);

        Guid orderId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            orderId = await db.Orders.OrderByDescending(o => o.CreatedDateUtc).Select(o => o.Id).FirstAsync();
        }

        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var workspacePage = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var adminHtml = await workspacePage.Content.ReadAsStringAsync();
        adminHtml.Should().Contain("id=\"addCustomLine\"", "the add-another-line button is rendered");
        adminHtml.Should().Contain("remove-line", "every custom line carries a remove button");

        var adminToken = TestSeed.ExtractAntiforgeryToken(adminHtml);
        // 2 × 15,000 products + (3,000×1 design) + (1,250×2 rush) + 2,000 delivery = 37,500.
        // Repeated custom_* keys post one triplet per extra charge line (the same wire
        // format the workspace's "+ Add another line" rows produce).
        var finalize = await owner.PostAsync($"/admin/orders/{orderId}/workspace/finalize",
            new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new($"line_{productId}_qty", "2"),
                new($"line_{productId}_price", "15000"),
                new("custom_desc", "Logo design"),
                new("custom_price", "3000"),
                new("custom_qty", "1"),
                new("custom_desc", "Rush handling"),
                new("custom_price", "1250"),
                new("custom_qty", "2"),
                new("deliveryFee", "2000"),
                new("discount", "0"),
                new("tax", "0"),
                new("__RequestVerificationToken", adminToken)
            }));
        finalize.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            // Include Items AND CustomLines: GrandTotal aggregates both collections, and an
            // unincluded navigation would read as empty and skew the expected total.
            var order = await db.Orders.Include(o => o.Items).Include(o => o.CustomLines)
                .FirstAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.PendingPayment);
            order.CustomLines.Should().HaveCount(2, "both extra charge lines are persisted");
            order.CustomLines.Should().Contain(c => c.Description == "Logo design" && c.UnitPrice.Amount == 3000m && c.Quantity == 1);
            order.CustomLines.Should().Contain(c => c.Description == "Rush handling" && c.UnitPrice.Amount == 1250m && c.Quantity == 2);
            order.GrandTotal.Amount.Should().Be(37500m);
        }

        // The customer quote lists both extra lines with their computed totals.
        var quote = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var quoteHtml = await quote.Content.ReadAsStringAsync();
        quoteHtml.Should().Contain("Logo design").And.Contain("Rush handling");
        quoteHtml.Should().Contain("₦37,500.00");
    }
}
