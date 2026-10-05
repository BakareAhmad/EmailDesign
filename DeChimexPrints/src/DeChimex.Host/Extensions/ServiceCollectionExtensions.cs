using System.Threading.RateLimiting;
using Asp.Versioning;
using DeChimex.Contracts.Common;
using DeChimex.Host.Authorization;
using DeChimex.Host.Middleware;
using DeChimex.Host.Services;
using DeChimex.Shared.Constants;
using DeChimex.Infrastructure.Options;
using DeChimex.Persistence.Context;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;

namespace DeChimex.Host.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiVersioningConfigured(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        }).AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });
        return services;
    }

    public static IServiceCollection AddAppAuthentication(this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false)
    {
        // AUD-023: Development keeps SameAsRequest (local HTTP E2E); every other
        // environment forces Secure — the site is HTTPS-only by design.
        var cookieSecure = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        // Cookie-only authentication. The JWT bearer (and the "smart" policy scheme forwarding to
        // it) was the auth surface for the retired vanilla-JS SPA; with that removed the MVC app
        // authenticates over the cookie scheme and every remaining /api/v1 endpoint is [AllowAnonymous].
        // The permission policy provider + handler are transport-agnostic, so the same
        // [Authorize(Policy = Permissions.X.Y)] and role checks work over the cookie scheme.
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // AUD-023: the site is HTTPS-only by design (App.FrontendBaseUrl is https) — the
            // session cookie must never ride an accidental HTTP hop. SameAsRequest would
            // issue a non-Secure cookie when a TLS-terminating proxy forwards plain HTTP;
            // with AUD-017's forwarded-headers handling the app sees the right scheme, and
            // outside Development we force Secure regardless.
            options.Cookie.SecurePolicy = cookieSecure;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);

            // For /api requests the cookie scheme must return 401/403, not redirect to a login
            // page (a browser would otherwise get a 302 for an unauthenticated API call).
            options.Events.OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                // Developer Portal security telemetry: an authenticated user was refused a resource.
                ctx.HttpContext.RequestServices.GetService<DeChimex.Application.Common.Interfaces.ITechnicalEventRecorder>()?.Record(
                    new DeChimex.Application.Common.Interfaces.TechnicalEventEntry(
                        DeChimex.Application.Common.Interfaces.TechnicalSeverity.Warning,
                        DeChimex.Application.Common.Interfaces.TechnicalCategory.Security,
                        DeChimex.Application.Common.Interfaces.TechnicalEventType.AuthorizationFailure,
                        $"Access denied to {ctx.Request.Method} {ctx.Request.Path}.",
                        Source: "CookieAuthentication",
                        MessageTemplate: "Access denied to " + ctx.Request.Path.Value));
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
        });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, NotRoleAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Permissions.StaffCounterPolicy, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(
                    new PermissionRequirement(Permissions.Orders.Create),
                    new NotRoleRequirement(RoleNames.Customer)));
        });
        return services;
    }

    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "DE-CHIMEX PRINT HUB API",
                Version = "v1",
                Description = "Business management & e-commerce API for DE-CHIMEX PRINT HUB. Creativity with Ease."
            });

            // Prevent 500s on swagger.json:
            // 1) If two actions map to the same route+verb, keep the first instead of throwing.
            options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
            // 2) Use fully-qualified schema ids so DTOs that share a class name across
            //    different feature namespaces don't collide (a common generation failure).
            options.CustomSchemaIds(type => type.FullName!.Replace("+", "."));
            // 3) Only include actions that belong to the "v1" group (versioned API explorer).
            options.DocInclusionPredicate((docName, apiDesc) =>
            {
                var group = apiDesc.GroupName;
                return string.IsNullOrEmpty(group) || group == docName;
            });
        });
        return services;
    }

    public static IServiceCollection AddCorsConfigured(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>()?.AllowedOrigins ?? Array.Empty<string>();
        services.AddCors(options => options.AddPolicy("DeChimexCors", policy =>
        {
            if (origins.Length > 0)
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
                      .WithExposedHeaders("X-Correlation-Id");
            else
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        }));
        return services;
    }

    public static IServiceCollection AddRateLimitingConfigured(this IServiceCollection services, IConfiguration configuration)
    {
        // Blank-page root cause (2026-09-09): the old global limit of 100 req/min per IP had
        // NO rejection handler, and ASP.NET Core's RateLimiterMiddleware default for a
        // rejection is a 429 with an EMPTY BODY. The 404-only status-code pages cannot catch
        // it (the limiter short-circuits upstream of them), so the browser rendered a blank
        // page at the correct URL until the 1-minute window reset - "intermittent blank page
        // that starts working later", and a restart did not help because the fresh window
        // filled up again within seconds of resumed browsing. Three fixes, all generic:
        //   1. OnRejected now always writes a real response (styled HTML page for browser
        //      requests, JSON envelope for /api) plus Retry-After - a 429 can never be blank.
        //   2. The limit is configurable (RateLimiting:PermitLimit, default 600/min). 100 was
        //      too low for real browsing: every missing uploaded image falls through the
        //      static-file middleware and burns a permit via the 404 re-execute, so a single
        //      heavy page (e.g. Products with a dozen missing thumbnails) could cost 13+.
        //   3. /hubs/* (authenticated SignalR real-time channel) is exempt: negotiate and
        //      reconnect attempts are not browsing traffic and must not starve page loads.
        var permitLimit = configuration.GetValue("RateLimiting:PermitLimit", 600);
        var window = TimeSpan.FromSeconds(configuration.GetValue("RateLimiting:WindowSeconds", 60));

        // AUD-024: focused per-route limits for the anonymous surfaces that cost money or
        // send email (register/OTP resend/forgot-password/contact). Without them, OTP
        // bombing burns Brevo credits and a staff inbox can be flooded; the global window
        // is far too wide to stop a patient abuser. Configurable so E2E environments can
        // widen the budget (RateLimiting:Sensitive:PermitLimit).
        var sensitiveLimit = configuration.GetValue("RateLimiting:Sensitive:PermitLimit", 5);
        var sensitiveWindow = TimeSpan.FromSeconds(configuration.GetValue("RateLimiting:Sensitive:WindowSeconds", 3600));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;

                // Tell clients (and caches) how long to wait, when the limiter knows.
                var retryAfter = window;
                if (context.Lease.TryGetMetadata("RETRY_AFTER", out var leaseRetry) && leaseRetry is TimeSpan leaseRetryTime)
                    retryAfter = leaseRetryTime;
                http.Response.Headers.RetryAfter = new Microsoft.Extensions.Primitives.StringValues(
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString());

                Serilog.Log.Warning(
                    "Rate limit exceeded for {Method} {Path} (IP {Ip}); responded 429. Retry after {RetryAfter}s.",
                    http.Request.Method, http.Request.Path,
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    (int)Math.Ceiling(retryAfter.TotalSeconds));

                var traceId = http.TraceIdentifier;
                var message = "You are making requests too quickly. Please wait a few seconds and try again.";
                if (ErrorPageWriter.IsBrowserHtmlRequest(http))
                {
                    // Full styled page with a "Try again" button - the failure is visible and
                    // recoverable instead of a blank document.
                    var retryHref = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
                    await ErrorPageWriter.WriteAsync(http, StatusCodes.Status429TooManyRequests,
                        message, traceId, retryHref: retryHref.ToString());
                    return;
                }

                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(
                    ApiResponse.Fail(message, "too.many.requests", null, traceId),
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    }), cancellationToken);
            };

            // AUD-024: named policy for sensitive anonymous endpoints — applied per action
            // with [EnableRateLimiting("sensitive")]; its own small fixed window per IP.
            options.AddPolicy("sensitive", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    "sensitive:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = sensitiveLimit,
                        Window = sensitiveWindow,
                        QueueLimit = 0
                    }));

            // Global fixed window per client IP; /hubs is the authenticated real-time channel.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                    return RateLimitPartition.GetNoLimiter("realtime");

                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = window,
                        QueueLimit = 0
                    });
            });
        });
        return services;
    }

    public static IServiceCollection AddGalleryVideoReEncodeBackfill(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GalleryBackfillOptions>()
            .Bind(configuration.GetSection(GalleryBackfillOptions.SectionName))
            .ValidateOnStart();
        services.AddScoped<DeChimex.Host.Services.GalleryVideoReEncodeService>();
        services.AddHostedService<DeChimex.Host.Services.GalleryVideoReEncodeBackgroundService>();
        return services;
    }

    /// <summary>
    /// Marketing broadcast background worker. Sends are additionally gated by
    /// Marketing:AllowBroadcasts (default false) so no environment accidentally emails
    /// the whole customer list (see MarketingOptions).
    /// </summary>
    public static IServiceCollection AddMarketingBroadcastWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DeChimex.Infrastructure.Options.MarketingOptions>()
            .Bind(configuration.GetSection(DeChimex.Infrastructure.Options.MarketingOptions.SectionName))
            .ValidateOnStart();
        services.AddScoped<DeChimex.Host.Services.MarketingBroadcastProcessor>();
        services.AddHostedService<DeChimex.Host.Services.MarketingBroadcastBackgroundService>();
        return services;
    }

    public static IServiceCollection AddHealthChecksConfigured(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<DeChimexDbContext>("database", tags: new[] { "ready" })
            // MED-007: external dependency checks. Failures degrade the detailed report rather
            // than failing the readiness probe (these carry the "external" tag, not "ready").
            .AddCheck<DeChimex.Host.Health.PaystackHealthCheck>("paystack",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
                tags: new[] { "external", "paystack" })
            .AddCheck<DeChimex.Host.Health.BrevoHealthCheck>("brevo",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
                tags: new[] { "external", "brevo" })
            // Developer Portal checks: tagged "dev" and EXCLUDED from the public /health payload.
            .AddCheck<DeChimex.Host.DevPortal.StorageHealthCheck>("storage", tags: new[] { "dev" })
            .AddCheck<DeChimex.Host.DevPortal.RealtimeHealthCheck>("realtime", tags: new[] { "dev" })
            .AddCheck<DeChimex.Host.DevPortal.BackgroundJobsHealthCheck>("background-jobs", tags: new[] { "dev" })
            // AUD-030: runtime readiness — SignalR channel health + a write-probe of BOTH
            // upload roots (public media and the private uploads root). Public on /health.
            .AddCheck<DeChimex.Host.Health.RuntimeHealthChecks>("runtime", tags: new[] { "ready" });
        return services;
    }
}
