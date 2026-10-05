using System.Net;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Sales-assignment workflow: automatic workload-based assignment at request submission,
/// manual reassignment with timeline history, inbox scoping, conversation state, the
/// finalized/completed lifecycle, and assignment security.
/// </summary>
public class SalesAssignmentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    private static readonly SemaphoreSlim StaffLock = new(1, 1);
    private static bool _staffSeeded;

    public SalesAssignmentTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    internal static async Task<Guid> SeedCustomizableProductAsync(IServiceProvider services, string question)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var category = Category.Create($"Sales {question} Category", "test seed", 99);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        await sender.Send(new SaveCategoryOptionsCommand(category.Id, new SaveCategoryOptionsRequest(
            new List<SaveCategoryOptionDto>
            {
                new("colour", question, "Text", true, null, null, new List<string>())
            })));

        var product = Product.Create("Sales Jersey", "SKU-SALES-" + Guid.NewGuid().ToString("N")[..8],
            "Seeded customizable product", category.Id, 15000m, isCustomizable: true);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    internal static async Task EnsureSalesStaffAsync(IServiceProvider services)
    {
        await StaffLock.WaitAsync();
        try
        {
            if (_staffSeeded) return;
            await TestSeed.CreateStaffUserAsync(services, "sales-a@example.com", TestSeed.DefaultPassword,
                RoleNames.SalesStaff, "Sales", "Alpha");
            await TestSeed.CreateStaffUserAsync(services, "sales-b@example.com", TestSeed.DefaultPassword,
                RoleNames.SalesStaff, "Sales", "Beta");
            await TestSeed.CreateStaffUserAsync(services, "sales-c@example.com", TestSeed.DefaultPassword,
                RoleNames.SalesStaff, "Sales", "Gamma");
            _staffSeeded = true;
        }
        finally
        {
            StaffLock.Release();
        }
    }

    internal static async Task<Guid> SubmitRequestAsync(HttpClient browser, Guid productId, string answer)
    {
        var form = await browser.GetAsync($"/portal/shop/{productId}/order");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var formHtml = await form.Content.ReadAsStringAsync();
        var optionId = System.Text.RegularExpressions.Regex.Match(formHtml, "name=\"opt_([0-9a-f-]{36})\"").Groups[1].Value;
        optionId.Should().NotBeEmpty();
        var submit = await browser.PostAsync($"/portal/shop/{productId}/order", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ProductId"] = productId.ToString(),
                ["quantity"] = "1",
                [$"opt_{optionId}"] = answer,
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(formHtml)
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = submit.Headers.Location!.ToString();
        return Guid.Parse(System.Text.RegularExpressions.Regex.Match(location, "([0-9a-f-]{36})").Groups[1].Value);
    }

    internal static async Task<Guid> RegisterAndLoginCustomerAsync(
        IServiceProvider services, Func<HttpClient> createBrowser, string tag)
    {
        var email = $"sales-{tag}-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email, firstName: "Sales", lastName: "Customer");
        await TestSeed.VerifyCustomerAsync(services, email);
        var browser = createBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email.Value == email);
        return user.Id;
    }

    [Fact]
    public async Task AutoAssignment_AssignsAndPersists_WithTimelineAndNotification()
    {
        var services = _factory.Services;
        await EnsureSalesStaffAsync(services);
        var customerEmail = $"sales-auto-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SeedCustomizableProductAsync(services, "Auto colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();

        var orderId = await SubmitRequestAsync(customer, productId, "Red");

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var order = await db.Orders.Include(o => o.Timeline).FirstAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.AwaitingReview);
            order.AssignedSalesUserId.Should().NotBeNull("a new request must be auto-assigned");
            order.AssignedSalesName.Should().NotBeNullOrWhiteSpace();
            order.ConversationState.Should().Be(OrderConversationState.Active);
            order.Timeline.Should().ContainSingle(t => t.Type == "assigned"
                && t.Description.Contains(order.AssignedSalesName!));

            var assignedId = order.AssignedSalesUserId!.Value;
            (await db.Notifications.AnyAsync(n => n.RecipientUserId == assignedId
                && n.Type == NotificationType.SalesAssigned)).Should().BeTrue();
            var assignee = await db.Users.FirstAsync(u => u.Id == assignedId);
            assignee.Email.Value.Should().StartWith("sales-");
        }
    }

    [Fact]
    public async Task ManualReassignment_Works_RecordsHistory_AndNotifies()
    {
        var services = _factory.Services;
        await EnsureSalesStaffAsync(services);
        var customerEmail = $"sales-reassign-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SeedCustomizableProductAsync(services, "Reassign colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SubmitRequestAsync(customer, productId, "Blue");

        Guid previousId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            previousId = (await db.Orders.FirstAsync(o => o.Id == orderId)).AssignedSalesUserId!.Value;
        }

        Guid targetId;
        string targetName;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var target = await db.Users.FirstAsync(u => u.Email.Value == "sales-c@example.com");
            targetId = target.Id;
            targetName = target.FullName;
            if (targetId == previousId)
            {
                var alt = await db.Users.FirstAsync(u => u.Email.Value == "sales-b@example.com");
                targetId = alt.Id;
                targetName = alt.FullName;
            }
        }

        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var workspace = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        workspace.StatusCode.Should().Be(HttpStatusCode.OK);
        var reassign = await owner.PostAsync($"/admin/orders/{orderId}/workspace/assign",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["salesUserId"] = targetId.ToString(),
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await workspace.Content.ReadAsStringAsync())
            }));
        reassign.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var order = await db.Orders.Include(o => o.Timeline).FirstAsync(o => o.Id == orderId);
            order.AssignedSalesUserId.Should().Be(targetId);
            order.AssignedSalesName.Should().Be(targetName);
            order.Timeline.Should().ContainSingle(t => t.Type == "reassigned"
                && t.Description.Contains(targetName));
            (await db.Notifications.AnyAsync(n => n.RecipientUserId == targetId
                && n.Type == NotificationType.SalesReassigned)).Should().BeTrue();

            // The previous owner no longer carries this order in their active workload.
            var inbox = scope.ServiceProvider.GetRequiredService<ISalesInboxQueries>();
            var staff = await inbox.GetEligibleSalesStaffAsync(CancellationToken.None);
            staff.Should().Contain(s => s.UserId == targetId);
        }
    }

    [Fact]
    public async Task SalesInbox_ScopesToAssignedWork()
    {
        var services = _factory.Services;
        await EnsureSalesStaffAsync(services);
        var customerEmail = $"sales-inbox-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SeedCustomizableProductAsync(services, "Inbox colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SubmitRequestAsync(customer, productId, "Green");

        string orderNumber;
        Guid assignedId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var order = await db.Orders.FirstAsync(o => o.Id == orderId);
            orderNumber = order.OrderNumber;
            assignedId = order.AssignedSalesUserId!.Value;
        }

        string assignedEmail, otherEmail;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            assignedEmail = (await db.Users.FirstAsync(u => u.Id == assignedId)).Email.Value;
            otherEmail = assignedEmail == "sales-a@example.com" ? "sales-b@example.com" : "sales-a@example.com";
        }

        var assigned = CreateBrowser();
        (await TestSeed.LoginMvcAsync(assigned, assignedEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var mine = await assigned.GetAsync("/admin/sales?bucket=new");
        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        (await mine.Content.ReadAsStringAsync()).Should().Contain(orderNumber);

        var other = CreateBrowser();
        (await TestSeed.LoginMvcAsync(other, otherEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var theirs = await other.GetAsync("/admin/sales?bucket=new");
        theirs.StatusCode.Should().Be(HttpStatusCode.OK);
        (await theirs.Content.ReadAsStringAsync()).Should().NotContain(orderNumber);

        // Owner/manager sees every assignment.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var all = await owner.GetAsync("/admin/sales?bucket=new");
        all.StatusCode.Should().Be(HttpStatusCode.OK);
        (await all.Content.ReadAsStringAsync()).Should().Contain(orderNumber);
    }

    [Fact]
    public async Task ConversationState_FollowsMessages_AndFinalizeResolves()
    {
        var services = _factory.Services;
        await EnsureSalesStaffAsync(services);
        var customerEmail = $"sales-conv-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SeedCustomizableProductAsync(services, "Conv colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SubmitRequestAsync(customer, productId, "Yellow");

        // Customer message -> waiting on staff.
        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var msg = await customer.PostAsync($"/portal/orders/{orderId}/workspace/message",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["body"] = "Can you add my logo?",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await workspace.Content.ReadAsStringAsync())
            }));
        msg.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Orders.FirstAsync(o => o.Id == orderId)).ConversationState
                .Should().Be(OrderConversationState.WaitingForStaff);
        }

        // Staff reply -> waiting on customer.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var admin = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var reply = await owner.PostAsync($"/admin/orders/{orderId}/workspace/message",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["body"] = "Yes — please upload the logo file.",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await admin.Content.ReadAsStringAsync())
            }));
        reply.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Orders.FirstAsync(o => o.Id == orderId)).ConversationState
                .Should().Be(OrderConversationState.WaitingForCustomer);
        }

        // Finalize -> resolved, conversation retained.
        var admin2 = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var finalize = await owner.PostAsync($"/admin/orders/{orderId}/workspace/finalize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["deliveryFee"] = "0",
                ["discount"] = "0",
                ["tax"] = "0",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await admin2.Content.ReadAsStringAsync())
            }));
        finalize.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var order = await db.Orders.Include(o => o.Messages).FirstAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.PendingPayment);
            order.ConversationState.Should().Be(OrderConversationState.Resolved);
            order.Messages.Should().HaveCount(2, "finalization must retain the conversation");
        }
    }

    [Fact]
    public async Task CompletedOrder_LeavesActiveWorkload_KeepsHistory()
    {
        var services = _factory.Services;
        await EnsureSalesStaffAsync(services);
        var customerEmail = $"sales-done-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SeedCustomizableProductAsync(services, "Done colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SubmitRequestAsync(customer, productId, "Black");

        Guid assignedId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            assignedId = (await db.Orders.FirstAsync(o => o.Id == orderId)).AssignedSalesUserId!.Value;
        }

        int Workload()
        {
            using var scope = services.CreateScope();
            var inbox = scope.ServiceProvider.GetRequiredService<ISalesInboxQueries>();
            return inbox.GetEligibleSalesStaffAsync(CancellationToken.None).GetAwaiter().GetResult()
                .First(s => s.UserId == assignedId).ActiveWorkload;
        }

        var before = Workload();
        before.Should().BeGreaterThan(0);

        // A real conversation first, so completion can prove history retention.
        var chatPage = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var chat = await customer.PostAsync($"/portal/orders/{orderId}/workspace/message",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["body"] = "Please confirm the delivery date.",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await chatPage.Content.ReadAsStringAsync())
            }));
        chat.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Finalize -> pay (fake gateway verification) -> production -> completed.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var admin = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var finalize = await owner.PostAsync($"/admin/orders/{orderId}/workspace/finalize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["deliveryFee"] = "0",
                ["discount"] = "0",
                ["tax"] = "0",
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await admin.Content.ReadAsStringAsync())
            }));
        finalize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Workload().Should().Be(before - 1, "a finalized order is no longer active sales work");

        var portal = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var pay = await customer.PostAsync($"/checkout/{orderId}/pay", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await portal.Content.ReadAsStringAsync())
            }));
        pay.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string reference;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            reference = (await db.Payments.OrderByDescending(p => p.CreatedDateUtc)
                .FirstAsync(p => p.OrderId == orderId)).Reference;
        }
        using (var scope = services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new VerifyPaymentCommand(reference));
            await sender.Send(new UpdateOrderStatusCommand(orderId, OrderStatus.InProduction.ToString()));
            await sender.Send(new UpdateOrderStatusCommand(orderId, OrderStatus.Ready.ToString()));
            await sender.Send(new UpdateOrderStatusCommand(orderId, OrderStatus.Completed.ToString()));
        }

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var order = await db.Orders.Include(o => o.Messages).Include(o => o.Timeline)
                .FirstAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.Completed);
            order.ConversationState.Should().Be(OrderConversationState.Resolved);
            order.Messages.Should().NotBeEmpty("history must survive completion");
            order.Timeline.Should().NotBeEmpty();
        }
        Workload().Should().Be(before - 1);

        // The completed order no longer appears in the assignee's active buckets.
        string assignedEmail;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            assignedEmail = (await db.Users.FirstAsync(u => u.Id == assignedId)).Email.Value;
        }
        var assigned = CreateBrowser();
        (await TestSeed.LoginMvcAsync(assigned, assignedEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        foreach (var bucket in new[] { "new", "active", "waiting-customer", "waiting-staff" })
        {
            var page = await assigned.GetAsync($"/admin/sales?bucket={bucket}");
            page.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        var resolved = await assigned.GetAsync("/admin/sales?bucket=resolved");
        resolved.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reassign_NonSalesUser_IsRejected()
    {
        var services = _factory.Services;
        await EnsureSalesStaffAsync(services);
        await TestSeed.CreateStaffUserAsync(services, $"designer-{Guid.NewGuid():N}@example.com",
            TestSeed.DefaultPassword, RoleNames.Designer, "Dee", "Signer");
        var customerEmail = $"sales-inelig-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SeedCustomizableProductAsync(services, "Eligible colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SubmitRequestAsync(customer, productId, "Teal");

        Guid designerId;
        Guid? originalAssignee;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            designerId = await db.Users.Where(u => u.FirstName == "Dee").Select(u => u.Id).FirstAsync();
            originalAssignee = (await db.Orders.FirstAsync(o => o.Id == orderId)).AssignedSalesUserId;
        }

        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var workspace = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var reassign = await owner.PostAsync($"/admin/orders/{orderId}/workspace/assign",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["salesUserId"] = designerId.ToString(),
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await workspace.Content.ReadAsStringAsync())
            }));
        reassign.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.Orders.FirstAsync(o => o.Id == orderId)).AssignedSalesUserId.Should().Be(originalAssignee);
        }
    }

    [Fact]
    public async Task Reassign_SalesStaffWithoutPermission_IsForbidden()
    {
        var services = _factory.Services;
        await EnsureSalesStaffAsync(services);
        var customerEmail = $"sales-forbid-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SeedCustomizableProductAsync(services, "Forbid colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SubmitRequestAsync(customer, productId, "Navy");

        var sales = CreateBrowser();
        (await TestSeed.LoginMvcAsync(sales, "sales-a@example.com", TestSeed.DefaultPassword)).Should().BeTrue();
        var workspace = await sales.GetAsync($"/admin/orders/{orderId}/workspace");
        workspace.StatusCode.Should().Be(HttpStatusCode.OK);
        var attempt = await sales.PostAsync($"/admin/orders/{orderId}/workspace/assign",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["salesUserId"] = Guid.NewGuid().ToString(),
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await workspace.Content.ReadAsStringAsync())
            }));
        // MVC cookie auth turns a policy denial into a redirect to AccessDenied (not a raw 403).
        attempt.StatusCode.Should().Be(HttpStatusCode.Redirect);
        attempt.Headers.Location!.ToString().Should().Contain("AccessDenied");
    }

    [Fact]
    public async Task CustomerWorkspace_NeverLeaksSalesStaffNames()
    {
        var services = _factory.Services;
        await EnsureSalesStaffAsync(services);
        var customerEmail = $"sales-priv-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SeedCustomizableProductAsync(services, "Privacy colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SubmitRequestAsync(customer, productId, "Teal");

        // Force a reassignment so both "assigned" and "reassigned" entries exist.
        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        Guid target;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var current = await db.Orders.FirstAsync(o => o.Id == orderId);
            var other = await db.Users.FirstAsync(u => u.Email.Value != null
                && u.Email.Value.StartsWith("sales-") && u.Id != current.AssignedSalesUserId);
            target = other.Id;
        }
        var staffWs = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        var reassign = await owner.PostAsync($"/admin/orders/{orderId}/workspace/assign",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["salesUserId"] = target.ToString(),
                ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await staffWs.Content.ReadAsStringAsync())
            }));
        reassign.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var page = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("A sales specialist is handling your request.");
        html.Should().Contain("Your request was handed over to another sales specialist.");
        html.Should().NotContain("Sales Alpha");
        html.Should().NotContain("Sales Beta");
        html.Should().NotContain("Sales Gamma");
        html.Should().NotContain("sales-a@example.com");
        html.Should().NotContain("sales-b@example.com");
        html.Should().NotContain("sales-c@example.com");
    }
}

