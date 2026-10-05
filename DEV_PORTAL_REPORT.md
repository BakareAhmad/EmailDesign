# Developer Portal — Architecture & Implementation Report

The pre-implementation audit, including the information classification (essential / useful / dangerous / duplicate) and the answers to the twelve §41 questions, is in **`DEV_PORTAL_ARCHITECTURE_ASSESSMENT.md`**. This report covers what was built, how to use it, how it was verified, and its known limits.

## 1. What was built

The portal lives at **`/dev`**. It is a third portal next to Customer (`/portal`) and Admin (`/admin`), with its own layout, CSS and JS. It loads no external resources.

| Section | Route | What it shows |
|---|---|---|
| Dashboard | `/dev` | Overall status plus tiles for app, database, Paystack, email, storage, SignalR and jobs, each rated Healthy / Warning / Degraded / Unavailable. Every tile links to its detail page. Also shows 24 h critical/error/warning/security counts, recent errors, the last hour's traffic, jobs and today's fees. Refreshes every 30 s from `/dev/api/status`. |
| System health | `/dev/health` | Uptime, start time, environment, version/build, runtime, every check with its latency, DB provider and migrations (applied/pending), storage free space, and SignalR connections. Never shows connection strings. |
| Statistics | `/dev/performance` | Requests, 4xx/5xx, error rate, average and max duration, slow requests, auth/authz failures, failures by subsystem, and top/slowest routes. Ranges: 1h / 24h / 7d / 30d / custom. |
| Background jobs | `/dev/jobs` | 7 jobs with state, last run/success/failure, failure count, duration and next expected run. |
| Technical log | `/dev/logs`, `/dev/logs/{id}` | Search plus filters for severity (with `+` for "and above"), category, event type, integration, correlation ID, order ID and user ID. Paged. The detail view shows the stack trace, metadata, and a correlation link to all events of the same request. |
| Error center | `/dev/errors`, `/dev/errors/{fingerprint}` | Errors grouped by fingerprint (category + event type + exception type + message template), with occurrences, first/last seen and subsystem. Drill down to individual occurrences. |
| Paystack & Brevo | `/dev/integrations` | Config status (Configured / Missing / Invalid, test vs live), reachability, call counts by outcome, failures, last success and last failure. |
| Payment diagnostics | `/dev/payments` | Initialize, verify, webhook and refund outcomes. Covers duplicates, amount/currency mismatches, rejected signatures, stuck payments and recent problems. No raw payloads and no customer contact data. |
| Email diagnostics | `/dev/email` | Attempted / succeeded / failed / skipped counts, broken down by template, recipient category and trigger, plus provider errors with correlation. No message bodies, and no send tool. |
| Service fees | `/dev/service-fees`, `/ledger`, `/{paymentId}`, `/export` | Totals for today / week / month / year / all time / custom range, each with the previous period for comparison. Monthly history, percentages used, a traceable ledger, per-transaction detail, and a PII-free CSV export. |
| Database & migrations | `/dev/database` | Read-only: provider, latency, applied/pending migrations, diagnostics-table sizes and slow commands. No forms. |
| Configuration | `/dev/configuration` | Status of each setting (Configured / Missing / Invalid, test/live), plus an environment/version panel. Never shows values. |
| Maintenance | `/dev/maintenance` | Five safe tools: re-run health checks, clear cache, flush diagnostics, apply retention, and re-verify a payment. |
| Change password | `/dev/account/password` | Reuses the existing `ChangePasswordCommand`. |

**Realtime:** the SignalR hub `/hubs/dev` pushes **Error and Critical** events only, and only to users who hold the dev permission.

## 2. Architecture: the smallest design that works

- **Reused:**
  - Serilog, since every existing `ILogger` warning or error is captured by one extra sink (`TechnicalEventSink`);
  - ASP.NET health checks (the public `/health` payload is unchanged);
  - the existing permission policy provider;
  - the MVC controllers and layout conventions;
  - EF Core and its migrations;
  - `IMemoryCache`;
  - the business audit log, which is untouched and correlatable through the correlation ID.
