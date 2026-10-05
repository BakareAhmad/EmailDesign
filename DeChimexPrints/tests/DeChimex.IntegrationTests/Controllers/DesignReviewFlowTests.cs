using System.Net;
using System.Net.Http.Headers;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// End-to-end coverage of the design-review workflow (OrderDesignReview):
/// customers and staff upload design files (independent history — no supersede),
/// staff approve/reject with a reason, both workspaces render the decision, and
/// unauthorized actors (other customers, customers on staff routes) are blocked.
/// </summary>
public class DesignReviewFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly byte[] Png1X1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly CustomWebApplicationFactory _factory;

    public DesignReviewFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<Guid> SeedCustomizableProductAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();

        var category = Category.Create($"DR {Guid.NewGuid():N}"[..35], "test seed", 99);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        await sender.Send(new SaveCategoryOptionsCommand(category.Id, new SaveCategoryOptionsRequest(
            new List<SaveCategoryOptionDto>
            {
                new("brief", "Any brief?", "Text", true, null, null, new List<string>())
            })));

        var product = Product.Create("DR Jersey", "SKU-DR-" + Guid.NewGuid().ToString("N")[..8],
            "Seeded customizable product", category.Id, 15000m, isCustomizable: true);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>Registers a verified customer, submits an order request, returns
    /// (browser, orderId). The order sits in AwaitingReview, ready for uploads.</summary>
    private async Task<(HttpClient Customer, Guid OrderId)> SubmitRequestAsync()
    {
        var services = _factory.Services;
        var email = $"dr-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email, firstName: "Design", lastName: "Reviewer");
        await TestSeed.VerifyCustomerAsync(services, email);

        var productId = await SeedCustomizableProductAsync(services);
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
                [$"opt_{optionId}"] = "Red with gold text",
                ["__RequestVerificationToken"] = token
            }));
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var orderId = await db.Orders.OrderByDescending(o => o.CreatedDateUtc).Select(o => o.Id).FirstAsync();
            return (customer, orderId);
        }
    }

    /// <summary>Uploads a design review over HTTP as the customer; asserts the
    /// redirect and returns the new review's id.</summary>
    private async Task<Guid> UploadAsCustomerAsync(HttpClient customer, Guid orderId,
        string fileName, string? notes)
    {
        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var html = await workspace.Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(html);

        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };
        if (notes is not null) form.Add(new StringContent(notes), "notes");
        var file = new ByteArrayContent(Png1X1);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", fileName);

        var response = await customer.PostAsync($"/portal/orders/{orderId}/workspace/designreview", form);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "a valid design review upload returns to the workspace");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            return await db.OrderDesignReviews
                .Where(r => r.OrderId == orderId && r.OriginalFileName == fileName)
                .OrderByDescending(r => r.CreatedDateUtc)
                .Select(r => r.Id)
                .FirstAsync();
        }
    }

    private async Task<HttpClient> LoginOwnerAsync()
        => await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);

    private async Task<(HttpClient Owner, string Token)> OwnerWorkspaceAsync(Guid orderId)
    {
        var owner = await LoginOwnerAsync();
        var page = await owner.GetAsync($"/admin/orders/{orderId}/workspace");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        return (owner, token);
    }

    [Fact]
    public async Task CustomerUpload_StaffApproves_FullFlow()
    {
        var (customer, orderId) = await SubmitRequestAsync();
        var reviewId = await UploadAsCustomerAsync(customer, orderId, "brand-logo.png", "First version of the logo");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var review = await db.OrderDesignReviews.SingleAsync(r => r.Id == reviewId);
            review.Status.Should().Be(DesignReviewStatus.Pending);
            review.UploadedByRole.Should().Be("customer");
            review.Notes.Should().Be("First version of the logo");
            review.SizeBytes.Should().BeGreaterThan(0);
            review.PreviewUrl.Should().NotBeNullOrWhiteSpace("image uploads get a preview");

            var order = await db.Orders.Include(o => o.Timeline).SingleAsync(o => o.Id == orderId);
            order.Timeline.Should().Contain(t => t.Type == "designreview-uploaded");
            order.ConversationState.Should().Be(OrderConversationState.WaitingForStaff,
                "the customer acted; staff must review");

            // Staff-side bell notification (permission-scoped) exists.
            (await db.Notifications.AnyAsync(n => n.Type == NotificationType.DesignReviewUploaded
                && n.Link == $"/admin/orders/{orderId}/workspace")).Should().BeTrue();
        }

        // The staff workspace renders the file, its status and decision controls.
        var (owner, token) = await OwnerWorkspaceAsync(orderId);
        var adminHtml = await (await owner.GetAsync($"/admin/orders/{orderId}/workspace")).Content.ReadAsStringAsync();
        adminHtml.Should().Contain("brand-logo.png");
        adminHtml.Should().Contain("Pending");
        adminHtml.Should().Contain("Approve");
        adminHtml.Should().Contain("Reject");

        // Staff approves; the decision is recorded and the customer is notified.
        var approve = await owner.PostAsync($"/admin/orders/{orderId}/workspace/designreview/{reviewId}/approve",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            }));
        approve.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var review = await db.OrderDesignReviews.SingleAsync(r => r.Id == reviewId);
            review.Status.Should().Be(DesignReviewStatus.Approved);
            review.ApprovedAtUtc.Should().NotBeNull();

            var order = await db.Orders.Include(o => o.Timeline).SingleAsync(o => o.Id == orderId);
            order.ConversationState.Should().Be(OrderConversationState.WaitingForCustomer,
                "staff acted; the ball is back with the customer");
            order.Timeline.Should().Contain(t => t.Type == "designreview-approved");

            (await db.Notifications.AnyAsync(n => n.Type == NotificationType.DesignReviewApproved
                && n.Link == $"/portal/orders/{orderId}/workspace")).Should().BeTrue();
        }

        // The customer sees the approved status.
        var portalHtml = await (await customer.GetAsync($"/portal/orders/{orderId}/workspace")).Content.ReadAsStringAsync();
        portalHtml.Should().Contain("brand-logo.png");
        portalHtml.Should().Contain("Approved");
    }

    [Fact]
    public async Task StaffRejectsWithReason_CustomerSeesReasonAndNotification()
    {
        var (customer, orderId) = await SubmitRequestAsync();
        var reviewId = await UploadAsCustomerAsync(customer, orderId, "artwork.png", "Please check bleed");

        var (owner, token) = await OwnerWorkspaceAsync(orderId);
        var reject = await owner.PostAsync($"/admin/orders/{orderId}/workspace/designreview/{reviewId}/reject",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["reason"] = "Missing 3mm bleed on all sides",
                ["__RequestVerificationToken"] = token
            }));
        reject.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var review = await db.OrderDesignReviews.SingleAsync(r => r.Id == reviewId);
            review.Status.Should().Be(DesignReviewStatus.Rejected);
            review.RejectionReason.Should().Be("Missing 3mm bleed on all sides");
            review.RejectedAtUtc.Should().NotBeNull();

            var note = await db.Notifications.SingleAsync(n => n.Type == NotificationType.DesignReviewRejected
                && n.Link == $"/portal/orders/{orderId}/workspace");
            note.Message.Should().Contain("Missing 3mm bleed");
        }

        // The customer workspace shows the rejection reason.
        var portalHtml = await (await customer.GetAsync($"/portal/orders/{orderId}/workspace")).Content.ReadAsStringAsync();
        portalHtml.Should().Contain("Missing 3mm bleed on all sides");
        portalHtml.Should().Contain("Rejected");
    }

    [Fact]
    public async Task Reject_WithoutReason_IsRefused()
    {
        var (customer, orderId) = await SubmitRequestAsync();
        var reviewId = await UploadAsCustomerAsync(customer, orderId, "no-reason.png", null);

        var (owner, token) = await OwnerWorkspaceAsync(orderId);
        var reject = await owner.PostAsync($"/admin/orders/{orderId}/workspace/designreview/{reviewId}/reject",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["reason"] = "   ",
                ["__RequestVerificationToken"] = token
            }));
        reject.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var review = await db.OrderDesignReviews.SingleAsync(r => r.Id == reviewId);
            review.Status.Should().Be(DesignReviewStatus.Pending, "a blank reason must not reject anything");
        }
    }

    [Fact]
    public async Task SecondSubmission_DoesNotSupersedeTheFirst()
    {
        var (customer, orderId) = await SubmitRequestAsync();
        var first = await UploadAsCustomerAsync(customer, orderId, "v1-logo.png", "v1");
        var second = await UploadAsCustomerAsync(customer, orderId, "v2-logo.png", "v2");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var reviews = await db.OrderDesignReviews.Where(r => r.OrderId == orderId).ToListAsync();
            reviews.Should().HaveCount(2, "design reviews keep independent history (no supersede)");
            reviews.Single(r => r.Id == first).Status.Should().Be(DesignReviewStatus.Pending);
            reviews.Single(r => r.Id == second).Status.Should().Be(DesignReviewStatus.Pending);
        }
    }

    [Fact]
    public async Task Customer_CannotUseStaffDecisionRoutes()
    {
        var (customer, orderId) = await SubmitRequestAsync();
        var reviewId = await UploadAsCustomerAsync(customer, orderId, "forbidden.png", "x");

        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var token = TestSeed.ExtractAntiforgeryToken(await workspace.Content.ReadAsStringAsync());

        var approve = await customer.PostAsync($"/admin/orders/{orderId}/workspace/designreview/{reviewId}/approve",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            }));

        // Authenticated customers never carry Orders.UpdateStatus: the policy blocks
        // the route (login redirect / access-denied / forbidden — never a decision).
        approve.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Forbidden);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var review = await db.OrderDesignReviews.SingleAsync(r => r.Id == reviewId);
            review.Status.Should().Be(DesignReviewStatus.Pending);
        }
    }

    [Fact]
    public async Task OtherCustomer_CannotUploadToSomeoneElsesOrder()
    {
        var (customer, orderId) = await SubmitRequestAsync();

        var intruderEmail = $"dr-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(_factory.Services, intruderEmail, firstName: "Nosy", lastName: "Neighbour");
        await TestSeed.VerifyCustomerAsync(_factory.Services, intruderEmail);
        var intruder = CreateBrowser();
        (await TestSeed.LoginMvcAsync(intruder, intruderEmail, TestSeed.DefaultPassword)).Should().BeTrue();

        var workspace = await intruder.GetAsync($"/portal/orders/{orderId}/workspace");
        workspace.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another customer's order workspace does not exist for this user");

        // A valid session token (the foreign workspace page never renders, so the
        // antiforgery token must come from a page the intruder CAN access).
        var list = await intruder.GetAsync("/portal/orders");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = TestSeed.ExtractAntiforgeryToken(await list.Content.ReadAsStringAsync());
        token.Should().NotBeNullOrWhiteSpace();

        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(Png1X1);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "intruder.png");

        var response = await intruder.PostAsync($"/portal/orders/{orderId}/workspace/designreview", form);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the ownership check reports foreign orders as not found");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.OrderDesignReviews.AnyAsync(r => r.OrderId == orderId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task DisallowedFileType_IsRejected()
    {
        var (customer, orderId) = await SubmitRequestAsync();

        var workspace = await customer.GetAsync($"/portal/orders/{orderId}/workspace");
        var html = await workspace.Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(html);

        var form = new MultipartFormDataContent
        {
            { new StringContent("evil"), "notes" },
            { new StringContent(token), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "malware.exe");

        var response = await customer.PostAsync($"/portal/orders/{orderId}/workspace/designreview", form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the storage layer's extension allowlist surfaces as a validation error");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            (await db.OrderDesignReviews.AnyAsync(r => r.OrderId == orderId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task StaffUpload_CustomerSeesFileAndIsNotified()
    {
        var (customer, orderId) = await SubmitRequestAsync();

        var (owner, token) = await OwnerWorkspaceAsync(orderId);
        var form = new MultipartFormDataContent
        {
            { new StringContent("Our proposed layout"), "notes" },
            { new StringContent(token), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(Png1X1);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "staff-layout.png");

        var response = await owner.PostAsync($"/admin/orders/{orderId}/workspace/designreview", form);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var review = await db.OrderDesignReviews.SingleAsync(r => r.OrderId == orderId);
            review.UploadedByRole.Should().Be("staff");
            review.Status.Should().Be(DesignReviewStatus.Pending);

            // The customer is notified directly (staff acted).
            (await db.Notifications.AnyAsync(n => n.Type == NotificationType.DesignReviewUploaded
                && n.Link == $"/portal/orders/{orderId}/workspace")).Should().BeTrue();
        }

        var portalHtml = await (await customer.GetAsync($"/portal/orders/{orderId}/workspace")).Content.ReadAsStringAsync();
        portalHtml.Should().Contain("staff-layout.png");
        portalHtml.Should().Contain("Our proposed layout");
    }
}
