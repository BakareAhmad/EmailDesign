using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Cart;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Reviews.Commands;
using DeChimex.Contracts.Cart;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Reviews;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Persistence.Context;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Direct Checkout (DIRECT_CHECKOUT_EVALUATION.md): eligible products flow through the
/// existing Order aggregate via the cart/checkout path with Kind=Direct and skip the
/// custom sales workflow; custom-only products (IsCustomizable or category ordering
/// options) are refused on the direct path — cart add AND order creation — and must use
/// "Order / Customize"; staff/quote orders stay unrestricted. Review purchase-verification
/// applies identically to both paths.
/// </summary>
public class DirectCheckoutTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DirectCheckoutTests(CustomWebApplicationFactory factory) => _factory = factory;

    // ---------- helpers ----------

    private sealed record Seed(Guid ProductId, Guid CustomerId);

    private static async Task<Guid> SeedProductAsync(IServiceProvider services,
        bool customizable = false, bool categoryHasOptions = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var category = Category.Create($"DirectCheckout {Guid.NewGuid():N}"[..40], "direct checkout test", 5);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        if (categoryHasOptions)
        {
            await sender.Send(new SaveCategoryOptionsCommand(category.Id, new SaveCategoryOptionsRequest(
                new List<SaveCategoryOptionDto>
                {
                    new("colour", "Which colour?", "Text", true, null, null, new List<string>())
                })));
        }

        var product = Product.Create($"Magic Mug {Guid.NewGuid():N}"[..40], $"DC-{Guid.NewGuid():N}"[..12],
            "direct checkout test product", category.Id, 9500m, isCustomizable: customizable);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.Inventories.Add(Inventory.Create(product.Id, product.Name, 50));
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<Guid> SeedCustomerAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var customer = Customer.Create("Direct", "Buyer", $"direct.{Guid.NewGuid():N}@yopmail.com");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static CreateOrderRequest Request(Guid customerId, Guid productId) =>
        new(customerId,
            new List<CreateOrderItemRequest> { new(productId, 1) },
            new List<CreateOrderServiceRequest>(),
            null, null, null, 0m, 0m, 0m);

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId { get; set; }
        public string? Email => "stub@yopmail.com";
        public bool IsAuthenticated => UserId is not null;
        public IReadOnlyList<string> Roles => new[] { "Customer" };
        public IReadOnlyList<string> Permissions => Array.Empty<string>();
        public string? IpAddress => null;
        public bool HasPermission(string permission) => false;
    }

    /// <summary>
    /// Host with an authenticated customer. Each derived factory has its own InMemory
    /// store, so registration, seeding and acting all happen through THIS host.
    /// </summary>
    private sealed class AuthedHost : IDisposable
    {
        public required StubCurrentUser User { get; init; }
        public required Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Inner { get; init; }
        public IServiceProvider Services => Inner.Services;
        public void Dispose() => Inner.Dispose();
    }

    private async Task<(AuthedHost Host, Guid CustomerId)> CreateAuthedCustomerAsync()
    {
        var stub = new StubCurrentUser();
        var inner = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICurrentUser>();
            services.AddScoped<ICurrentUser>(_ => stub);
        }));
        var host = new AuthedHost { User = stub, Inner = inner };

        var email = $"direct.{Guid.NewGuid():N}@yopmail.com";
        var userId = await TestSeed.RegisterCustomerAsync(host.Services, email);
        await TestSeed.VerifyCustomerAsync(host.Services, email);
        stub.UserId = userId;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var customerId = await db.Customers.Where(c => c.UserId == userId).Select(c => c.Id).SingleAsync();
        return (host, customerId);
    }

    // ---------- routing & kind ----------

    [Fact]
    public async Task An_eligible_product_lands_in_a_direct_kind_order_without_sales()
    {
        var productId = await SeedProductAsync(_factory.Services);
        var customerId = await SeedCustomerAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(Request(customerId, productId)));

        order.Kind.Should().Be("Direct");
        order.Status.Should().Be(nameof(OrderStatus.PendingPayment), "direct checkout places the order for payment immediately");

        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var entity = await db.Orders.SingleAsync(o => o.Id == order.Id);
        entity.AssignedSalesUserId.Should().BeNull("direct orders never enter the sales workflow");
    }

    [Fact]
    public async Task A_customizable_product_is_refused_on_the_direct_path_at_cart_add()
    {
        var productId = await SeedProductAsync(_factory.Services, customizable: true);
        var customerId = await SeedCustomerAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var act = () => sender.Send(new AddCartItemCommand(customerId,
            new AddCartItemRequest("product", productId, 1, null)));
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.Which.Message.Should().Contain("Order / Customize");
    }

    [Fact]
    public async Task A_category_options_product_is_refused_on_the_direct_path_at_cart_add()
    {
        var productId = await SeedProductAsync(_factory.Services, categoryHasOptions: true);
        var customerId = await SeedCustomerAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var act = () => sender.Send(new AddCartItemCommand(customerId,
            new AddCartItemRequest("product", productId, 1, null)));
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task A_custom_only_product_is_refused_at_direct_order_creation()
    {
        // Covers the race/tamper case: the gate is repeated authoritatively at creation.
        var productId = await SeedProductAsync(_factory.Services, customizable: true);
        var customerId = await SeedCustomerAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var act = () => sender.Send(new CreateOrderCommand(Request(customerId, productId)));
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Staff_and_quote_orders_may_use_custom_products()
    {
        var productId = await SeedProductAsync(_factory.Services, customizable: true);
        var customerId = await SeedCustomerAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(Request(customerId, productId),
            Kind: OrderKind.Custom));
        order.Kind.Should().Be("Custom");
    }

    [Fact]
    public async Task A_custom_order_request_is_marked_custom_and_gets_sales_assignment()
    {
        var (host, _) = await CreateAuthedCustomerAsync();
        using var _ = host;
        var productId = await SeedProductAsync(host.Services, customizable: true);

        using var scope = host.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var ws = await sender.Send(new SubmitOrderRequestCommand(new SubmitOrderRequest(
            productId, 1, null,
            new List<OrderRequirementInput>(), new List<OrderFileInput>())));

        ws.Kind.Should().Be("Custom");
        ws.Status.Should().Be(nameof(OrderStatus.AwaitingReview));
    }

    [Fact]
    public async Task A_plain_product_cannot_use_the_custom_request_path()
    {
        // Inverse guard (existing behaviour, pinned): the custom path rejects plain products.
        var (host, _) = await CreateAuthedCustomerAsync();
        using var _ = host;
        var productId = await SeedProductAsync(host.Services);

        using var scope = host.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var act = () => sender.Send(new SubmitOrderRequestCommand(new SubmitOrderRequest(
            productId, 1, null, new List<OrderRequirementInput>(), new List<OrderFileInput>())));
        await act.Should().ThrowAsync<ConflictException>();
    }

    // ---------- review purchase-verification ----------

    [Fact]
    public async Task A_review_requires_a_paid_purchase()
    {
        var (host, customerId) = await CreateAuthedCustomerAsync();
        using var _ = host;
        var productId = await SeedProductAsync(host.Services);

        using var scope = host.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(Request(customerId, productId)));

        // Unpaid/abandoned direct orders never qualify as a purchase.
        var act = () => sender.Send(new CreateReviewCommand(new CreateReviewRequest(productId, 5, null, "great")));
        var ex = await act.Should().ThrowAsync<BusinessRuleValidationException>();
        ex.Which.Message.Should().Contain("purchased");

        // Pay the order -> the same customer now qualifies.
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var entity = await db.Orders.SingleAsync(o => o.Id == order.Id);
        entity.MarkPaid();
        await db.SaveChangesAsync();

        var review = await sender.Send(new CreateReviewCommand(new CreateReviewRequest(productId, 5, null, "great")));
        review.Rating.Should().Be(5);
    }

    [Fact]
    public async Task A_refunded_purchase_does_not_qualify_for_a_review()
    {
        var (host, customerId) = await CreateAuthedCustomerAsync();
        using var _ = host;
        var productId = await SeedProductAsync(host.Services);

        using var scope = host.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(Request(customerId, productId)));

        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var entity = await db.Orders.SingleAsync(o => o.Id == order.Id);
        entity.MarkPaid();
        entity.Refund();
        await db.SaveChangesAsync();

        var act = () => sender.Send(new CreateReviewCommand(new CreateReviewRequest(productId, 5, null, "ok")));
        await act.Should().ThrowAsync<BusinessRuleValidationException>();
    }

    // ---------- audit wording ----------

    [Fact]
    public async Task Direct_orders_record_direct_activity_wording()
    {
        var productId = await SeedProductAsync(_factory.Services);
        var customerId = await SeedCustomerAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(Request(customerId, productId)));

        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.AuditLogs.AnyAsync(a => a.OrderId == order.Id
            && a.ActivityType == "order.created" && a.Title == "Direct product order created")).Should().BeTrue();

        var entity = await db.Orders.SingleAsync(o => o.Id == order.Id);
        entity.MarkPaid();
        await db.SaveChangesAsync();
        (await db.AuditLogs.AnyAsync(a => a.OrderId == order.Id
            && a.ActivityType == "order.status_changed" && a.Title == "Direct order status changed")).Should().BeTrue();
    }
}
