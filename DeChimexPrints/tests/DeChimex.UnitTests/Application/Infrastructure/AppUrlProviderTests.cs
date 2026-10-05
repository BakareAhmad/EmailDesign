using DeChimex.Infrastructure.Options;
using DeChimex.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.UnitTests.Application.Infrastructure;

/// <summary>
/// Guards the application links emitted in transactional emails. These must point at the
/// CURRENT MVC routes, not the retired vanilla-JS SPA pages (/verify-email, /reset-password,
/// /track, /public/payment-callback.html).
/// </summary>
public class AppUrlProviderTests
{
    private static readonly AppOptions App = new() { FrontendBaseUrl = "https://dechimexprinthub.com" };
    private static readonly AppUrlProvider Provider = new(Options.Create(App));

    [Fact]
    public void VerificationLink_PointsToMvcVerifyEmail()
    {
        Provider.BuildEmailVerificationLink("ada@example.com", "tok3")
            .Should().Be("https://dechimexprinthub.com/Account/VerifyEmail?email=ada%40example.com&token=tok3");
    }

    [Fact]
    public void ResetLink_PointsToMvcResetPassword()
    {
        Provider.BuildPasswordResetLink("ada@example.com", "tok3")
            .Should().Be("https://dechimexprinthub.com/Account/ResetPassword?email=ada%40example.com&token=tok3");
    }

    [Fact]
    public void TrackingLink_PointsToPortalOrdersTrack()
    {
        Provider.BuildOrderTrackingLink("DCX-2026-0001")
            .Should().Be("https://dechimexprinthub.com/portal/orders/track?orderNumber=DCX-2026-0001");
    }

    [Fact]
    public void CallbackLink_PointsToCheckoutReturn()
    {
        Provider.BuildPaymentCallbackUrl("pay-123")
            .Should().Be("https://dechimexprinthub.com/checkout/return/pay-123");
    }
}