- **Three new tables** (migration `20260925232100_AddDeveloperPortalDiagnostics`, additive only):
  - `TechnicalEvents`
  - `IntegrationCalls`
  - `RequestMetricBuckets` (per minute, route and method)
  - All three are indexed for the portal's time-window queries.
- **The Payments table is the service-fee ledger.** At checkout the app already snapshots the percentage, order amount, expected Paystack fee and applied fee on each payment row. Nothing is duplicated and nothing can drift.
- **Write path:** Sink / recorders → redaction → bounded in-memory queue → a background writer that batches inserts every 2 s. A logging burst can never block a request; if the queue overflows, it drops events and counts them.
- **Transient vs persisted:** health results are cached for 30 s, request metrics are aggregated in memory and flushed each minute, and events and calls are persisted.
- **Retention:** 30 days (configurable), applied hourly.
- **Redaction** (`SensitiveDataRedactor`, used for every message, stack trace and property):
  - Paystack and Brevo keys, bearer tokens, `password=`/`token=`-style pairs, and connection-string passwords are removed.
  - Card numbers are removed after a Luhn check, including space- or dash-grouped forms.
  - Emails are masked (`a***@domain`).
  - Any property whose *name* is sensitive is redacted wholesale.

## 3. Access model

- **Five dedicated permissions:** `dev.portal.view`, `dev.logs.view`, `dev.payments.view`, `dev.servicefees.view` and `dev.maintenance.run`.
  - They are deliberately **not** part of `Permissions.All()`, so the Owner and every existing role do not inherit them.
  - They are granted only through the seeded **Developer** role.
- **The Developer role holds nothing else.** In particular: no customer deletion, pricing, refunds, settings, staff permissions or owner credentials, and no customer PII. The E2E proves `/admin/settings`, `/admin/users`, `/admin/customers` and `/admin/orders` are denied, and that a service-fee POST is refused.
- **Owner/Manager get dev access only if explicitly granted.** The Owner can assign the Developer role through the existing role assignment. The role-permission editor hides `dev.*` ids and preserves them on save.
- **Customers and normal staff** never see a dev link, and get Access Denied on `/dev` and `/dev/api/status`.
- **Every `/dev` request** is checked against the policy, then re-checked against the live database (so revoked access takes effect immediately), then checked for the page-level permission. Denials are logged as Security events.
- **Seeding** is idempotent by email and environment-aware:
  - Passwords are hashed with the existing hasher.
  - It never takes over a non-developer account.
  - **Sandbox/Development:** `developer@dechimexprinthub.local` / `DevPortal#2026!` (from `appsettings.Development.json`).
  - **Production:** set `Seed__DeveloperEmail` and `Seed__DeveloperPassword` through environment variables or secrets. Base `appsettings.json` ships them blank. Outside Development the account must change its password at first login.

## 4. Service-fee rules (taken from the app's code, and shown in the portal)

- **Collected:** when the payment is `Succeeded`, counted on the paid date in the Africa/Lagos calendar.
- **Not collected:** Failed, Abandoned, Initialized and Pending attempts. They are listed but never counted. A retry creates a new attempt, and only the one that succeeds counts.
- **Refunds are always full** (order amount plus fee); **the app has no partial refunds.** On a successful refund the fee is reversed on the refund date. A failed refund leaves the payment Succeeded and the fee collected.
- **Net** = gross − reversed for the same period, so a month can be net negative.
- **Historical percentage:** each payment keeps its own percentage, and changing the configuration never rewrites old rows. Proven by the E2E: a payment made at 50% still shows 50.00% in the ledger, detail page and CSV after the configuration is set back to 100%.
- **Scope:** Paystack payments only. Direct and custom orders are both included.
- **CSV export:** UTF-8 BOM, formula-injection guard, no names or emails. Summing its `NetFee` column reproduces the portal's net total (asserted in the integration tests).

## 5. Maintenance safety

- **Every tool requires:**
  - a POST with an anti-forgery token (a POST without one gets 400);
  - the `dev.maintenance.run` permission;
  - `confirm=yes`, enforced on the server even if the browser guard is bypassed.
- **Every tool is idempotent.** For example, re-verifying an already-paid payment is a no-op.
- **Every run is audited** in the technical log as *"Developer X ran 'Y': result"*.
- **Deliberately absent:** SQL console, shell, file browser, data deletion, email sender and webhook replay.

