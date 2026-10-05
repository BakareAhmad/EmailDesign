using System.Net;
using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Enums;
using DeChimex.Host.DevPortal;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace DeChimex.IntegrationTests.DevPortal;

/// <summary>Health states, technical log search/filters/redaction, error grouping, metrics accuracy,
/// maintenance safety and secret hygiene. Uses its own factory so health toggles don't leak.</summary>
public class DevPortalDiagnosticsTests : IClassFixture<DevPortalFactory>
{
    private readonly DevPortalFactory _f;
    public DevPortalDiagnosticsTests(DevPortalFactory f)
    {
        _f = f;
        _f.PaystackHealth = HealthStatus.Healthy;
        _f.BrevoHealth = HealthStatus.Healthy;
    }

    private ITechnicalEventRecorder Recorder => _f.Services.GetRequiredService<ITechnicalEventRecorder>();

    private async Task<JsonElement> StatusAsync(HttpClient c)
        => JsonDocument.Parse(await (await c.GetAsync("/dev/api/status")).Content.ReadAsStringAsync()).RootElement;

    private static string Tile(JsonElement status, string key)
        => status.GetProperty("tiles").EnumerateArray().First(t => t.GetProperty("key").GetString() == key).GetProperty("status").GetString()!;

    private async Task<HttpResponseMessage> MaintainAsync(HttpClient c, string op, string? confirm = "yes", string? reference = null)
    {
        var data = new Dictionary<string, string>();
        if (confirm is not null) data["confirm"] = confirm;
        if (reference is not null) data["reference"] = reference;
        return await DevPortalFactory.PostFormAsync(c, "/dev/maintenance", $"/dev/maintenance/{op}", data);
    }

    private static async Task<string> FollowAsync(HttpClient c, HttpResponseMessage r)
        => await (await c.GetAsync(r.Headers.Location!.ToString())).Content.ReadAsStringAsync();

    [Fact]
    public async Task Healthy_then_unhealthy_then_recovered_is_reflected_on_the_dashboard_after_a_rerun()
    {
        var c = await _f.DeveloperAsync();
        await MaintainAsync(c, "rerun-health");
        var s = await StatusAsync(c);
        Tile(s, "paystack").Should().Be("Healthy");
        Tile(s, "db").Should().Be("Healthy");
        Tile(s, "storage").Should().Be("Healthy");

        _f.PaystackHealth = HealthStatus.Unhealthy;
        (await StatusAsync(c).ContinueWith(t => Tile(t.Result, "paystack"))).Should().Be("Healthy", "results are cached for 30 s until a re-run");
        var rerun = await MaintainAsync(c, "rerun-health");
        (await FollowAsync(c, rerun)).Should().Contain("paystack Unavailable");
        s = await StatusAsync(c);
        Tile(s, "paystack").Should().Be("Unavailable");
        s.GetProperty("overall").GetString().Should().Be("Unavailable");
        var health = await (await c.GetAsync("/dev/health")).Content.ReadAsStringAsync();
        health.Should().Contain("Unreachable (test).").And.Contain("HttpRequestException");

        _f.BrevoHealth = HealthStatus.Degraded;
        _f.PaystackHealth = HealthStatus.Healthy;
        await MaintainAsync(c, "rerun-health");
        s = await StatusAsync(c);
        Tile(s, "email").Should().Be("Degraded");
        s.GetProperty("overall").GetString().Should().Be("Degraded");

        _f.BrevoHealth = HealthStatus.Healthy;
        await MaintainAsync(c, "rerun-health");
        await _f.FlushDiagnosticsAsync();
        (await _f.WithDbAsync(db => db.TechnicalEvents.CountAsync(e => e.EventType == TechnicalEventType.HealthChanged)))
            .Should().BeGreaterThanOrEqualTo(2, "each change of overall health is recorded");
    }

    [Fact]
    public async Task Public_health_endpoint_payload_is_unchanged_by_the_dev_checks()
    {
        var body = await (await _f.CreateClient().GetAsync("/health")).Content.ReadAsStringAsync();
        body.Should().NotContain("\"storage\"").And.NotContain("\"background-jobs\"").And.NotContain("\"realtime\"");
    }

