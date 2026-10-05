using DeChimex.Application.Common.Interfaces;
using DeChimex.Shared.Constants;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Domain.Repositories;
using MediatR;

namespace DeChimex.Application.EventHandlers;

public sealed class OrderPlacedEventHandler(INotificationService notifications)
    : INotificationHandler<OrderPlacedEvent>
{
    public Task Handle(OrderPlacedEvent e, CancellationToken ct)
        => notifications.NotifyAsync(NotificationType.NewOrder, "New order placed",
            $"Order {e.OrderNumber} was placed (total {e.GrandTotal:N2}).",
            link: $"/admin/orders/{e.OrderId}", requiredPermission: Permissions.Orders.View, ct: ct);
}

public sealed class OrderPaidEventHandler(
    ICustomerRepository customers, IOrderRepository orders, IEmailQueue emailQueue,
    IEmailTemplateRenderer renderer, IAppUrlProvider urls)
    : INotificationHandler<OrderPaidEvent>
{
    public async Task Handle(OrderPaidEvent e, CancellationToken ct)
    {
        var customer = await customers.GetByIdAsync(e.CustomerId, ct);
        // Guests (walk-in customers without an email) have no inbox to notify.
        if (customer is null or { Email: null }) return;
        var order = await orders.GetByIdAsync(e.OrderId, ct);
        var content = renderer.Render(EmailTemplate.OrderConfirmation, new Dictionary<string, string>
        {
            ["FirstName"] = customer.FirstName,
            ["OrderNumber"] = e.OrderNumber,
            ["Amount"] = order is null ? "—" : DeChimex.Application.Common.MoneyText.Format(order.GrandTotal.Amount),
            ["TrackingLink"] = urls.BuildOrderTrackingLink(e.OrderNumber)
        });
        await emailQueue.EnqueueAsync(new EmailMessage(customer.Email.Value, customer.FullName,
            content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.OrderConfirmation), Category: "customer"), ct);
    }
}

public sealed class OrderStatusChangedEventHandler(
    ICustomerRepository customers, IOrderRepository orders, IEmailQueue emailQueue,
    IEmailTemplateRenderer renderer, IAppUrlProvider urls)
    : INotificationHandler<OrderStatusChangedEvent>
{
    public async Task Handle(OrderStatusChangedEvent e, CancellationToken ct)
    {
        // Only email the customer on meaningful production milestones. Cancelled/Refunded
        // are included so the customer is told when an order is called off or money is returned.
        if (e.NewStatus is not (OrderStatus.InProduction or OrderStatus.Ready or OrderStatus.Completed
            or OrderStatus.Delivered or OrderStatus.Cancelled or OrderStatus.Refunded))
            return;

        var order = await orders.GetByIdAsync(e.OrderId, ct);
        if (order is null) return;
        var customer = await customers.GetByIdAsync(order.CustomerId, ct);
        if (customer is null or { Email: null }) return;

        var content = renderer.Render(EmailTemplate.OrderStatusUpdate, new Dictionary<string, string>
        {
            ["FirstName"] = customer.FirstName,
            ["OrderNumber"] = e.OrderNumber,
            // Human-readable status ("In Production"), never the raw enum name.
            ["Status"] = System.Text.RegularExpressions.Regex.Replace(e.NewStatus.ToString(), "(?<=[a-z])([A-Z])", " $1"),
            ["TrackingLink"] = urls.BuildOrderTrackingLink(e.OrderNumber)
        });
        if (customer.Email is null) return; // guest customer: no inbox to notify
        await emailQueue.EnqueueAsync(new EmailMessage(customer.Email.Value, customer.FullName,
            content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.OrderStatusUpdate), Category: "customer"), ct);
    }
}

public sealed class OrderRequiresProcurementEventHandler(INotificationService notifications)
    : INotificationHandler<OrderRequiresProcurementEvent>
{
    public Task Handle(OrderRequiresProcurementEvent e, CancellationToken ct)
        => notifications.NotifyAsync(NotificationType.ProcurementRequired, "Procurement required",
            $"Order {e.OrderNumber} requires stock procurement.",
            link: $"/admin/procurement", requiredPermission: Permissions.Procurement.View, ct: ct);
}

