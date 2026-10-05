using System.Net;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Services;

/// <summary>
/// Builds application links used in transactional emails and the Paystack callback.
/// These point at the CURRENT (MVC) application routes, not the retired vanilla-JS
/// SPA pages that used to live at /verify-email, /reset-password, /track and
/// /public/payment-callback.html.
/// </summary>
public sealed class AppUrlProvider(IOptions<AppOptions> app) : IAppUrlProvider
{
    private readonly AppOptions _app = app.Value;

    public string BuildEmailVerificationLink(string email, string token)
        => $"{_app.FrontendBaseUrl}/Account/VerifyEmail?email={WebUtility.UrlEncode(email)}&token={WebUtility.UrlEncode(token)}";

    public string BuildPasswordResetLink(string email, string token)
        => $"{_app.FrontendBaseUrl}/Account/ResetPassword?email={WebUtility.UrlEncode(email)}&token={WebUtility.UrlEncode(token)}";

    public string BuildOrderTrackingLink(string orderNumber)
        => $"{_app.FrontendBaseUrl}/portal/orders/track?orderNumber={WebUtility.UrlEncode(orderNumber)}";

    public string BuildPaymentCallbackUrl(string reference)
        => $"{_app.FrontendBaseUrl}/checkout/return/{WebUtility.UrlEncode(reference)}";

    public string BuildMarketingUnsubscribeLink(Guid unsubscribeToken)
        => $"{_app.FrontendBaseUrl}/unsubscribe/{unsubscribeToken}";

    // Owner directive 2026-09-08: shareable invoice/receipt links must carry the
    // environment's base URL (same origin rule as email links, §53) so a copied
    // link actually resolves, instead of a bare relative path like /r/{token}.
    public string BuildPublicInvoiceLink(string shareToken)
        => $"{_app.FrontendBaseUrl}/i/{WebUtility.UrlEncode(shareToken)}";

    public string BuildPublicReceiptLink(string shareToken)
        => $"{_app.FrontendBaseUrl}/r/{WebUtility.UrlEncode(shareToken)}";

    public string BuildOrderWorkspaceLink(Guid orderId)
        => $"{_app.FrontendBaseUrl}/portal/orders/{orderId}/workspace";
}
