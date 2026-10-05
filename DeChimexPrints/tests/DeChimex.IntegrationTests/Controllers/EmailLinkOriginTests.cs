using DeChimex.Application.Common.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Email link origin regression (owner report 2026-09-08): the unsubscribe link in a
/// marketing email pointed at the production domain (https://dechimexprinthub.com)
/// even while the app ran in Development, so clicking it from a local test hit a
/// dead site. Root cause: App:FrontendBaseUrl had a single production value with no
/// Development override. Development now overrides it to the local https profile
/// (launchSettings: https://localhost:7061) so every email link generated while
/// running locally (verification, reset, tracking, unsubscribe) is actually
/// clickable on the developer's machine. Production keeps the real domain.
/// </summary>
public class EmailLinkOriginTests
{
    private sealed class DevLinkFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }
    }

    private sealed class ProdLinkFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            base.ConfigureWebHost(builder);
        }
    }

    private static IAppUrlProvider ResolveUrls(CustomWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IAppUrlProvider>();
    }

    [Fact]
    public void Development_emails_link_to_local_origin()
    {
        using var factory = new DevLinkFactory();
        var urls = ResolveUrls(factory);
        var token = Guid.NewGuid();

        urls.BuildMarketingUnsubscribeLink(token).Should()
            .Be($"https://localhost:7061/unsubscribe/{token}",
                "locally-sent emails must carry links that resolve on the developer's machine (https launch profile)");

        urls.BuildEmailVerificationLink("a@b.c", "123456").Should()
            .StartWith("https://localhost:7061/Account/VerifyEmail");
        urls.BuildPasswordResetLink("a@b.c", "tok").Should()
            .StartWith("https://localhost:7061/Account/ResetPassword");
        urls.BuildOrderTrackingLink("DCX-1").Should()
            .StartWith("https://localhost:7061/portal/orders/track");
    }

    [Fact]
    public void Production_emails_link_to_public_domain()
    {
        using var factory = new ProdLinkFactory();
        var urls = ResolveUrls(factory);
        var token = Guid.NewGuid();

        urls.BuildMarketingUnsubscribeLink(token).Should()
            .Be($"https://dechimexprinthub.com/unsubscribe/{token}",
                "production emails must point at the public site");
        urls.BuildEmailVerificationLink("a@b.c", "123456").Should()
            .StartWith("https://dechimexprinthub.com/Account/VerifyEmail");
    }
}
