using DeChimex.Application.Common;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Cart;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Orders.Queries;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Contracts.Cart;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Payments;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Models.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Store;

/// <summary>
/// Storefront checkout: server-side cart to order (ISS-01). The order is placed in
/// PendingPayment and the customer pays online via Paystack; a failed or abandoned
/// payment leaves the order unpaid and can be retried. There is no pay-on-delivery.
/// </summary>
[Authorize]
[Route("checkout")]
public sealed class CheckoutController(ICustomerRepository customers, IPaymentChargeCalculator chargeCalculator) : StoreControllerBase
{
    private const string DefaultDeliveryFee = "1500";

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Checkout";
        ViewData["ActiveNav"] = "cart";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();

        var (customer, error) = await ResolveCustomerAsync();
        if (error is not null) return error;

        var cart = await Sender.Send(new GetCartQuery(customer.Id));
        if (cart is null || cart.Items.Count == 0)
        {
            TempData["Error"] = "Your cart is empty. Add something first.";
            return RedirectToAction(nameof(StoreCartController.Index), "StoreCart");
        }

        var business = ViewBag.Business as Dictionary<string, string> ?? new();
        var deliveryFee = ParseDecimal(business.GetValueOrDefault("delivery.default_fee"), decimal.Parse(DefaultDeliveryFee));

        var model = new CheckoutFormViewModel
        {
            Street = customer.Addresses.FirstOrDefault(a => a.IsDefault)?.Address.Street,
            City = customer.Addresses.FirstOrDefault(a => a.IsDefault)?.Address.City,
            State = customer.Addresses.FirstOrDefault(a => a.IsDefault)?.Address.State,
            Country = customer.Addresses.FirstOrDefault(a => a.IsDefault)?.Address.Country,
            PostalCode = customer.Addresses.FirstOrDefault(a => a.IsDefault)?.Address.PostalCode
        };