/// <summary>
/// Notifies the customer when an online payment fails (Paystack verify returned failed/abandoned),
/// so they know the order is still awaiting payment and can retry. Fire-and-forget like the other
/// email handlers; a mail outage never blocks the payment flow.
/// </summary>
public sealed class PaymentFailedEventHandler(
    ICustomerRepository customers, IOrderRepository orders, IEmailQueue emailQueue,
    IEmailTemplateRenderer renderer, IAppUrlProvider urls)
    : INotificationHandler<PaymentFailedEvent>
{
    public async Task Handle(PaymentFailedEvent e, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(e.OrderId, ct);
        if (order is null) return;
        var customer = await customers.GetByIdAsync(order.CustomerId, ct);
        if (customer is null or { Email: null }) return; // guests have no inbox

        var content = renderer.Render(EmailTemplate.PaymentFailed, new Dictionary<string, string>
        {
            ["FirstName"] = customer.FirstName,
            // The subject reads "Payment failed for {{OrderNumber}}" - use the order number
            // (consistent with every other order email); the gateway reference stays in the
            // payment records. e.Reference is NOT customer-friendly in a subject line.
            ["OrderNumber"] = order.OrderNumber,
            ["Amount"] = DeChimex.Application.Common.MoneyText.Format(e.Amount),
            ["TrackingLink"] = urls.BuildOrderTrackingLink(order.OrderNumber)
        });
        await emailQueue.EnqueueAsync(new EmailMessage(customer.Email.Value, customer.FullName,
            content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.PaymentFailed), Category: "customer"), ct);
    }
}

// ----------------------------------------------------------------------------------
// Custom-order workflow event handlers (CUSTOM_ORDER_FLOW_PLAN.md §11).
// ----------------------------------------------------------------------------------

public sealed class OrderRequestSubmittedEventHandler(
    ICustomerRepository customers, INotificationService notifications,
    IEmailQueue emailQueue, IEmailTemplateRenderer renderer, IAppUrlProvider urls)
    : INotificationHandler<OrderRequestSubmittedEvent>
{
    public async Task Handle(OrderRequestSubmittedEvent e, CancellationToken ct)
    {
        await notifications.NotifyAsync(NotificationType.OrderRequestReceived, "New order request",
            $"Order request {e.OrderNumber} needs review.",
            link: $"/admin/orders/{e.OrderId}/workspace", requiredPermission: Permissions.Orders.View, ct: ct);

        var customer = await customers.GetByIdAsync(e.CustomerId, ct);
        if (customer is null or { Email: null }) return;
        var content = renderer.Render(EmailTemplate.OrderRequestReceived, new Dictionary<string, string>
        {
            ["FirstName"] = customer.FirstName,
            ["OrderNumber"] = e.OrderNumber,
            ["WorkspaceLink"] = urls.BuildOrderWorkspaceLink(e.OrderId)
        });
        await emailQueue.EnqueueAsync(new EmailMessage(customer.Email.Value, customer.FullName,
            content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.OrderRequestReceived), Category: "customer"), ct);
    }
}

public sealed class OrderMessageAddedEventHandler(
    ICustomerRepository customers, IOrderRepository orders, INotificationService notifications,
    IEmailQueue emailQueue, IEmailTemplateRenderer renderer, IAppUrlProvider urls)
    : INotificationHandler<OrderMessageAddedEvent>
{
    public async Task Handle(OrderMessageAddedEvent e, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(e.OrderId, ct);
        if (order is null) return;

        if (e.AuthorRole == "customer")
        {
            // Route to the responsible sales owner when one is assigned; otherwise fall
            // back to the staff broadcast so the message is never silently missed.
            if (order.AssignedSalesUserId is Guid salesUserId)
            {
                await notifications.NotifyAsync(NotificationType.OrderMessageReceived, "New message on your assigned order",
                    $"{e.AuthorName} sent a message on {e.OrderNumber}.",
                    recipientUserId: salesUserId, link: $"/admin/orders/{e.OrderId}/workspace", ct: ct);
            }
            else
            {
                await notifications.NotifyAsync(NotificationType.OrderMessageReceived, "New message on order request",
                    $"{e.AuthorName} sent a message on {e.OrderNumber}.",
                    link: $"/admin/orders/{e.OrderId}/workspace", requiredPermission: Permissions.Orders.View, ct: ct);
            }
            return;
        }

        var customer = await customers.GetByIdAsync(order.CustomerId, ct);
        if (customer is null) return;
        if (customer.UserId is not null)
            await notifications.NotifyAsync(NotificationType.OrderMessageReceived, "New message on your order",
                $"{e.AuthorName} replied on {e.OrderNumber}.",
                recipientUserId: customer.UserId, link: $"/portal/orders/{e.OrderId}/workspace", ct: ct);
        if (customer.Email is null) return;
        // Media-only messages carry no text; the email still needs a readable line.
        var preview = string.IsNullOrWhiteSpace(e.Body)
            ? "You have a new attachment in the conversation."
            : e.Body.Length > 180 ? e.Body[..180] + "…" : e.Body;
        var content = renderer.Render(EmailTemplate.OrderMessageNotification, new Dictionary<string, string>
        {
            ["FirstName"] = customer.FirstName,
            ["OrderNumber"] = e.OrderNumber,
            ["MessagePreview"] = preview,
            ["WorkspaceLink"] = urls.BuildOrderWorkspaceLink(e.OrderId)
        });
        await emailQueue.EnqueueAsync(new EmailMessage(customer.Email.Value, customer.FullName,
            content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.OrderMessageNotification), Category: "customer"), ct);
    }
}

