using System.Text.Json.Serialization;
using DeChimex.Host.Extensions;
using DeChimex.Host.Middleware;
using DeChimex.Application;
using DeChimex.Contracts.Common;
using DeChimex.Infrastructure;
using DeChimex.Infrastructure.Persistence;
using DeChimex.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Serilog;

// QuestPDF runs under the community license (free for small teams). Set before any
// document is generated.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// ---------------- Serilog ----------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithMachineName());

// ---------------- Layers ----------------
builder.Services.AddApplication();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

// ---------------- Configuration Validation ----------------
// Validate critical configuration settings before building the app
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Database connection string is not configured. " +
        "Please set 'ConnectionStrings:DefaultConnection' using dotnet user-secrets or environment variables. " +
        "See SETUP.md for instructions.");
}

// JWT signing key is no longer required: MVC login/register authenticate via cookie and
// never use the (removed) ITokenService access/refresh tokens, so the app boots without
// Jwt:SigningKey. See AI_HANDOFF.txt (repo root) for the JWT-independence change.

// ---------------- AUD-020: secret precedence probe ----------------
// Secrets now exist in appsettings*.json (single-box convenience). Configuration order gives
// environment variables and user secrets the final say, so both remain valid overrides —
// but a secret that silently rides on appsettings.json in a deployment can go unnoticed.
// Probe the environment-variable source directly: if it carries no override, warn that the
// value in effect comes from appsettings.json (keys stay in appsettings; this only informs
// the operator — environment variables / user secrets still win by configuration order).
var secretWarnings = new List<string>();
foreach (var (key, envName) in new[]
{
    ("Paystack:SecretKey", "PAYSTACK__SECRETKEY"),
    ("Brevo:ApiKey", "BREVO__APIKEY")
})
{
    var fromEnv = Environment.GetEnvironmentVariable(envName) is { Length: > 0 };
    var effective = builder.Configuration[key];
    if (!fromEnv && !string.IsNullOrWhiteSpace(effective))
        secretWarnings.Add($"{key} is served by appsettings.json (no {envName} override). " +
                           "Set the environment variable or user secret to override per environment.");
}

// ---------------- HSTS (production HTTPS enforcement, HIGH-005) ----------------
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHsts(options =>
    {
        options.Preload = true;
        options.IncludeSubDomains = true;
        options.MaxAge = TimeSpan.FromDays(365);
    });
}

// ---------------- API + MVC ----------------
// AddControllersWithViews() registers the same JSON API controllers as AddControllers()
// plus MVC controllers returning Razor Views. API controllers are attribute-routed at
// /api/v{version}/[controller]; MVC controllers use conventional routes mapped below.
builder.Services.AddResponseCaching();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// Return our standardised envelope for model-binding validation failures too.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .ToDictionary(e => e.Key, e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray());
        var response = ApiResponse.Fail("One or more validation errors occurred.", "validation.failed", errors, context.HttpContext.TraceIdentifier);
        return new BadRequestObjectResult(response);
    };
});

builder.Services.AddApiVersioningConfigured();
builder.Services.AddAppAuthentication(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddSwaggerDocumentation();
builder.Services.AddCorsConfigured(builder.Configuration);
builder.Services.AddRateLimitingConfigured(builder.Configuration);
builder.Services.AddHealthChecksConfigured(builder.Configuration);
builder.Services.AddGalleryVideoReEncodeBackfill(builder.Configuration);
builder.Services.AddMarketingBroadcastWorker(builder.Configuration);
// ---------------- Developer Portal diagnostics (see DEV_PORTAL_REPORT.md) ----------------
// Serilog picks up DI-registered sinks through ReadFrom.Services (configured above).
builder.Services.AddSingleton<Serilog.Core.ILogEventSink, DeChimex.Host.DevPortal.TechnicalEventSink>();
builder.Services.AddSingleton<DeChimex.Host.DevPortal.RequestMetricsCollector>();
builder.Services.AddSingleton<DeChimex.Host.DevPortal.MetricsFlushService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeChimex.Host.DevPortal.MetricsFlushService>());
builder.Services.AddSingleton<DeChimex.Host.DevPortal.RealtimeMonitor>();
builder.Services.AddSingleton<DeChimex.Host.DevPortal.RealtimeMonitorHubFilter>();
builder.Services.AddSingleton<DeChimex.Infrastructure.Diagnostics.ITechnicalEventObserver, DeChimex.Host.DevPortal.DevAlertPusher>();
builder.Services.AddScoped<DeChimex.Host.DevPortal.DevHealthService>();
builder.Services.AddScoped<DeChimex.Host.DevPortal.DevPortalQueries>();
builder.Services.AddScoped<DeChimex.Host.DevPortal.ServiceFeeReportService>();