        // Customer-borne Paystack charge (owner directive 2026-09-08): shown before payment.
        var total = cart.Subtotal + deliveryFee;
        var paystackCharge = chargeCalculator.Compute(total, ServiceFeeSettings.Parse(business.GetValueOrDefault(ServiceFeeSettings.Key))).ServiceFeeAmount;
        ViewBag.Cart = cart;
        ViewBag.DeliveryFee = deliveryFee;
        ViewBag.GrandTotal = total;
        ViewBag.PaystackCharge = paystackCharge;
        ViewBag.CustomerPayable = total + paystackCharge;
        ViewBag.PaystackChargePickup = chargeCalculator.Compute(cart.Subtotal, ServiceFeeSettings.Parse(business.GetValueOrDefault(ServiceFeeSettings.Key))).ServiceFeeAmount;
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PlaceOrder(CheckoutFormViewModel model)
    {
        ViewData["Title"] = "Checkout";
        ViewData["ActiveNav"] = "cart";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();

        var (customer, error) = await ResolveCustomerAsync();
        if (error is not null) return error;

        var cart = await Sender.Send(new GetCartQuery(customer.Id));
        if (cart is null || cart.Items.Count == 0)
        {
            TempData["Error"] = "Your cart is empty. Add something first.";
            return RedirectToAction(nameof(StoreCartController.Index), "StoreCart");
        }

        var business = ViewBag.Business as Dictionary<string, string> ?? new();
        var deliveryFee = ParseDecimal(business.GetValueOrDefault("delivery.default_fee"), decimal.Parse(DefaultDeliveryFee));
        var isDelivery = string.Equals(model.Fulfilment, "delivery", StringComparison.OrdinalIgnoreCase);

        if (isDelivery)
        {
            if (string.IsNullOrWhiteSpace(model.Street) || string.IsNullOrWhiteSpace(model.City)
                || string.IsNullOrWhiteSpace(model.State) || string.IsNullOrWhiteSpace(model.Country))
            {
                ModelState.AddModelError(string.Empty, "Please complete the delivery address.");
                ViewBag.Cart = cart;
                ViewBag.DeliveryFee = deliveryFee;
                ViewBag.GrandTotal = cart.Subtotal + deliveryFee;
                ViewBag.PaystackCharge = chargeCalculator.Compute(cart.Subtotal + deliveryFee, ServiceFeeSettings.Parse(business.GetValueOrDefault(ServiceFeeSettings.Key))).ServiceFeeAmount;
                ViewBag.CustomerPayable = cart.Subtotal + deliveryFee + ViewBag.PaystackCharge;
                ViewBag.PaystackChargePickup = chargeCalculator.Compute(cart.Subtotal, ServiceFeeSettings.Parse(business.GetValueOrDefault(ServiceFeeSettings.Key))).ServiceFeeAmount;
                return View("Index", model);
            }
        }

        var items = cart.Items
            .Where(i => string.Equals(i.LineType, "product", StringComparison.OrdinalIgnoreCase))
            .Select(i => new CreateOrderItemRequest(i.ReferenceId, i.Quantity))
            .ToList();
        var services = cart.Items
            .Where(i => string.Equals(i.LineType, "service", StringComparison.OrdinalIgnoreCase))
            .Select(i => new CreateOrderServiceRequest(i.ReferenceId, i.Quantity, i.Notes))
            .ToList();

        var request = new CreateOrderRequest(
            customer.Id,
            items,
            services,
            ShippingAddress: isDelivery
                ? new AddressRequest(model.Street!, model.City!, model.State!, model.Country!, model.PostalCode)
                : null,
            BillingAddress: null,
            CustomerNotes: string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes,
            Discount: 0m,
            DeliveryFee: isDelivery ? deliveryFee : 0m,
            Tax: 0m);

        OrderDto? order = null;
        try
        {
            order = await Sender.Send(new CreateOrderCommand(request));
            await Sender.Send(new ClearCartCommand(customer.Id));
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (ConflictException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = $"Order {order.OrderNumber} placed.";
        return RedirectToAction(nameof(Complete), new { orderId = order.Id });
    }

    [HttpGet("complete/{orderId:guid}")]
    public async Task<IActionResult> Complete(Guid orderId)
    {
        ViewData["Title"] = "Payment";
        ViewData["ActiveNav"] = "cart";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();

        var (customer, error) = await ResolveCustomerAsync();
        if (error is not null) return error;

        var order = await Sender.Send(new GetOrderByIdQuery(orderId));
        if (order is null || order.CustomerId != customer.Id)
            return NotFound();

        ViewBag.Order = order;
        var businessInfo = ViewBag.Business as Dictionary<string, string> ?? new();
        var paystackCharge = chargeCalculator.Compute(order.GrandTotal, ServiceFeeSettings.Parse(businessInfo.GetValueOrDefault(ServiceFeeSettings.Key))).ServiceFeeAmount;
        ViewBag.PaystackCharge = paystackCharge;
        ViewBag.CustomerPayable = order.GrandTotal + paystackCharge;
        return View();
    }

    [HttpPost("{orderId:guid}/pay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(Guid orderId)
    {
        var (customer, error) = await ResolveCustomerAsync();
        if (error is not null) return error;

        var order = await Sender.Send(new GetOrderByIdQuery(orderId));
        if (order is null || order.CustomerId != customer.Id)
            return NotFound();

        // Paystack redirects the customer to callback_url with ?trxref=&reference= appended,
        // so the callback must be the bare return route (a placeholder path segment would be
        // bound instead of the real reference). Public hosts always use https; loopback dev
        // keeps its own scheme (http profiles included).
        var scheme = Request.Host.Host is "localhost" or "127.0.0.1" or "::1" ? Request.Scheme : "https";
        var callbackUrl = Url.Action(nameof(Return), "Checkout", null, protocol: scheme)!;
        try
        {
            var init = await Sender.Send(new InitializePaymentCommand(
                new InitializePaymentRequest(order.Id, "paystack"), callbackUrl));

            if (string.IsNullOrWhiteSpace(init.AuthorizationUrl))
            {
                // Gateway accepted but gave no redirect: show the reference for manual confirmation.
                TempData["Error"] = "Payment was initialized but the payment page could not be reached. Please try again.";
                return RedirectToAction(nameof(Complete), new { orderId });
            }

            return Redirect(init.AuthorizationUrl);
        }
        catch (ConflictException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Complete), new { orderId });
        }
        catch (ValidationAppException ex)
        {
            TempData["Error"] = ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "Payment could not be started.";
            return RedirectToAction(nameof(Complete), new { orderId });
        }
    }

    /// <summary>
    /// Paystack redirect target. Verifies the payment reference with the gateway and
    /// confirms the order when successful (shared with the webhook pipeline).
    /// The reference arrives as a query parameter appended by Paystack; the path form is
    /// kept for backwards compatibility with earlier links.
    /// </summary>
    [HttpGet("return")]
    [HttpGet("return/{reference}")]
    public async Task<IActionResult> Return(string? reference = null, string? trxref = null)
    {
        ViewData["Title"] = "Payment result";
        ViewData["ActiveNav"] = "cart";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();

        var (customer, error) = await ResolveCustomerAsync();
        if (error is not null) return error;

        PaymentDto? payment = null;
        try
        {
            var resolvedReference = reference ?? trxref;
            if (string.IsNullOrWhiteSpace(resolvedReference))
            {
                ViewBag.VerificationError = "No payment reference was provided.";
                return View();
            }
            // Ownership-first: references belonging to other customers behave exactly like
            // unknown references (no gateway call, no state change on their payments).
            payment = await Sender.Send(new VerifyPaymentCommand(resolvedReference, customer.Id));
        }
        catch (NotFoundException)
        {
            // Unknown or foreign reference: say so plainly instead of revealing anything.
            ViewBag.VerificationError = "We could not find a payment with that reference. If you just paid, please wait a moment and refresh, or contact support.";
            return View();
        }

        var order = await Sender.Send(new GetOrderByIdQuery(payment.OrderId));
        if (order is null || order.CustomerId != customer.Id)
        {
            // Not this customer's order: do not leak payment details.
            ViewBag.Payment = null;
            return View();
        }

        ViewBag.Payment = payment;
        ViewBag.Order = order;
        return View();
    }

    private async Task<(Customer? Customer, IActionResult? Error)> ResolveCustomerAsync()
    {
        if (CurrentUser.UserId is not Guid uid)
        {
            return (null, RedirectToAction(nameof(AccountController.Login), "Account", new { returnUrl = "/checkout" }));
        }

        return await ResolveCustomerByIdAsync(uid);
    }

    private async Task<(Customer? Customer, IActionResult? Error)> ResolveCustomerByIdAsync(Guid userId)
    {
        var customer = await customers.GetByUserIdAsync(userId, HttpContext.RequestAborted);
        if (customer is null)
        {
            var view = View("NoProfile", "No customer profile is linked to this account. Contact support to link your profile.");
            return (null, view);
        }
        return (customer, null);
    }

    private static decimal ParseDecimal(string? value, decimal fallback)
        => decimal.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
}