public sealed class OrderFinalizedEventHandler(
    ICustomerRepository customers, INotificationService notifications,
    IEmailQueue emailQueue, IEmailTemplateRenderer renderer, IAppUrlProvider urls)
    : INotificationHandler<OrderFinalizedEvent>
{
    public async Task Handle(OrderFinalizedEvent e, CancellationToken ct)
    {
        var customer = await customers.GetByIdAsync(e.CustomerId, ct);
        if (customer is null) return;
        if (customer.UserId is not null)
            await notifications.NotifyAsync(NotificationType.OrderQuoteReady, "Your quote is ready",
                $"The quote for {e.OrderNumber} is ready for review and payment.",
                recipientUserId: customer.UserId, link: $"/portal/orders/{e.OrderId}/workspace", ct: ct);
        if (customer.Email is null) return;
        var content = renderer.Render(EmailTemplate.OrderQuoteReady, new Dictionary<string, string>
        {
            ["FirstName"] = customer.FirstName,
            ["OrderNumber"] = e.OrderNumber,
            ["WorkspaceLink"] = urls.BuildOrderWorkspaceLink(e.OrderId)
        });
        await emailQueue.EnqueueAsync(new EmailMessage(customer.Email.Value, customer.FullName,
            content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.OrderQuoteReady), Category: "customer"), ct);
    }
}

public sealed class DesignProofUploadedEventHandler(
    ICustomerRepository customers, INotificationService notifications,
    IEmailQueue emailQueue, IEmailTemplateRenderer renderer, IAppUrlProvider urls)
    : INotificationHandler<DesignProofUploadedEvent>
{
    public async Task Handle(DesignProofUploadedEvent e, CancellationToken ct)
    {
        var customer = await customers.GetByIdAsync(e.CustomerId, ct);
        if (customer is null) return;
        if (customer.UserId is not null)
            await notifications.NotifyAsync(NotificationType.DesignProofReady, "Design proof ready",
                $"A design proof (v{e.Version}) for {e.OrderNumber} is ready for your approval.",
                recipientUserId: customer.UserId, link: $"/portal/orders/{e.OrderId}/workspace", ct: ct);
        if (customer.Email is null) return;
        var content = renderer.Render(EmailTemplate.DesignProofReady, new Dictionary<string, string>
        {
            ["FirstName"] = customer.FirstName,
            ["OrderNumber"] = e.OrderNumber,
            ["WorkspaceLink"] = urls.BuildOrderWorkspaceLink(e.OrderId)
        });
        await emailQueue.EnqueueAsync(new EmailMessage(customer.Email.Value, customer.FullName,
            content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.DesignProofReady), Category: "customer"), ct);
    }
}

public sealed class DesignProofApprovedEventHandler(INotificationService notifications)
    : INotificationHandler<DesignProofApprovedEvent>
{
    public Task Handle(DesignProofApprovedEvent e, CancellationToken ct)
        => notifications.NotifyAsync(NotificationType.DesignProofApproved, "Design approved",
            $"Design approved for order {e.OrderNumber}.",
            link: $"/admin/orders/{e.OrderId}/workspace", requiredPermission: Permissions.Orders.View, ct: ct);
}