## 6. Defects found by the sandbox E2E and fixed

1. **EF Core warning flood (pre-existing).** `Invoice` and `Receipt` shared their order's or payment's `Money` instances. `Money` is an EF owned type, so EF tracked one object as two entity types and warned on every save: 81 warnings in a single journey. Fixed with `Money.Copy()` in `Invoice.Issue`, quote conversion and `Receipt`, plus unit tests. The warning count in the final run is 0.
2. **Expected business rejections showed as errors.** The MediatR `LoggingBehavior` logs every handler exception at Error, including a wrong password (`UnauthorizedAppException`) and the custom-product cart guard (`ConflictException`). The sink now ignores application and domain rule exceptions from that pipeline log. Those outcomes are already recorded as authentication-failure / access-denied events and 4xx metrics.
3. **Over-redaction broke correlation.** Payment references like `PAY-20260925235026-…` were masked as card numbers. Card detection now requires a Luhn-valid number and skips timestamp shapes. Grouped card formats are now also caught.
4. **An unreachable provider showed as "Degraded".** The existing Paystack/Brevo checks report connection failures as Degraded, deliberately, so the public readiness probe stays up. That is unchanged. The dev portal now maps a connectivity failure on an external check to **Unavailable**.
5. **Events could be silently lost (a race).** Fire-and-forget work started by a request, such as the email queue's `Task.Run`, still sees that request's `HttpContext`. If it logged after the response completed, reading the recycled context threw, and the recorder's silent `catch` dropped the event. In one run this lost the Brevo outage error. Request enrichment is now defensive: a recycled context counts as "no request". Any remaining capture failure is **counted** and shown on System health ("capture failures"), never silent. A unit test reproduces the race, and the E2E asserts that the pipeline dropped 0 events, had 0 failed writes and 0 capture failures.
6. **Client-aborted requests showed as errors.** An `OperationCanceledException` from the request pipeline means the browser navigated away. It is no longer reported as a fault.
7. **Sensitive property names were not redacted.** A structured property such as `password` with a plain value was stored as-is. Such values are now redacted by key name (found by the integration tests).

## 7. Verification

- **Tests:** **829/829** (454 unit + 375 integration) passed, 0 failed. That is the 748 pre-existing tests plus the new ones: access/permissions, health healthy and unhealthy, log filters/search/redaction, exact request metrics, error grouping, maintenance CSRF/confirmation/audit, service-fee calculations, historical preservation, and a ledger flow covering success, decline, abandoned, retry, refund, failed refund, direct and custom orders. It also covers redactor, job-monitor, status-mapping and `Money`-ownership units.
- **Sandbox E2E** (`e2e/dev-portal/run_final.sh`, one clean run, in-memory DB, Paystack and Brevo stubs):
  - public `/health` Healthy;
  - HTTP smoke of every dev page, 29/29;
  - checkout regression journey, 60/60 (unchanged customer, admin, checkout, payment and refund behaviour);
  - developer-portal journey, **107/107**;
  - secret-leak sweep of all evidence clean.
- **Evidence** is in `e2e/dev-portal/evidence-final-run/` (originally `evidence/`): ordered screenshots `01-…` to `30-…` following §44 (login → dashboard → health → logs → errors → stats → integrations → payment diagnostics → service fees → fee detail → jobs → DB → config → maintenance → access denial), including validation and error states. Also final logs and the fee CSV.
- The previous task's evidence was moved to `e2e/provider-keys/evidence-final-run/`.

## 8. Known limits and observations (not changed)

- The E2E uses the in-memory database. The migration was generated with `dotnet-ef` and is additive; apply it with the normal migration step before deploying.
- Request metrics are per instance, so multi-instance deployments sum in the DB but each instance's current minute is in memory until flushed.
- A job that only records runs when it has work can look "Stale" when idle. The staleness threshold is 3× the job's interval.
- ~~Pre-existing: `NotificationHub` put every connection, customers included, in the staff group, and the shared error pages offered customers an "Admin dashboard" button.~~ **Fixed in the follow-up pass: see `FIXES_REPORT.md`.**