builder.Services.AddSignalR(o => Microsoft.AspNetCore.SignalR.HubOptionsExtensions.AddFilter<DeChimex.Host.DevPortal.RealtimeMonitorHubFilter>(o));
builder.Services.AddHostedService<DeChimex.Host.Realtime.BusinessActivityDispatcher>();
builder.Services.AddSingleton<DeChimex.Application.Common.Interfaces.INotificationPusher, DeChimex.Host.Realtime.NotificationPusher>();
builder.Services.AddSingleton<DeChimex.Application.Common.Interfaces.IOrderChatPusher, DeChimex.Host.Realtime.OrderChatPusher>();
builder.Services.AddSingleton<DeChimex.Application.Common.Interfaces.IOrderPresenceTracker, DeChimex.Host.Realtime.OrderPresenceTracker>();
// Customer shell data (business info + major categories) for _CustomerLayout — per request.
builder.Services.AddScoped<DeChimex.Host.Services.CustomerShellService>();

var app = builder.Build();

// AUD-020: surface secret precedence at startup (after Serilog is live).
if (secretWarnings.Count > 0)
{
    var bootLog = app.Services.GetRequiredService<ILogger<Program>>();
    foreach (var w in secretWarnings)
        bootLog.LogWarning("Secret configuration: {Warning}", w);
}

// ---------------- Migrate & seed ----------------
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

// Technical lifecycle events (Developer Portal technical log).
{
    var recorder = app.Services.GetRequiredService<DeChimex.Application.Common.Interfaces.ITechnicalEventRecorder>();
    recorder.Record(new DeChimex.Application.Common.Interfaces.TechnicalEventEntry(
        DeChimex.Application.Common.Interfaces.TechnicalSeverity.Information,
        DeChimex.Application.Common.Interfaces.TechnicalCategory.System,
        DeChimex.Application.Common.Interfaces.TechnicalEventType.SystemStart,
        $"Application started ({app.Environment.EnvironmentName}, .NET {Environment.Version}).", Source: "Program"));
    // AUD-003: durable outbox replaces the fire-and-forget queue (the processor
    // self-registers its job; the line below is kept only for the pre-drain listing).
    app.Services.GetRequiredService<DeChimex.Application.Common.Interfaces.IJobMonitor>().Register(
        DeChimex.Infrastructure.Email.OutboxEmailProcessor.JobKey, "Email outbox",
        "Dispatches transactional emails from the durable outbox with retries.", expectedInterval: null, recurring: true);
    app.Lifetime.ApplicationStopping.Register(() => recorder.Record(new DeChimex.Application.Common.Interfaces.TechnicalEventEntry(
        DeChimex.Application.Common.Interfaces.TechnicalSeverity.Information,
        DeChimex.Application.Common.Interfaces.TechnicalCategory.System,
        DeChimex.Application.Common.Interfaces.TechnicalEventType.SystemStop,
        "Application stopping.", Source: "Program")));
}