    [Fact]
    public async Task Technical_log_search_filters_correlation_and_redaction()
    {
        var marker = "zq" + Guid.NewGuid().ToString("N")[..10];
        var corr = "corr-" + marker;
        var order = Guid.NewGuid();
        Recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Error, TechnicalCategory.Payments, "TestFailure",
            $"Charge {marker} failed password=Hunter22 token sk_live_abcdefghijklmnop card 4111111111111111 for jane.doe@example.com",
            ExceptionType: "System.TimeoutException", OrderId: order, Route: "POST /checkout", Integration: IntegrationNames.Paystack,
            Properties: new Dictionary<string, string?> { ["password"] = "Hunter22", ["authorization"] = "Bearer abc.def.ghi", ["step"] = "verify" }));
        Recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Warning, TechnicalCategory.Email, "TestWarn", $"Slow {marker} email"));
        Recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Information, TechnicalCategory.System, "TestInfo", $"Info {marker}"));
        using (new CorrelationScope(corr))
            Recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Information, TechnicalCategory.System, "TestInfo", $"Correlated {marker}"));
        await _f.FlushDiagnosticsAsync();

        var stored = await _f.WithDbAsync(db => db.TechnicalEvents.AsNoTracking().SingleAsync(e => e.EventType == "TestFailure" && e.Message.Contains(marker)));
        stored.Message.Should().NotContain("Hunter22").And.NotContain("sk_live_abcdefghijklmnop").And.NotContain("4111111111111111").And.NotContain("jane.doe@example.com");
        (stored.Properties ?? "").Should().NotContain("Hunter22").And.NotContain("abc.def.ghi").And.Contain("verify");
        stored.Environment.Should().NotBeNullOrEmpty();
        stored.Fingerprint.Should().MatchRegex("^[0-9a-f]{40}$");

        var c = await _f.DeveloperAsync();
        async Task<string> Logs(string qs) => await (await c.GetAsync("/dev/logs?range=1h&" + qs)).Content.ReadAsStringAsync();

        (await Logs($"q={marker}")).Should().Contain("4 event(s)");
        (await Logs($"q={marker}&severity=Error")).Should().Contain("1 event(s)");
        (await Logs($"q={marker}&severity=Warning%2B")).Should().Contain("2 event(s)", "Warning+ = Warning, Error and Critical");
        (await Logs($"q={marker}&category=Email")).Should().Contain("1 event(s)");
        (await Logs($"q={marker}&integration=Paystack")).Should().Contain("1 event(s)");
        (await Logs($"orderId={order}")).Should().Contain("1 event(s)");
        (await Logs($"correlationId={corr}")).Should().Contain("1 event(s)").And.Contain("Correlated");
        (await Logs($"q={marker}&eventType=TestInfo")).Should().Contain("2 event(s)");
        (await Logs("q=no-such-thing-" + marker)).Should().Contain("No technical events match");
        (await Logs($"q={marker}&orderId=not-a-guid")).Should().Contain("must be full GUIDs");
        (await (await c.GetAsync($"/dev/logs?q={marker}&range=bogus")).Content.ReadAsStringAsync())
            .Should().Contain("Last 24 hours", "invalid ranges fall back safely");

        var detail = await (await c.GetAsync($"/dev/logs/{stored.Id}")).Content.ReadAsStringAsync();
        detail.Should().Contain("System.TimeoutException").And.Contain(order.ToString()).And.NotContain("Hunter22");
        (await c.GetAsync($"/dev/logs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Redirect, "missing events redirect with a message");
    }

    [Fact]
    public async Task Errors_are_grouped_with_counts_first_last_seen_and_drill_down()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 3; i++)
            Recorder.Record(new TechnicalEventEntry(TechnicalSeverity.Error, TechnicalCategory.Storage, "UploadFailed",
                $"Upload {i} failed", ExceptionType: "System.IO.IOException", MessageTemplate: "Upload {Index} failed " + marker));
        await _f.FlushDiagnosticsAsync();
        var fp = await _f.WithDbAsync(db => db.TechnicalEvents.Where(e => e.MessageTemplate!.Contains(marker)).Select(e => e.Fingerprint).Distinct().ToListAsync());
        fp.Should().HaveCount(1, "same category/type/exception/template = one group");

        var c = await _f.DeveloperAsync();
        var errors = await (await c.GetAsync("/dev/errors?range=1h&category=Storage")).Content.ReadAsStringAsync();
        errors.Should().Contain($"/dev/errors/{fp[0]}").And.Contain("<strong>3</strong>");
        var group = await (await c.GetAsync($"/dev/errors/{fp[0]}")).Content.ReadAsStringAsync();
        group.Should().Contain("Upload 0 failed").And.Contain("Upload 2 failed").And.Contain("System.IO.IOException");
        (await c.GetAsync("/dev/errors/" + new string('0', 40))).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await c.GetAsync("/dev/errors/not-hex")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the fingerprint route is constrained");
    }

    [Fact]
    public async Task Request_metrics_are_exact_and_security_failures_are_counted()
    {
        var c = await _f.DeveloperAsync();
        // Request metrics are counted when the response completes (after the client has it), so
        // let in-flight requests from earlier tests settle before taking the baseline.
        await Task.Delay(500);
        await _f.FlushDiagnosticsAsync();
        using var scope = _f.Services.CreateScope();
        var q = scope.ServiceProvider.GetRequiredService<DevPortalQueries>();
        var before = await q.PerformanceAsync(DevRange.Parse("1h", null, null));
        async Task<int> Persisted(string r) => await _f.WithDbAsync(db => db.RequestMetricBuckets
            .Where(b => b.Route == r && b.Method == "GET").SumAsync(b => b.RequestCount));
        var jobsBefore = await Persisted("/dev/jobs");

        for (var i = 0; i < 7; i++) (await c.GetAsync("/dev/jobs")).EnsureSuccessStatusCode();
        var anon = _f.Browser();
        for (var i = 0; i < 2; i++) await TestSeed.LoginMvcAsync(anon, "nobody@dechimex.test", "wrong-password");
        await c.GetAsync("/admin/users");
        await Task.Delay(500);
        await _f.FlushDiagnosticsAsync();

        var after = await q.PerformanceAsync(DevRange.Parse("1h", null, null));
        ((await Persisted("/dev/jobs")) - jobsBefore).Should().Be(7, "every request is counted exactly once");
        (after.Requests - before.Requests).Should().BeGreaterThanOrEqualTo(7 + 2 + 1);
        (after.AuthenticationFailures - before.AuthenticationFailures).Should().Be(2);
        (after.AuthorizationFailures - before.AuthorizationFailures).Should().BeGreaterThanOrEqualTo(1);
        after.Requests.Should().BeGreaterThan(before.Requests);
        var html = await (await c.GetAsync("/dev/performance?range=1h")).Content.ReadAsStringAsync();
        html.Should().Contain("GET /dev/jobs");
        (await c.GetAsync("/dev/performance?range=custom&from=2026-13-01&to=x")).StatusCode.Should().Be(HttpStatusCode.OK, "bad custom dates fall back");
    }

    [Fact]
    public async Task Maintenance_requires_csrf_confirmation_and_permission_and_is_audited()
    {
        var c = await _f.DeveloperAsync();
        (await c.PostAsync("/dev/maintenance/clear-cache", new FormUrlEncodedContent(new Dictionary<string, string> { ["confirm"] = "yes" })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "no anti-forgery token");

        var noConfirm = await MaintainAsync(c, "clear-cache", confirm: null);
        (await FollowAsync(c, noConfirm)).Should().Contain("tick the confirmation box");
        (await FollowAsync(c, await MaintainAsync(c, "drop-database"))).Should().Contain("Unknown maintenance action");

        foreach (var op in new[] { "clear-cache", "clear-cache", "flush-diagnostics", "apply-retention" })
            (await FollowAsync(c, await MaintainAsync(c, op))).Should().Contain("dv-flash-ok", $"{op} succeeds and is idempotent");

        await _f.FlushDiagnosticsAsync();
        var audits = await _f.WithDbAsync(db => db.TechnicalEvents.AsNoTracking().Where(e => e.EventType == TechnicalEventType.MaintenanceAction).ToListAsync());
        audits.Count(a => a.Message.Contains("Clear application cache")).Should().BeGreaterThanOrEqualTo(2);
        audits.Should().OnlyContain(a => a.Message.StartsWith("Developer ") && a.UserId != null);
        audits.Should().NotContain(a => a.Message.Contains("drop-database"), "rejected requests run nothing");
        (await (await c.GetAsync("/dev/maintenance")).Content.ReadAsStringAsync()).Should().Contain("Clear application cache");
    }

    [Fact]
    public async Task Reverify_validates_the_reference_and_only_touches_pending_paystack_payments()
    {
        var c = await _f.DeveloperAsync();
        (await FollowAsync(c, await MaintainAsync(c, "reverify-payment", reference: ""))).Should().Contain("Enter the payment reference");
        (await FollowAsync(c, await MaintainAsync(c, "reverify-payment", reference: "NOPE-123"))).Should().Contain("No payment with reference NOPE-123");

        var (orderId, _) = await FeeFlow.PlaceOrderAsync(_f, 5000m, custom: false);
        var (pid, reference) = await FeeFlow.InitializeAsync(_f, orderId);
        _f.Gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        var done = await FollowAsync(c, await MaintainAsync(c, "reverify-payment", reference: reference));
        done.Should().Contain("status now Succeeded");
        (await _f.WithDbAsync(db => db.Payments.SingleAsync(p => p.Id == pid))).Status.Should().Be(PaymentStatus.Succeeded);

        var again = await FollowAsync(c, await MaintainAsync(c, "reverify-payment", reference: reference));
        again.Should().Contain("already Succeeded; nothing to do");
        (await _f.WithDbAsync(db => db.Payments.CountAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Succeeded))).Should().Be(1);
    }

    [Fact]
    public async Task Paystack_calls_are_recorded_and_payment_diagnostics_never_show_raw_payloads_or_customer_emails()
    {
        var (orderId, _) = await FeeFlow.PlaceOrderAsync(_f, 4000m, custom: false);
        var (_, reference) = await FeeFlow.InitializeAsync(_f, orderId);
        _f.Gateway.VerifyOutcome = PaymentVerificationOutcome.Failed;
        await FeeFlow.VerifyAsync(_f, reference);
        _f.Gateway.VerifyOutcome = PaymentVerificationOutcome.Succeeded;
        await _f.FlushDiagnosticsAsync();

        var calls = await _f.WithDbAsync(db => db.IntegrationCalls.AsNoTracking().Where(x => x.Reference == reference).ToListAsync());
        calls.Should().Contain(x => x.Operation == "initialize" && x.Succeeded);
        // A declined card is a successful provider call whose outcome is "Failed" (a customer
        // decline is not an integration failure and does not count against Paystack health).
        calls.Should().Contain(x => x.Operation == "verify" && x.Outcome == "Failed" && x.Succeeded);

        var c = await _f.DeveloperAsync();
        var html = await (await c.GetAsync("/dev/payments?range=1h")).Content.ReadAsStringAsync();
        html.Should().Contain(reference).And.NotContain("\"{}\"").And.NotContain("@yopmail.com");
        (await (await c.GetAsync("/dev/integrations?range=1h")).Content.ReadAsStringAsync()).Should().Contain("Recent failures");
    }

    [Fact]
    public async Task No_portal_page_or_api_ever_contains_secret_values()
    {
        var cfg = _f.Services.GetRequiredService<IConfiguration>();
        var secrets = new[] { cfg["Paystack:SecretKey"], cfg["Paystack:PublicKey"], cfg["Brevo:ApiKey"], cfg["Email:ApiKey"],
                              cfg["ConnectionStrings:DefaultConnection"], cfg["Jwt:Secret"], cfg["Jwt:Key"], DevPortalFactory.DevPassword }
            .Where(s => !string.IsNullOrWhiteSpace(s) && s!.Length >= 8).ToList();
        secrets.Should().NotBeEmpty();
        var c = await _f.DeveloperAsync();
        foreach (var p in DevPortalAccessTests.DevPages.Append("/dev/api/status").Append("/dev/service-fees/export"))
        {
            var body = await (await c.GetAsync(p)).Content.ReadAsStringAsync();
            foreach (var s in secrets) body.Should().NotContain(s!, $"{p} must never render a secret");
        }
        var config = await (await c.GetAsync("/dev/configuration")).Content.ReadAsStringAsync();
        config.Should().Contain("Paystack keys").And.Contain("Configured");
    }

    /// <summary>Sets the ambient correlation id for code running outside an HTTP request.</summary>
    private sealed class CorrelationScope : IDisposable
    {
        private readonly string? _prev;
        public CorrelationScope(string id) { _prev = DeChimex.Shared.Diagnostics.TechnicalContext.CorrelationId; DeChimex.Shared.Diagnostics.TechnicalContext.CorrelationId = id; }
        public void Dispose() => DeChimex.Shared.Diagnostics.TechnicalContext.CorrelationId = _prev;
    }
}
