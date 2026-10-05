namespace DeChimex.Application.Common.Interfaces;

/// <summary>Builds fully-qualified frontend links used in transactional emails.</summary>
public interface IAppUrlProvider
{
    string BuildEmailVerificationLink(string email, string token);
    string BuildPasswordResetLink(string email, string token);
    string BuildOrderTrackingLink(string orderNumber);
    string BuildPaymentCallbackUrl(string reference);

    /// <summary>Per-recipient marketing unsubscribe link (token identifies the customer).</summary>
    string BuildMarketingUnsubscribeLink(Guid unsubscribeToken);

    /// <summary>Fully-qualified public invoice share link (/i/{shareToken}).</summary>
    string BuildPublicInvoiceLink(string shareToken);

    /// <summary>Fully-qualified public receipt share link (/r/{shareToken}).</summary>
    string BuildPublicReceiptLink(string shareToken);

    /// <summary>Fully-qualified customer order-workspace link (/portal/orders/{id}/workspace).</summary>
    string BuildOrderWorkspaceLink(Guid orderId);
}