// ---------------- Pipeline ----------------
// AUD-017: forwarded-headers strategy. When deployed behind a reverse proxy / load
// balancer, RemoteIpAddress (rate-limit partitions, login lockout, audit IPs) and the
// request scheme (HTTPS-only cookies, redirects) must come from X-Forwarded-For/Proto
// — otherwise every client shares the proxy's identity (one abuser can exhaust the
// whole 600/min window) and audit IPs are worthless. Only headers from TRUSTED proxies
// are honoured: configure the proxy addresses via ForwardedHeaders:KnownProxies /
// KnownNetworks; with none configured the middleware ignores forwarded headers from
// unknown senders (safe for direct deployments and local development).
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                       | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
foreach (var proxy in app.Configuration.GetSection("ForwardedHeaders:KnownProxies").GetChildren()
             .Select(c => System.Net.IPAddress.Parse(c.Value!)))
    forwardedOptions.KnownProxies.Add(proxy);
foreach (var network in app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").GetChildren()
             .Select(c => Microsoft.AspNetCore.HttpOverrides.IPNetwork.Parse(c.Value!)))
    forwardedOptions.KnownNetworks.Add(network);
app.UseForwardedHeaders(forwardedOptions);

// Outermost: request metrics see the FINAL status code (including 500s written by the
// exception middleware and re-executed error pages).
app.UseMiddleware<DeChimex.Host.DevPortal.RequestMetricsMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "DE-CHIMEX PRINT HUB API v1");
        o.DocumentTitle = "DE-CHIMEX PRINT HUB API";
    });
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseResponseCompression();
// AUD-021: confidential uploads are authorized here (private categories never reach
// UseStaticFiles; their files live outside wwwroot entirely).
app.UseMiddleware<DeChimex.Host.Middleware.UploadAuthorizationMiddleware>();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Cache uploaded images for 7 days, immutable where possible; other static for 1 day
        var path = ctx.Context.Request.Path.Value ?? string.Empty;
        var headers = ctx.Context.Response.Headers;
        if (path.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase))
        {
            // Uploaded product/gallery images change rarely, cache 7 days, must-revalidate
            headers.CacheControl = "public,max-age=604800,must-revalidate";
            headers["Expires"] = DateTime.UtcNow.AddDays(7).ToString("R");
        }
        else if (string.Equals(path, "/service-worker.js", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(path, "/site.webmanifest", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(path, "/offline.html", StringComparison.OrdinalIgnoreCase))
        {
            // PWA plumbing must revalidate on every load: a cached service worker delays
            // update detection (browsers cap SW-age at 24h, but no-cache is stronger and
            // keeps manifest/icon changes from going stale in between).
            headers.CacheControl = "no-cache";
        }
        else
        {
            // Other wwwroot files, if any, use a short cache
            headers.CacheControl = "public,max-age=86400";
        }
    }
});
app.UseResponseCaching();
app.UseCors("DeChimexCors");
app.UseRateLimiter();