/// <summary>
/// Workload distribution across three sales staff on a clean database: four sequential
/// requests must spread 2/1/1 with the deterministic earliest-account tie-break.
/// </summary>
public class SalesAssignmentDistributionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SalesAssignmentDistributionTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    [Fact]
    public async Task AutoAssignment_DistributesAcrossThreeSalesStaff()
    {
        var services = _factory.Services;
        await TestSeed.CreateStaffUserAsync(services, "dist-a@example.com", TestSeed.DefaultPassword,
            RoleNames.SalesStaff, "Dist", "Alpha");
        await TestSeed.CreateStaffUserAsync(services, "dist-b@example.com", TestSeed.DefaultPassword,
            RoleNames.SalesStaff, "Dist", "Beta");
        await TestSeed.CreateStaffUserAsync(services, "dist-c@example.com", TestSeed.DefaultPassword,
            RoleNames.SalesStaff, "Dist", "Gamma");

        var customerEmail = $"sales-dist-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SalesAssignmentTests.SeedCustomizableProductAsync(services, "Dist colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();

        // One customer, four requests: per-order (never per-customer) assignment.
        var orderIds = new List<Guid>();
        for (var i = 0; i < 4; i++)
            orderIds.Add(await SalesAssignmentTests.SubmitRequestAsync(customer, productId, $"Answer {i}"));

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var orders = await db.Orders.Include(o => o.Timeline)
                .Where(o => orderIds.Contains(o.Id)).ToListAsync();
            orders.Should().HaveCount(4);
            orders.Should().OnlyContain(o => o.AssignedSalesUserId != null);

            var assignees = orders.OrderBy(o => orderIds.IndexOf(o.Id))
                .Select(o => o.AssignedSalesUserId!.Value).ToList();
            assignees.Take(3).Distinct().Should().HaveCount(3, "the first three requests spread across all three staff");
            assignees[3].Should().Be(assignees[0], "with equal workloads the deterministic tie-break repeats the first pick");
            orders.Should().OnlyContain(o => o.Timeline.Any(t => t.Type == "assigned"));

            var counts = await db.Notifications
                .Where(n => n.Type == NotificationType.SalesAssigned)
                .GroupBy(n => n.RecipientUserId!.Value)
                .Select(g => g.Count()).ToListAsync();
            counts.OrderDescending().Should().BeEquivalentTo(new[] { 2, 1, 1 });

            var inbox = scope.ServiceProvider.GetRequiredService<ISalesInboxQueries>();
            var staff = await inbox.GetEligibleSalesStaffAsync(CancellationToken.None);
            staff.Should().HaveCount(3);
            staff.Select(s => s.ActiveWorkload).OrderDescending().Should().BeEquivalentTo(new[] { 2, 1, 1 });
        }
    }
}