public sealed class DesignProofChangesRequestedEventHandler(INotificationService notifications)
    : INotificationHandler<DesignProofChangesRequestedEvent>
{
    public Task Handle(DesignProofChangesRequestedEvent e, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(e.Feedback)
            ? "no reason given"
            : e.Feedback.Length > 180 ? e.Feedback[..180] + "…" : e.Feedback;
        return notifications.NotifyAsync(NotificationType.DesignProofChangesRequested, "Proof changes requested",
            $"Customer requested changes on proof v{e.Version} for order {e.OrderNumber}: {reason}",
            link: $"/admin/orders/{e.OrderId}/workspace", requiredPermission: Permissions.Orders.View, ct: ct);
    }
}

// ----------------------------------------------------------------------------------
// Design-review workflow event handlers (independent-history submissions with
// their own approve/reject decision — see OrderDesignReview). Bell notifications
// only: the live workspace toast + timeline already cover in-app visibility, and
// no email template exists for these (emails stay on the proof/order surface).
// ----------------------------------------------------------------------------------

public sealed class DesignReviewUploadedEventHandler(
    ICustomerRepository customers, INotificationService notifications)
    : INotificationHandler<DesignReviewUploadedEvent>
{
    public async Task Handle(DesignReviewUploadedEvent e, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(e.OriginalFileName) ? "a design file" : e.OriginalFileName;
        // Customer upload: staff with order visibility must review the file.
        if (e.UploadedByRole == "customer")
        {
            await notifications.NotifyAsync(NotificationType.DesignReviewUploaded, "Design review uploaded",
                $"Customer uploaded '{name}' for order {e.OrderNumber} — review it in the workspace.",
                link: $"/admin/orders/{e.OrderId}/workspace", requiredPermission: Permissions.Orders.View, ct: ct);
            return;
        }
        // Staff upload: the customer should take a look (direct, like proof-ready).
        var customer = await customers.GetByIdAsync(e.CustomerId, ct);
        if (customer?.UserId is not null)
            await notifications.NotifyAsync(NotificationType.DesignReviewUploaded, "Design file shared",
                $"A design file '{name}' was shared for order {e.OrderNumber} — open the workspace to view it.",
                recipientUserId: customer.UserId, link: $"/portal/orders/{e.OrderId}/workspace", ct: ct);
    }
}

public sealed class DesignReviewApprovedEventHandler(
    ICustomerRepository customers, INotificationService notifications)
    : INotificationHandler<DesignReviewApprovedEvent>
{
    public async Task Handle(DesignReviewApprovedEvent e, CancellationToken ct)
    {
        var customer = await customers.GetByIdAsync(e.CustomerId, ct);
        if (customer?.UserId is null) return;
        await notifications.NotifyAsync(NotificationType.DesignReviewApproved, "Design review approved",
            $"Your design review for order {e.OrderNumber} was approved.",
            recipientUserId: customer.UserId, link: $"/portal/orders/{e.OrderId}/workspace", ct: ct);
    }
}

public sealed class DesignReviewRejectedEventHandler(
    ICustomerRepository customers, INotificationService notifications)
    : INotificationHandler<DesignReviewRejectedEvent>
{
    public async Task Handle(DesignReviewRejectedEvent e, CancellationToken ct)
    {
        var customer = await customers.GetByIdAsync(e.CustomerId, ct);
        if (customer?.UserId is null) return;
        var reason = string.IsNullOrWhiteSpace(e.Reason)
            ? "no reason given"
            : e.Reason.Length > 180 ? e.Reason[..180] + "…" : e.Reason;
        await notifications.NotifyAsync(NotificationType.DesignReviewRejected, "Design review rejected",
            $"Your design review for order {e.OrderNumber} was rejected: {reason} Please upload a revised file.",
            recipientUserId: customer.UserId, link: $"/portal/orders/{e.OrderId}/workspace", ct: ct);
    }
}

// ----------------------------------------------------------------------------------
// Sales-assignment workflow event handlers.
// ----------------------------------------------------------------------------------

public sealed class SalesAssignedEventHandler(INotificationService notifications)
    : INotificationHandler<SalesAssignedEvent>
{
    public Task Handle(SalesAssignedEvent e, CancellationToken ct)
        => notifications.NotifyAsync(
            e.IsReassignment ? NotificationType.SalesReassigned : NotificationType.SalesAssigned,
            e.IsReassignment ? "Order reassigned to you" : "New order assigned to you",
            e.IsReassignment
                ? $"Order {e.OrderNumber} was reassigned to you."
                : $"Order {e.OrderNumber} needs your review.",
            recipientUserId: e.NewSalesUserId,
            link: $"/admin/orders/{e.OrderId}/workspace", ct: ct);
}
