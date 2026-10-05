using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Infrastructure.Authentication;
using DeChimex.Infrastructure.Caching;
using DeChimex.Infrastructure.Common;
using DeChimex.Infrastructure.Diagnostics;
using DeChimex.Infrastructure.Email;
using DeChimex.Infrastructure.Options;
using DeChimex.Infrastructure.Payments;
using DeChimex.Infrastructure.Persistence;
using DeChimex.Infrastructure.Services;
using DeChimex.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeChimex.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Strongly-typed options (validated on start).
        // NOTE: JwtOptions / ITokenService were removed as part of the JWT-independence
        // change (MVC auth is cookie-only); no Jwt:SigningKey is required at startup.
        services.AddOptions<BrevoOptions>().Bind(configuration.GetSection(BrevoOptions.SectionName)).ValidateOnStart();
        services.AddOptions<PaystackOptions>().Bind(configuration.GetSection(PaystackOptions.SectionName)).ValidateOnStart();
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateOnStart();
        services.AddOptions<AppOptions>().Bind(configuration.GetSection(AppOptions.SectionName)).ValidateOnStart();
        services.AddOptions<CorsOptions>().Bind(configuration.GetSection(CorsOptions.SectionName)).ValidateOnStart();
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName)).ValidateOnStart();
        services.AddOptions<DevPortalOptions>().Bind(configuration.GetSection(DevPortalOptions.SectionName));

        // Developer Portal diagnostics core (queue -> background batch writer; see DEV_PORTAL_REPORT.md).
        services.AddSingleton<DiagnosticsQueue>();
        services.AddSingleton<ITechnicalEventRecorder, TechnicalEventRecorder>();
        services.AddSingleton<IIntegrationCallRecorder, IntegrationCallRecorder>();
        services.AddSingleton<IJobMonitor, JobMonitor>();
        services.AddSingleton<DiagnosticsWriterService>();
        services.AddHostedService(sp => sp.GetRequiredService<DiagnosticsWriterService>());
        services.AddScoped<DiagnosticsRetention>();
        services.AddHostedService<DiagnosticsRetentionService>();

        // Core infrastructure services
        services.AddHttpContextAccessor();
        services.AddMemoryCache();

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddOptions<PasswordHasherOptions>()
            .Bind(configuration.GetSection(PasswordHasherOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ISecureTokenGenerator, SecureTokenGenerator>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IAppUrlProvider, AppUrlProvider>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IWebPushDispatcher, Services.PwaWebPushDispatcher>(); // PWA Web Push (scoped: uses repos + DbContext)
        services.AddSingleton<ICacheService, MemoryCacheService>();

        // Email
        services.AddScoped<IEmailTemplateRenderer, EmailTemplateRenderer>();
        // AUD-003: durable outbox replaces the old fire-and-forget BackgroundEmailQueue —
        // queued emails are persisted rows drained by OutboxEmailProcessor (registered by
        // the Host alongside the other hosted workers) with retries and a portal job view.
        services.Configure<EmailOutboxOptions>(configuration.GetSection(EmailOutboxOptions.SectionName));
        services.AddScoped<IEmailQueue, DurableEmailQueue>();
        services.AddHostedService<OutboxEmailProcessor>();
        services.AddHttpClient<IEmailService, BrevoEmailService>();
        services.AddHttpClient<IEmailHealthCheck, BrevoEmailHealthCheck>();

        // Payments
        // PaystackGateway is the typed HTTP client; IPaymentGateway resolves to the diagnostics
        // decorator wrapping it (behaviour unchanged, calls recorded for the Developer Portal).
        services.AddHttpClient<PaystackGateway>();
        services.AddScoped<IPaymentGateway>(sp => new InstrumentedPaymentGateway(
            sp.GetRequiredService<PaystackGateway>(), sp.GetRequiredService<IIntegrationCallRecorder>()));
        services.AddScoped<IWebhookPayloadParser, PaystackWebhookParser>();
        // AUD-033: gateway payloads are PII-redacted before entering the ledger.
        services.AddSingleton<IGatewayPayloadRedactor, PaystackPayloadRedactor>();
        services.AddSingleton<IPaymentChargeCalculator, PaystackChargeCalculator>();
        services.AddScoped<IPaymentGatewayRefundCoordinator, PaymentGatewayRefundCoordinator>();

        // Storage
        services.AddScoped<IFileStorageService, LocalFileStorageService>();
        services.AddScoped<IVideoEncodeService, FfmpegVideoEncodeService>();

        // Order fulfilment domain service + seeder
        services.AddScoped<Application.Features.Orders.IOrderFulfilmentService, Application.Features.Orders.OrderFulfilmentService>();
        services.AddScoped<Application.Features.Orders.ISalesAssignmentService, Application.Features.Orders.SalesAssignmentService>();
        services.AddScoped<Application.Features.Payments.IPaymentConfirmationService, Application.Features.Payments.PaymentConfirmationService>();
        services.AddScoped<DatabaseSeeder>();

        return services;
    }
}