// Browser-friendly error pages for the MVC surface; /api requests keep their JSON contract.
// Hand-rolled (not UseStatusCodePagesWithReExecute) for two reasons: the built-in re-execute
// variant rewrites every 4xx/5xx body and would clobber the JSON 401/403/409/422 contract of
// the API, and it cannot tell browser from API clients. This is also the app-wide blank-page
// safety net: ANY error status that would otherwise reach a browser with an empty body is
// re-executed into a real error page (404 -> /Error/NotFound, other 4xx/5xx -> /Error?code=N),
// so no middleware or endpoint can ever leave a user staring at a blank document.
// Status-code pages must run BEFORE routing: the re-executed request has to pass through
// routing again so the error endpoint is re-selected. The original path is exposed to the
// controller via IStatusCodeReExecuteFeature.
app.UseStatusCodePages(async context =>
{
    var http = context.HttpContext;
    var status = http.Response.StatusCode;

    // Only error responses are candidates for rescue.
    if (status is < 400 or >= 600)
        return;
    // Something already wrote a body (or declared one): leave it alone.
    if (http.Response.HasStarted
        || http.Response.ContentLength.HasValue
        || !string.IsNullOrEmpty(http.Response.ContentType))
        return;
    // Loop guard: never re-execute an already re-executed request.
    if (http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>() is not null)
        return;

    var isApi = http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    if (isApi)
    {
        // Preserve the historical JSON-404 contract for the API; other statuses are the
        // caller's business (auth events write their own 401/403 bodies).
        if (status != StatusCodes.Status404NotFound)
            return;
    }
    else if (!DeChimex.Host.Middleware.ErrorPageWriter.IsBrowserHtmlRequest(http))
    {
        return;
    }

    var originalPath = http.Request.Path.Value ?? string.Empty;
    var originalQuery = http.Request.QueryString.Value ?? string.Empty;
    http.Request.Path = status == StatusCodes.Status404NotFound ? "/Error/NotFound" : "/Error";
    http.Request.QueryString = status == StatusCodes.Status404NotFound
        ? QueryString.Empty
        : QueryString.Create("code", status.ToString());
    http.Features.Set<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>(
        new Microsoft.AspNetCore.Diagnostics.StatusCodeReExecuteFeature
        {
            OriginalPathBase = http.Request.PathBase.Value ?? string.Empty,
            OriginalPath = originalPath,
            OriginalQueryString = originalQuery,
        });
    // Clear the endpoint the first pass selected (as the built-in re-execute does). UseRouting
    // skips matching when an endpoint is already set, so without this the re-executed request ran
    // the ORIGINAL action a second time (e.g. AdminCustomers.Details returning NotFound() twice,
    // or a POST failing anti-forgery re-running the POST) and the browser got a BLANK 404/400.
    // The error page is a read: re-execute it as GET so a failed POST (e.g. an anti-forgery 400)
    // reaches the GET-only ErrorController instead of a 405. ErrorController re-applies the
    // original status code, so callers still see 400/403/404/...
    http.SetEndpoint(null);
    http.Request.RouteValues.Clear();
    http.Request.Method = HttpMethods.Get;
    await context.Next(http);
});
app.UseRouting();
// Remember the route TEMPLATE selected for this request (before any status-code re-execution).
app.Use(DeChimex.Host.DevPortal.RequestMetricsMiddleware.CaptureRoute);
app.UseAuthentication();

// AUD-006: per-request authorization freshness — disabled accounts are signed out on
// their next request and role/permission changes take effect immediately (the cookie is
// re-issued when the user's SecurityStamp no longer matches the cookie claim).
app.UseMiddleware<DeChimex.Host.Middleware.SessionFreshnessMiddleware>();

// First-login password change enforcement (bootstrap owner). While the signed-in user
// still carries the must_change_password claim, every /admin page bounces to the
// change-password screen. The change screen itself, storefront, portal, and
// login/logout are unaffected. Cleared by the first successful password change.
app.Use(async (context, next) =>
{
    var user = context.User;
    var path = context.Request.Path.Value ?? string.Empty;
    if (user.Identity?.IsAuthenticated == true
        && user.HasClaim("must_change_password", "true")
        && path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/admin/security/password", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/admin/security/password?forced=1");
        return;
    }
    // Same rule for the Developer Portal (its own change-password screen).
    if (user.Identity?.IsAuthenticated == true
        && user.HasClaim("must_change_password", "true")
        && (path.Equals("/dev", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase))
        && !path.StartsWith("/dev/account/password", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/dev/account/password?forced=1");
        return;
    }
    await next();
});

app.UseAuthorization();

app.MapControllers();
app.MapHub<DeChimex.Host.Realtime.NotificationHub>("/hubs/notifications").RequireAuthorization();
app.MapHub<DeChimex.Host.Realtime.OrderChatHub>("/hubs/orderchat").RequireAuthorization();
app.MapHub<DeChimex.Host.DevPortal.DevHub>("/hubs/dev").RequireAuthorization(DeChimex.Shared.Constants.Permissions.Developer.PortalView);
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Health endpoints
// Public detailed report: unchanged payload (Developer Portal "dev"-tagged checks excluded).
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => !check.Tags.Contains("dev"),
    ResponseWriter = DeChimex.Host.Health.HealthReportWriter.WriteJson
});
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

/// <summary>Exposed for integration testing via WebApplicationFactory.</summary>
public partial class Program { }