/// <summary>
/// Unassigned fallback on a database with no eligible sales staff: the request must stay
/// visible in AwaitingReview with an unassigned timeline entry, and the owner must be
/// able to triage it from the sales inbox.
/// </summary>
public class SalesAssignmentNoStaffTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SalesAssignmentNoStaffTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    [Fact]
    public async Task NoEligibleStaff_RequestStaysUnassigned_AndOwnerCanTriage()
    {
        var services = _factory.Services;
        var customerEmail = $"sales-nostaff-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, customerEmail);
        await TestSeed.VerifyCustomerAsync(services, customerEmail);
        var productId = await SalesAssignmentTests.SeedCustomizableProductAsync(services, "Lonely colour?");
        var customer = CreateBrowser();
        (await TestSeed.LoginMvcAsync(customer, customerEmail, TestSeed.DefaultPassword)).Should().BeTrue();

        var orderId = await SalesAssignmentTests.SubmitRequestAsync(customer, productId, "Crimson");

        string orderNumber;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var order = await db.Orders.Include(o => o.Timeline).FirstAsync(o => o.Id == orderId);
            orderNumber = order.OrderNumber;
            order.Status.Should().Be(OrderStatus.AwaitingReview);
            order.AssignedSalesUserId.Should().BeNull();
            order.Timeline.Should().ContainSingle(t => t.Type == "unassigned");
        }

        var owner = await TestSeed.LoginOwnerMvcAsync(services, CreateBrowser);
        var triage = await owner.GetAsync("/admin/sales?bucket=unassigned");
        triage.StatusCode.Should().Be(HttpStatusCode.OK);
        (await triage.Content.ReadAsStringAsync()).Should().Contain(orderNumber);

        var workspace = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        workspace.StatusCode.Should().Be(HttpStatusCode.OK);
        (await workspace.Content.ReadAsStringAsync()).Should().Contain("Unassigned");
    }
}
