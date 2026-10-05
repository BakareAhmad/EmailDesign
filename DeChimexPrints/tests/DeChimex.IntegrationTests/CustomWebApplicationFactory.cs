using System.Threading.RateLimiting;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Persistence.Context;
using DeChimex.Persistence.Interceptors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DeChimex.IntegrationTests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Functional tests must not be throttled by the global rate limiter, so by default the
    /// test host replaces it with a permit-all limiter. Rate-limiting tests override this to
    /// false (and configure a small RateLimiting:PermitLimit) to exercise the real pipeline.
    /// </summary>
    protected virtual bool DisableRateLimiting => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs fails fast if ConnectionStrings:DefaultConnection is empty, and it runs
        // before this factory's InMemory swap (which happens during builder.Build()). Supply a
        // dummy value so the validation passes; it is never used because the SqlServer
        // registration is removed and replaced with InMemory below.
        builder.UseSetting("ConnectionStrings:DefaultConnection",
            "Server=localhost;Database=DeChimexTest;User Id=sa;Password=TestOnly1!;TrustServerCertificate=True");
        builder.ConfigureServices(services =>
        {
            // HIGH-002: Remove ALL EF Core registrations for the context so the InMemory
            // provider is the only one registered. Removing just DbContextOptions<T> is not
            // enough. The app's SqlServer IDbContextOptionsConfiguration<T> would still run,
            // registering a second provider and throwing:
            //   "Only a single database provider can be registered in a service provider."
            var efDescriptors = services
                .Where(d =>
                    d.ServiceType == typeof(DbContextOptions<DeChimexDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(DeChimexDbContext) ||
                    d.ServiceType == typeof(IDbContextOptionsConfiguration<DeChimexDbContext>))
                .ToList();

            foreach (var descriptor in efDescriptors)
            {
                services.Remove(descriptor);
            }

            // One shared in-memory database for the lifetime of this factory instance.
            // NOTE: the name must be captured once - computing Guid.NewGuid() inside the
            // options lambda would create a brand-new empty database per context resolution.
            var databaseName = "DeChimexTestDb_" + Guid.NewGuid();

            // Re-register the context against the in-memory database, mirroring the production
            // registration (interceptor + warning suppression) so tests exercise the same
            // soft-delete/audit behaviour as the real app. The application's own DatabaseSeeder
            // provisions roles, permissions, the Owner account, settings and starter catalog
            // data on startup (it falls back to EnsureCreated for non-relational providers).
            services.AddDbContext<DeChimexDbContext>((sp, options) =>
            {
                options.UseInMemoryDatabase(databaseName);
                options.AddInterceptors(sp.GetRequiredService<AuditableSoftDeleteInterceptor>());
                options.ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId
                        .PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
            });

            // Functional tests must not be throttled by the auth rate limiter added in HIGH-007.
            // Override the global limiter and the named "auth" policy with no-limiters so the
            // many auth calls made from the same loopback IP aren't rejected with 429. This only
            // affects the test host; production rate limiting is unchanged.
            // Remove the app's rate limiter configuration (HIGH-007 adds a 10 req/min "auth"
            // policy that would reject the many auth calls these functional tests make from a
            // single loopback IP with 429). We replace it with permit-all limiters below.
            if (DisableRateLimiting)
            {
                var rateLimiterConfig = services
                    .Where(d => d.ServiceType == typeof(IConfigureOptions<RateLimiterOptions>))
                    .ToList();
                foreach (var descriptor in rateLimiterConfig)
                {
                    services.Remove(descriptor);
                }

                // Permit-all limiters for the test host only: a no-op global limiter plus the "auth"
                // named policy referenced by [EnableRateLimiting("auth")] on the controllers.
                services.PostConfigure<RateLimiterOptions>(options =>
                {
                    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                        RateLimitPartition.GetNoLimiter("integration-tests"));
                    options.AddPolicy("auth", _ => RateLimitPartition.GetNoLimiter("integration-tests"));
                });
            }

            // LOW-008: replace the real Paystack HTTP gateway with a deterministic fake so payment
            // flows can be tested end-to-end without any network calls.
            var gatewayDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IPaymentGateway));
            if (gatewayDescriptor is not null) services.Remove(gatewayDescriptor);
            services.AddSingleton<IPaymentGateway, Fakes.FakePaymentGateway>();

            // Replace the real Brevo HTTP email service with a recording fake: integration
            // tests must never hit the live provider (which rejects sandbox IPs with 401),
            // and message-reply tests assert on what would have been sent. One shared
            // instance so tests can inspect Sent / toggle DeliverySucceeds.
            var emailDescriptors = services.Where(d => d.ServiceType == typeof(IEmailService)).ToList();
            foreach (var descriptor in emailDescriptors) services.Remove(descriptor);
            services.AddSingleton<IEmailService>(new Fakes.FakeEmailService());

            // The real email health check calls the live Brevo API - swap it for a fake so
            // the settings page is deterministic offline.
            var healthDescriptors = services.Where(d => d.ServiceType == typeof(IEmailHealthCheck)).ToList();
            foreach (var descriptor in healthDescriptors) services.Remove(descriptor);
            services.AddSingleton<IEmailHealthCheck>(new Fakes.FakeEmailHealthCheck());
        });
    }
}
