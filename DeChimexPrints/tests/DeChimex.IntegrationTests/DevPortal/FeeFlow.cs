using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>Drives the REAL order → Paystack initialize → verify → refund commands (fake gateway).</summary>
public static class FeeFlow
{
    public static async Task SetPercentAsync(DevPortalFactory f, decimal percent)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var setting = await db.ApplicationSettings.FirstOrDefaultAsync(s => s.Key == "service_fee.percent");
        var v = DeChimex.Application.Common.ServiceFeeSettings.Format(percent);
        if (setting is null) db.ApplicationSettings.Add(ApplicationSetting.Create("service_fee.percent", v)); else setting.SetValue(v);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ICacheService>().RemoveAsync(DeChimex.Application.Common.CacheKeys.SettingsAll);
    }

    public static async Task<(Guid OrderId, decimal Total)> PlaceOrderAsync(DevPortalFactory f, decimal price, bool custom)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create($"DP Test {Guid.NewGuid():N}"[..30]);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var product = Product.Create($"DP Test {price:0} {Guid.NewGuid():N}"[..40], $"DP-{price:0}-{Guid.NewGuid():N}"[..20], null, category.Id, price);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.Inventories.Add(Inventory.Create(product.Id, product.Name, 50));
        var customer = Customer.Create("Fee", "Tester", $"fee.{Guid.NewGuid():N}@yopmail.com");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var order = await sender.Send(new CreateOrderCommand(new CreateOrderRequest(customer.Id,
            new List<CreateOrderItemRequest> { new(product.Id, 1) }, new List<CreateOrderServiceRequest>(), null, null, null, 0m, 0m, 0m),
            Kind: custom ? OrderKind.Custom : OrderKind.Direct));
        return (order.Id, order.GrandTotal);
    }

    public static async Task<(Guid PaymentId, string Reference)> InitializeAsync(DevPortalFactory f, Guid orderId)
    {
        using var scope = f.Services.CreateScope();
        var init = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new InitializePaymentCommand(
            new InitializePaymentRequest(orderId, "paystack"), "https://localhost:7061/checkout/return"));
        return (init.PaymentId, init.Reference);
    }

    public static async Task VerifyAsync(DevPortalFactory f, string reference)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new VerifyPaymentCommand(reference));
    }

    public static async Task RefundAsync(DevPortalFactory f, Guid orderId)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RefundOrderCommand(orderId));
    }
}
