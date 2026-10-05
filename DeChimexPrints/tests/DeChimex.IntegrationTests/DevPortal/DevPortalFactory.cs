using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Diagnostics;
using DeChimex.Infrastructure.Payments;
using DeChimex.IntegrationTests.Fakes;
using DeChimex.Persistence.Context;
using DeChimex.Host.DevPortal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Net;
using FluentAssertions;
using System.Text.RegularExpressions;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>
/// Test host for the Developer Portal: seeds the developer account through the real
/// Seed:Developer* settings, instruments the fake Paystack gateway exactly like production
/// (InstrumentedPaymentGateway decorator) and replaces the two network health checks with
/// switchable deterministic checks so healthy/unhealthy states can be asserted offline.
/// </summary>
public class DevPortalFactory : CustomWebApplicationFactory
{
    public const string DevEmail = "developer@dechimex.test";
    public const string DevPassword = "DevPortal!Test9";

    public HealthStatus PaystackHealth { get; set; } = HealthStatus.Healthy;
    public HealthStatus BrevoHealth { get; set; } = HealthStatus.Healthy;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Seed:DeveloperEmail", DevEmail);
        builder.UseSetting("Seed:DeveloperPassword", DevPassword);
        builder.ConfigureServices(services =>
        {
            var gw = services.Where(d => d.ServiceType == typeof(IPaymentGateway)).ToList();
            foreach (var d in gw) services.Remove(d);
            services.AddSingleton<FakePaymentGateway>();
            services.AddScoped<IPaymentGateway>(sp => new InstrumentedPaymentGateway(
                sp.GetRequiredService<FakePaymentGateway>(), sp.GetRequiredService<IIntegrationCallRecorder>()));

            services.PostConfigure<HealthCheckServiceOptions>(o =>
            {
                Replace(o, "paystack", () => PaystackHealth, "Paystack reachable (test).");
                Replace(o, "brevo", () => BrevoHealth, "Brevo reachable (test).");
            });
        });
    }

    private static void Replace(HealthCheckServiceOptions o, string name, Func<HealthStatus> status, string ok)
    {
        var existing = o.Registrations.FirstOrDefault(r => r.Name == name);
        if (existing is not null) o.Registrations.Remove(existing);
        IEnumerable<string> tags = existing?.Tags is { } t ? t : new[] { "external" };
        o.Registrations.Add(new HealthCheckRegistration(name, (IServiceProvider _) => (IHealthCheck)new SwitchCheck(status, ok),
            HealthStatus.Unhealthy, tags));
    }

    private sealed class SwitchCheck(Func<HealthStatus> status, string ok) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
            => Task.FromResult(status() switch
            {
                HealthStatus.Healthy => HealthCheckResult.Healthy(ok),
                HealthStatus.Degraded => HealthCheckResult.Degraded("Slow responses (test)."),
                _ => HealthCheckResult.Unhealthy("Unreachable (test).", new HttpRequestException("Connection refused"))
            });
    }

    public FakePaymentGateway Gateway => Services.GetRequiredService<FakePaymentGateway>();

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public async Task<HttpClient> DeveloperAsync()
    {
        var c = Browser();
        (await TestSeed.LoginMvcAsync(c, DevEmail, DevPassword)).Should().BeTrue("the seeded developer must be able to sign in");
        return c;
    }

    /// <summary>Flushes queued technical events / integration calls and metric buckets to the database.</summary>
    public async Task FlushDiagnosticsAsync()
    {
        await Services.GetRequiredService<DiagnosticsWriterService>().DrainAsync(CancellationToken.None);
        await Services.GetRequiredService<MetricsFlushService>().FlushAsync(CancellationToken.None);
        await Services.GetRequiredService<DiagnosticsWriterService>().DrainAsync(CancellationToken.None);
    }

    public async Task<T> WithDbAsync<T>(Func<DeChimexDbContext, Task<T>> f)
    {
        using var scope = Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<DeChimexDbContext>());
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient c, string pageUrl, string postUrl, Dictionary<string, string> data)
    {
        var page = await c.GetAsync(pageUrl);
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        data["__RequestVerificationToken"] = token;
        return await c.PostAsync(postUrl, new FormUrlEncodedContent(data));
    }

    public static bool IsDenied(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
        || (r.StatusCode == HttpStatusCode.Redirect && (r.Headers.Location?.ToString() ?? "").Contains("AccessDenied", StringComparison.OrdinalIgnoreCase));
}
