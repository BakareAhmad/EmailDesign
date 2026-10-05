# Developer Portal — Architecture Assessment (pre-implementation)

Date: 2026-09-25 · Base commit `a316bd4` (v047-CustomerUXRedesign + provider keys) · 748/748 tests green before any change.

This assessment was written **before** implementation from a read of the actual code
(not the docs). Section references (§) point at the task brief.

---

## 0. What the solution is today (audit summary)

| Area | What exists (verified in code) | Relevance to a Dev Portal |
|---|---|---|
| Architecture | Clean-architecture .NET 9: Domain / Application (MediatR CQRS, FluentValidation behaviours) / Persistence (EF Core, SQL Server; `Database:UseInMemory` for sandbox) / Infrastructure (Brevo, Paystack, storage, cache, seeder) / Host (MVC + a residual `/api/v1` surface + SignalR). | Portal must be a Host area reusing Application/Persistence, not a new app. |
| Authentication | Cookie auth (`AccountController.SignInAsync`), claims = roles + `permission` claims read from `RolePermissions` at login. PBKDF2 `PasswordHasher`. `must_change_password` claim enforced by a middleware for `/admin/*`. | Reuse as-is. Developer is a normal `User` with a role. |
| Authorization | `PermissionPolicyProvider` turns any string in `Permissions.All()` into a policy; `PermissionAuthorizationHandler` checks the claim. `ActivityAccess` shows the "live DB permission check" pattern (stale cookies can't keep revoked access). | Add a **separate** `Permissions.Developer` registry (NOT in `All()`), teach the provider about it, add a live DB check for the portal. |
| Roles | Owner (= `All()`), Manager (`All()` − users.* − activity.view), SalesStaff, InventoryStaff, Designer, Customer. Canonical map applied only at role creation; the admin checklist (`/admin/users/roles`) then owns role permissions. | Because Owner/Manager maps are built from `All()`, putting dev permissions in `All()` would silently give them to Owner/Manager — exactly what the brief forbids. |
| Seeding | `DatabaseSeeder` (idempotent): migrate/EnsureCreated → permissions → roles → Owner (from `Seed:*` config, force password change) → settings → catalog. | Extend with Developer role + optional developer account from `Seed:Developer*` config. |
| Database | EF Core 9, 48 DbSets, 30+ migrations; SQL Server in prod, InMemory in sandbox/tests. `AuditableSoftDeleteInterceptor` writes AuditLog rows for `AggregateRoot`/`IAuditable` changes. | New technical tables must derive from `Entity` (not AggregateRoot) so they never generate business audit rows. |
| Technical logging | **Serilog** → Console + rolling file `logs/dechimex-.log` (31 days). `CorrelationIdMiddleware` pushes `CorrelationId`; `ExceptionHandlingMiddleware` logs 5xx as Error (with exception), 4xx as Warning; request logging via `UseSerilogRequestLogging`. ~60 `logger.LogWarning/LogError` call sites across Paystack, Brevo, storage, background workers, SignalR pushers. **Nothing is queryable from the UI** — logs are files on the server. | Biggest gap. A Serilog sink can turn the *existing* log calls into persisted technical events with zero changes at call sites. |
| Business audit | `AuditLogs` table + `BusinessActivityCapture` → Admin "Business activity" (`/admin/activity`) with realtime dispatcher. | Keep separate. Technical events carry the same `CorrelationId`/`OrderId` so they can be correlated, never merged. |
| Health checks | `/health` (JSON: database via `AddDbContextCheck`, `paystack`, `brevo` probes), `/health/live`, `/health/ready`. Anonymous. Admin Settings has an email health card + "send test email". | Reuse the same `HealthCheckService` for the portal (cached, re-runnable). |
| Metrics | **None.** No request counters, no latency, no job status. | Minimum instrumentation required (request metrics middleware, job monitor, integration call records). |
| Exception handling | Global middleware; never leaks stack traces to customers (styled error page / JSON envelope). | Keep. Portal shows stack traces only to `dev.logs.view`. |
| SignalR | `NotificationHub` (/hubs/notifications, auto-joins `perm:*` groups), `OrderChatHub` (/hubs/orderchat) + `OrderPresenceTracker`. No connection/failure counters. | Add a global `IHubFilter` (no hub code changes) for connection/invocation metrics; a small dedicated dev hub for critical pushes. |
| Background work | Hosted services: `BusinessActivityDispatcher` (2 s loop), `MarketingBroadcastBackgroundService` (10 s poll), `GalleryVideoReEncodeBackgroundService` (one-shot backfill). `BackgroundEmailQueue` = fire-and-forget `Task.Run` per email (no retry, no persistence). No status anywhere. | In-memory job monitor with heartbeats; failures also become persisted technical events. No new job framework. |
| Email | `BrevoEmailService` (HTTP v3), 18 templates via `EmailTemplateRenderer`; failures only logged. Bodies contain OTPs/links. | Need a per-attempt delivery record (template, masked recipient, status, provider error). **Never** store bodies. |
| Paystack | `PaystackGateway` (initialize / verify / refund / webhook signature), `HandlePaystackWebhookHandler` (signature, duplicate, mismatch guards), `VerifyPaymentHandler`, `PaymentGatewayRefundCoordinator`. Outcomes only logged. | Need per-call integration records + webhook outcome records. |
| Storage | `LocalFileStorageService` under `wwwroot/uploads`, ffmpeg re-encode. | Health = writable probe + free disk; failures via the log sink. |
| Cache | `IMemoryCache` via `MemoryCacheService` (catalog/category keys, 5-min TTL). No session, no distributed cache. | "Clear application cache" is safe (entries rebuild on demand). |
| Service fee | `Payment` rows are an immutable per-attempt snapshot: `OrderAmount`, `ServiceFeePercent`, `CustomerBorneCharge` (= applied service fee), `PaystackFeeExpected/Actual`, `Status`, `PaidAtUtc`, `RefundedAtUtc`, `RefundReference`. Config key `service_fee.percent` changes never rewrite rows (SERVICE_FEE_REPORT §8). | This already *is* a transaction-level ledger. Reuse it. |
| Refunds | Whole-order only: `RefundOrderPaymentsAsync` refunds each **Succeeded** Paystack payment for its **full `Amount`** (order + service fee) and marks it `Refunded`; a failed provider refund throws `ConflictException` and leaves the payment `Succeeded`. No partial refunds exist. | Business rule to surface: a refunded payment's service fee was returned to the customer → reverses the fee. |
| Existing dev utilities | Swagger in Development only; `/health`; admin email test. No diagnostic endpoints, no SQL console. | Good — nothing dangerous to remove. |

## 1. Classification of technical information (§2)

**Essential monitoring** — overall health (DB / Paystack / Brevo / storage / realtime / jobs), unhandled
exceptions (grouped), integration failures (Paystack init/verify/refund/webhook, Brevo sends), background
job failures and staleness, request error rate + slow endpoints, authentication/authorization failures,
service-fee accumulation.

**Useful diagnostics** — migration list/pending state, configuration presence (configured / missing /
test-mode), runtime/version/build/uptime, request volume per endpoint, SignalR connection counts, email
per-template success, payment status mismatches (webhook amount/currency mismatch, amount-mismatch
verifications), disk usage of uploads.

**Dangerous / never in the UI** — secret values (Paystack sk_, Brevo key, SQL password, connection
string), raw Paystack payloads (`Payment.RawResponse` contains customer email / card BIN/last4 /
authorization codes), email bodies (OTP codes, reset links), customer PII beyond what a diagnosis needs,
raw request bodies, cookies/tokens, arbitrary SQL / shell / file browsing, destructive DB operations.

**Unnecessary duplication (stays in Admin)** — orders, customers, refunds UI, business activity feed,
revenue analytics, service-fee *configuration* (Owner/Manager two-step flow), notifications, staff.

## 2. Answers to the twelve architectural questions (§41)

1. **Technical logging that already exists** — Serilog console + daily file; structured properties
   (`CorrelationId`, machine, environment); error logs from the global exception middleware and ~60
   component call sites. Not persisted in a queryable store, no UI.
2. **Business auditing that already exists** — `AuditLogs` (generic entity-change audit + business
   activity descriptors: type/category/title/summary/actor/order/customer/importance) surfaced at
   `/admin/activity` with realtime refresh. Unchanged by this work.
3. **Health checks** — ASP.NET Core `HealthCheckService` with `database`, `paystack`, `brevo`. Reused.
   Added: `storage` and `realtime` checks and a `background-jobs` check driven by the job monitor.
4. **Metrics** — none. Nothing is fabricated: every number in the portal comes from a new instrument
   or an existing table.
5. **Monitoring buildable from existing data** — service-fee ledger (`Payments`), payment-status
   anomalies (`Payments.Status/FailureReason`), health checks, migrations (EF metadata), configuration
   presence (options), runtime/process info, business-audit correlation (`AuditLogs.CorrelationId/OrderId`).
6. **Instrumentation to add (minimum)** — (a) Serilog sink capturing Warning+ (and explicitly tagged
   Information events) into technical events, with redaction; (b) explicit security events (login
   failure, access denied, rate-limited); (c) integration call recorder at the Paystack gateway boundary
   (decorator — gateway code untouched), in the webhook handler, and in the Brevo service;
   (d) request-metrics middleware aggregated into 5-minute buckets; (e) job monitor heartbeats in the
   three hosted services + the email queue; (f) SignalR hub filter counters; (g) slow-DB-command
   interceptor (relational providers only); (h) startup/shutdown/migration events.
7. **New tables actually necessary** — three, all append-only and retention-pruned:
   `TechnicalEvents` (technical audit log + error center source), `IntegrationCalls` (Paystack/Brevo
   per-attempt records incl. webhooks and email deliveries), `RequestMetricBuckets` (5-min aggregates
   per endpoint). **No service-fee ledger table** (see 10). No job table (see 8).
8. **Transient vs persisted** — Persisted: technical events, integration calls, request metric buckets
   (they must survive restarts to answer "what happened last night"). Transient (process memory, reset
   on restart and labelled "since process start"): job heartbeats/last-run state, SignalR connection
   counts, health-check cache, sink queue depth/drop counters. Failures of transient subsystems are
   *also* written as technical events, so nothing important is lost on restart.
9. **Retention** — configurable `DevPortal:RetentionDays` (default 30) for technical events and
   integration calls, `DevPortal:MetricsRetentionDays` (default 30) for buckets, enforced hourly by a
   retention job (and on demand from Maintenance). Writes go through a bounded channel (capacity 5,000,
   drop-oldest with a visible drop counter) and are batch-inserted every 2 s off the request path.
10. **Service-fee source** — the `Payments` table, Paystack method only. Each row already freezes
    `ServiceFeePercent`, `CustomerBorneCharge` (applied fee), `OrderAmount`, `PaystackFeeExpected/Actual`,
    status and dates at the time of the attempt; global config changes never rewrite it. Rules taken from
    the code, not invented: fee is *earned* when a payment is `Succeeded` or `Refunded` (was collected);
    a `Refunded` payment returned its full `Amount` including the fee → reversal of the whole fee; failed /
    initialized / abandoned attempts collected nothing → excluded but listed for reconciliation; partial
    refunds do not exist; a failed refund leaves the payment `Succeeded` → fee still counted.
    Net = gross collected − reversed. Totals are computed with SQL aggregates over the same filtered rows
    the ledger table/CSV shows, so the figure reconciles row-for-row. A separate copied ledger would add a
    second source of truth that can drift — rejected.
11. **Permission integration** — new `Permissions.Developer` registry (`dev.portal.view`,
    `dev.logs.view`, `dev.payments.view`, `dev.servicefees.view`, `dev.maintenance.run`), **excluded from
    `Permissions.All()`** so Owner/Manager canonical maps never pick it up; recognised by the existing
    `PermissionPolicyProvider`; seeded Permission rows attached only to a new system role **Developer**
    (no business permissions). Developer access is granted exclusively by holding the Developer role
    (Owner can assign it explicitly via existing user management). The admin role checklist hides `dev.*`
    and preserves them on save so the Developer role can't be broken accidentally. Portal controllers add a
    live DB re-check (the `ActivityAccess` pattern) so revocation is immediate.
12. **Safe maintenance operations** — re-run health checks (bypass cache); clear application memory
    cache; flush request metrics now; apply log retention now; re-verify a *pending* Paystack payment with
    the provider (same idempotent `VerifyPaymentCommand` the webhook path uses — it can only mark paid if
    Paystack confirms the exact amount). All POST + anti-forgery + confirmation + `dev.maintenance.run` +
    technical audit entry. **Excluded:** email retry (bodies deliberately not stored — they contain OTPs;
    the business flows already offer "resend"), webhook replay (raw payloads are not stored — PII),
    any SQL/shell/file browser, drop/delete data, config editing, refunds, price/fee changes.

## 3. Resulting portal information architecture

Dashboard · Health · Errors · Technical log · Performance · Integrations (Paystack, Brevo, storage,
realtime) · Payments diagnostics · Email diagnostics · Service fees (+ CSV) · Jobs · Database ·
Configuration · Maintenance · System info. Separate `_DevLayout` + `dev.css`, route prefix `/dev`.
