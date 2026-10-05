# DeChimexPrints — Comprehensive Feature & Implementation Audit

**Date:** 2026-10-04 · **Scope:** Full solution audit (`DeChimex.sln`: Domain, Application, Contracts, Infrastructure, Persistence, Host, UnitTests, IntegrationTests) · **Mode:** AUDIT ONLY — no code, configuration, schema, test, or UI changes were made.

**Evidence standard:** every finding below was verified by reading the actual source (file/method references given), by configuration inspection, and — where stated — by test/runtime observation. Findings are labelled **Confirmed** (determined by the repository itself), **Likely** (strong code evidence, environmental factor involved), or **Potential** (depends on deployment/usage). Secrets are referenced by location only, never by value.

---

## 1. Executive Summary

- **Total findings: 34** — 1 critical (9/10), 3 high (8/10), 4 very-high-priority (7/10), 6 important (6/10), 9 moderate (5/10), 8 lower (4/10), 2 minor (3/10), 1 trivial (2/10).
- **By category:** 14 faulted implementations · 5 architectural weaknesses · 6 security findings · 5 missing features · 4 optimizations.
- **The three findings that matter most:**
  1. **AUD-020 (9/10):** live provider credentials (Brevo API key, Paystack test keys, SQL `sa` password, seed passwords) are committed in `appsettings*.json` **and the repository is public on GitHub**. The Brevo key is a *live sending key* — anyone can send email as the business.
  2. **AUD-001 (8/10):** the Admin Analytics and Reports surfaces compute "paid revenue" with `OrderStatus >= Paid` — an enum comparison that includes the five **unpaid** custom-order states (`AwaitingReview`=10 … `OutForDelivery`=15). Any shop using the custom-order flow (the primary flow) gets materially inflated revenue in `/admin/analytics` and `/admin/reports/sales|products|services`.
  3. **AUD-002 (8/10):** payment confirmation idempotency is check-then-act, not atomic. Paystack's webhook and the browser return-verify run **concurrently in practice**; the race can double-count `Customer.LifetimeSpending`, raise duplicate `OrderPaidEvent`s (duplicate confirmation emails) and create duplicate receipts (`Receipts` has **no** unique index on `OrderId`).
- **Major areas requiring attention:** financial reporting correctness, payment-confirmation concurrency, credential management, authorization freshness (stale cookie claims), email durability, refund settlement reconciliation, and upload authorization.
- **Notable strengths (do not refactor these):** clean layered/CQRS architecture with domain events; a **single** Order aggregate for direct + custom flows; an excellent payment ledger with per-attempt fee snapshots and amount/currency verification on both verify and webhook; the two-step, concurrency-safe service-fee change flow; atomic audit-at-save-boundary; a genuinely strong Developer Portal (jobs, metrics, integration-call ledger, fee reconciliation with independent recomputation); 887 green tests including extensive E2E journeys.

---

## 2. Findings Summary Table

| ID | Category | Area | Finding | Importance /10 | Current /10 | Suggested /10 |
|---|---|---|---|---|---|---|
| AUD-001 | Faulted Implementation | Financial reporting | Unpaid custom-order statuses counted as paid revenue in Analytics/Reports | 8 | 3 | 9 |
| AUD-002 | Faulted Implementation | Payments / concurrency | Payment-confirmation idempotency is check-then-act; webhook vs callback race double-counts spending | 8 | 3 | 9 |
| AUD-003 | Faulted Implementation | Email | Fire-and-forget in-process email queue: non-durable, no retry, lost on restart | 7 | 4 | 9 |
| AUD-004 | Faulted Implementation | Numbering / concurrency | Count-based `DCX-/INV-/RCT-` reference generation races under concurrency | 7 | 3 | 9 |
| AUD-005 | Faulted Implementation | Refunds | Refund marked final at provider *acceptance*; `refund.processed/failed` events unhandled, no reconciliation | 7 | 4 | 8 |
| AUD-006 | Faulted Implementation | Auth / permissions | Cookie claims never refresh: role/permission changes and account disable lag until re-login | 7 | 3 | 9 |
| AUD-007 | Faulted Implementation | Orders list (admin) | In-memory `Type` filter after paging corrupts totals; list query over-fetches full graphs | 6 | 4 | 9 |
| AUD-008 | Faulted Implementation | Order lifecycle UI | `AllowedActionsFor` offers "Refund" on `AwaitingProcurement`, which the domain rejects | 5 | 4 | 9 |
| AUD-009 | Faulted Implementation | Receipts (admin) | Receipts list loads the entire table then filters/pages in memory | 5 | 3 | 9 |
| AUD-010 | Faulted Implementation | Auth | Registration endpoint confirms whether an email is already registered (enumeration) | 5 | 4 | 8 |
| AUD-011 | Faulted Implementation | Payments | Payment row persisted only *after* successful gateway initialize → orphaned gateway transactions | 5 | 5 | 8 |
| AUD-012 | Faulted Implementation | Payments (staff) | Manual confirmation can mark a fee-inclusive Paystack attempt as paid with a different method | 4 | 4 | 8 |
| AUD-013 | Faulted Implementation | Files / design review | "Preview" generation is a placeholder `File.Copy` (PDFs copied as "previews") | 4 | 3 | 8 |
| AUD-014 | Faulted Implementation | Inventory | Production stock-out deducts `min(qty, onHand)`, silently under-deducting vs the reservation | 4 | 5 | 7 |
| AUD-015 | Architectural Weakness | Design workflow | Two parallel design-approval mechanisms (Proofs vs DesignReviews) with different semantics | 6 | 5 | 8 |
| AUD-016 | Architectural Weakness | Financial reporting | Revenue date-basis differs per surface (PaidAtUtc vs CreatedDateUtc) — figures never reconcile | 6 | 4 | 9 |
| AUD-017 | Architectural Weakness | Deployment / security | No forwarded-headers strategy: client IP, rate limiting and audit IPs keyed to the proxy | 6 | 4 | 9 |
| AUD-018 | Architectural Weakness | Order lifecycle | Cancelling a paid order keeps the money with no guided refund path or customer explanation | 6 | 4 | 8 |
| AUD-019 | Architectural Weakness | Deployment | Single-instance assumptions across background workers, local uploads, presence tracking | 5 | 6 | 8 |
| AUD-020 | Security (Critical) | Secrets | Live/test provider credentials committed to a **public** repository | 9 | — | — |
| AUD-021 | Security (High) | File access | Uploads (order attachments, chat media, proofs) served publicly, unauthenticated; inline SVG = same-origin script vector | 8 | 3 | 9 |
| AUD-022 | Security (Medium) | Headers / CSP | CSP allows `unsafe-inline` and `unsafe-eval` for scripts | 5 | 4 | 8 |
| AUD-023 | Security (Medium) | Cookies | Auth cookie `SecurePolicy.SameAsRequest` — insecure over accidental HTTP in production | 6 | 5 | 9 |
| AUD-024 | Security (Medium) | Anti-automation | No CAPTCHA/proof-of-work on register, login, contact, OTP-resend (spam/OTP-bombing) | 5 | 4 | 7 |
| AUD-025 | Security (Low) | Password storage | PBKDF2-SHA256 @ 100k iterations is below current OWASP guidance (600k) | 4 | 6 | 8 |
| AUD-026 | Missing Feature | Audit | No viewer for raw `AuditLogs` rows (Old/New value diffs) outside the business-activity projection | 5 | — | — |
| AUD-027 | Missing Feature | Continuity | Database backup / disaster-recovery runbook (checklist item still open) | 5 | — | — |
| AUD-028 | Missing Feature | Reporting | No CSV/PDF export for admin business reports | 3 | — | — |
| AUD-029 | Missing Feature | Mobile / PWA | Manifest is `display: browser`; no service worker → not installable, no offline states | 4 | — | — |
| AUD-030 | Missing Feature | Health | No SignalR or storage/disk health checks | 3 | — | — |
| AUD-031 | Optimization | Sales inbox | Bucket counts materialise every order row to count in memory | 4 | 6 | 8 |
| AUD-032 | Optimization | DI hygiene | `ICategoryOptionsQueries` / `IOrderWorkspaceQueries` registered twice | 2 | 7 | 9 |
| AUD-033 | Optimization | Data governance | `Payment.RawResponse` stores full gateway/webhook bodies (customer PII) indefinitely | 4 | 5 | 8 |
| AUD-034 | Optimization | Financial integrity | `Customer.LifetimeSpending` has no reconciliation/recompute path if it drifts | 4 | 6 | 8 |

---

## 3. Detailed Findings

### AUD-001 — Unpaid custom-order statuses are counted as paid revenue

**Category:** Faulted Implementation · **Area:** Financial reporting (Admin Analytics + Reports) · **Importance:** 8/10 · **Current:** 3/10 · **Suggested:** 9/10 · **Status: Confirmed**

**Evidence.** `OrderStatus` enum values (`src/DeChimex.Domain/Enums/Enums.cs`): `Paid=2, …, Cancelled=8, Refunded=9`, then the additive custom-order states `AwaitingReview=10, InDiscussion=11, RequirementsConfirmed=12, AwaitingDesignApproval=13, QualityCheck=14, OutForDelivery=15`. Four queries filter "paid" with a numeric comparison:
`src/DeChimex.Host/Controllers/Mvc/Admin/AdminAnalyticsController.cs` — `bool Paid(OrderStatus s) => s is >= OrderStatus.Paid and not OrderStatus.Cancelled and not OrderStatus.Refunded;` (used in `BuildSummaryAsync`, `BuildRevenueTrendAsync` `o.Status >= OrderStatus.Paid …`, `BuildTopProductsAsync` `validOrderIds`), and `src/DeChimex.Host/Controllers/Mvc/Admin/AdminReportsController.cs` — the same `o.Status >= OrderStatus.Paid && … != Cancelled && != Refunded` predicate in `Sales`, `Products`, `Services`. Because `AwaitingReview..OutForDelivery` (10–15) are all `>= Paid`, **an order request that a customer merely submitted, with zero payment, is summed into revenue, AOV, conversion, top products and service reports**. The Dashboard does it correctly: `src/DeChimex.Persistence/Queries/DashboardUserQueries.cs` `GetKpisAsync` uses an explicit `paidStatuses` whitelist *and* `PaidAtUtc != null`.

**Why it matters.** The owner makes pricing/stock/staffing decisions off `/admin/analytics` and `/admin/reports/sales`. A shop whose main flow is custom requests (this business's primary flow) sees revenue that includes unpaid quotes; the numbers diverge from the Dashboard and from the Developer-Portal fee ledger, destroying trust in all of them.

**Current implementation.** Enum-range predicate inherited from a time when `Paid` was near the top of the enum; the additive custom statuses (documented "values 10+ are additive") silently changed its meaning.

**Suggested approach.** Centralise one predicate, e.g. a `Domain.Enums` helper `OrderStatus.IsPaidLike(OrderStatus s)` (explicit whitelist: `Paid, AwaitingProcurement, InProduction, QualityCheck, Ready, OutForDelivery, Completed, Delivered`) — mirroring the existing `Order.IsActiveSalesWork` pattern — and additionally require `o.PaidAtUtc != null` for revenue sums, matching `DashboardQueries`. Replace the four inline predicates with it and add a regression test asserting an `AwaitingReview` order contributes 0 revenue.

**Why better:** single source of truth; impossible for a future enum addition to silently corrupt revenue again; aligns all surfaces.

**Risk of leaving as-is:** owner-facing financial figures remain wrong; decisions made on inflated revenue.

**Dependencies / impact:** AdminAnalyticsController, AdminReportsController, tests (`OrderPricingTests`/analytics assertions). Complexity: **Low**.

---

### AUD-002 — Payment-confirmation idempotency is check-then-act, not atomic (webhook vs callback race)

**Category:** Faulted Implementation · **Area:** Payments / concurrency / financial integrity · **Importance:** 8/10 · **Current:** 3/10 · **Suggested:** 9/10 · **Status: Confirmed** (race window; realistic trigger)

**Evidence.** Paystack's documented behaviour: the `charge.success` webhook and the customer's browser redirect to `/checkout/return` arrive **at nearly the same time**. Both paths converge on `PaymentConfirmationService.ConfirmPaidOrderAsync` (`src/DeChimex.Application/Features/Payments/PaymentConfirmationService.cs`), whose idempotency is `if (order.Status is Paid or Completed or Delivered) return false;` — a read-then-write with **no transaction, no lock, and no concurrency token** (no `RowVersion` anywhere in the model; `OrderConfiguration`/`PaymentConfiguration` in `SalesConfigurations.cs` define none). Similarly `HandleWebhook.cs` and `VerifyPayment.cs` both check `payment.Status is Succeeded or Refunded` before `MarkSucceeded`. Two concurrent executions both observe `PendingPayment`, both call `order.MarkPaid()`, both save, and both publish `OrderPaidEvent` ⇒:
- `customer.RecordSpending(order.GrandTotal)` runs **twice** (`PaymentConfirmationService` lines around `RecordSpending`) → `LifetimeSpending` double-counts the order;
- duplicate `OrderConfirmation` emails (`OrderEventHandlers.OrderPaidEventHandler`);
- duplicate receipts: receipt creation guards with `GetByOrderIdAsync(...) is null`, and `ReceiptConfiguration` (`SalesConfigurations.cs` lines 269–276) has a unique index **only on `ReceiptNumber`**, not `OrderId` — two receipts for one order are possible (invoices are protected by `HasIndex(x => x.OrderId).IsUnique()`; receipts are not).

Sequential idempotency (the tested case — `PaymentLifecycleTests`) is fine; only the concurrent interleaving corrupts state, and that interleaving is the *normal* Paystack flow.

**Why it matters.** `LifetimeSpending` is a customer-facing financial figure (`/admin/customers`, customer report) and receipt uniqueness is a financial-document guarantee. Silent money-figures corruption in the happy path is exactly the class of bug that is painful to detect later.

**Suggested approach.** Make confirmation atomic at the database: wrap `ConfirmPaidOrderAsync` in an explicit transaction with a row lock (`SELECT … WITH (UPDLOCK, ROWLOCK)` via `FromSqlRaw`/`EXEC sp_getapplock`, or simpler: add a `RowVersion` concurrency token to `Order` and retry on `DbUpdateConcurrencyException`), plus a **filtered unique index on `Payments (OrderId) WHERE Status = 'Succeeded'`** and on `Receipts (OrderId)` as hard backstops. The `MarkSucceeded` unique-reference index already blocks duplicate payment rows; the missing piece is the per-order single-succeeded guarantee.

**Why better:** converts a race-prone invariant into a database-enforced one; retry-on-conflict is simple and testable; the filtered indexes also make historical data provably safe.

**Risk of leaving as-is:** occasional double-counted spending/duplicate emails/receipts that increase with volume; reconciliation effort grows.

**Dependencies / impact:** PaymentConfirmationService, HandleWebhook, VerifyPayment, ConfirmManualPayment, migrations (index + rowversion), tests (add a parallel-invocation test). Complexity: **Medium**.

---

### AUD-003 — Email queue is fire-and-forget and non-durable

**Category:** Faulted Implementation · **Area:** Email / reliability · **Importance:** 7/10 · **Current:** 4/10 · **Suggested:** 9/10 · **Status: Confirmed**

**Evidence.** `src/DeChimex.Infrastructure/Email/EmailQueue.cs` (`BackgroundEmailQueue.EnqueueAsync`): every transactional email — verification OTP, password reset, order confirmation, payment receipt, quote-ready, design-proof-ready — is dispatched via `_ = Task.Run(...)` with `CancellationToken.None`, and failures are only logged (`logger.LogError`). There is **no persistence, no retry, no dead-letter**; an app restart, crash, or pool recycle silently discards everything queued. Brevo failures return `false` and are dropped (the comment in the file itself says "Swap this for a durable queue later without changing callers").

**Why it matters.** A lost verification email strands signup (mitigated only by manual "Resend"); a lost password-reset email locks a customer out; a lost quote-ready email stalls the custom-order funnel — and none of it is visible to staff. The *marketing* worker, by contrast, already models the right pattern (persisted per-recipient rows, at-most-once, retry), proving the codebase already knows how to do this.

**Suggested approach.** Persist an `EmailOutbox` row in the **same `SaveChanges`** as the business change (the codebase already has the perfect hook: the `AuditableSoftDeleteInterceptor` save boundary / `BusinessActivityCapture` pattern), and let a small `BackgroundService` drain `Pending` rows with per-row status/attempts, exactly like `MarketingBroadcastProcessor`. The existing `IEmailQueue.EnqueueAsync` signature can stay, so callers don't change.

**Why better:** at-least-once delivery with retry and an ops view (the Developer Portal job monitor already exists to show it); request latency unchanged; crash-safe.

**Risk of leaving as-is:** intermittent, invisible loss of the most business-critical emails.

**Dependencies / impact:** EmailQueue, one migration (outbox table), a background worker, Dev-portal job registration. Complexity: **Medium**.

---

### AUD-004 — Count-based document/reference generation races under concurrency

**Category:** Faulted Implementation · **Area:** Reference numbering / data integrity · **Importance:** 7/10 · **Current:** 3/10 · **Suggested:** 9/10 · **Status: Confirmed** (race window; unique indexes turn it into a user-facing error)

**Evidence.** `src/DeChimex.Persistence/Repositories/ReferenceGenerator.cs`: `NextOrderNumberAsync` etc. compute `$"DCX-{year}-{(count + 1):D6}"` from `CountForYearAsync`, which two concurrent checkouts can read simultaneously. `Orders.OrderNumber`, `Invoices.InvoiceNumber`, `Receipts.ReceiptNumber`, `Procurements.Reference` all have **unique indexes** (`SalesConfigurations.cs`, `ProcurementConfigurations.cs`), so the loser of the race hits `DbUpdateException` (SQL 2601/2627) → 500 at checkout for the customer (only the category-specific conflict mapping in `UnitOfWork.SaveChangesAsync` is caught, and only for categories).

**Why it matters.** Checkout is the single most revenue-sensitive request; a duplicate-number collision converts a normal "two customers place orders in the same second" event into a failed order plus a scary error, and staff-side `New order` / receipt generation has the same exposure.

**Suggested approach.** Keep the friendly format but make allocation collision-proof: retry-once-on-2601/2627 inside `ReferenceGenerator` callers (cheap and contained), or move to a per-year sequence table updated in the same transaction (`UPDATE Seq SET val = val + 1 OUTPUT …`), or fall back to `DCX-YYYY-<random suffix>` on collision. Retry-on-conflict is the smallest change consistent with the existing architecture.

**Why better:** deterministic, human-friendly numbers preserved; concurrency failure becomes invisible.

**Risk of leaving as-is:** rare but public order-placement failures; erodes checkout trust.

**Dependencies / impact:** ReferenceGenerator + its four callers; one retry helper; test with parallel order creation. Complexity: **Low–Medium**.

---

### AUD-005 — Refund lifecycle stops at provider *acceptance*; settlement events are unhandled

**Category:** Faulted Implementation · **Area:** Refunds / financial correctness · **Importance:** 7/10 · **Current:** 4/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** `src/DeChimex.Infrastructure/Payments/PaymentGatewayRefundCoordinator.cs` marks the payment `Refunded` the moment Paystack's `/refund` returns 2xx with a non-failed status, and the honest audit copy in `BusinessActivityCapture` says "Final settlement is not confirmed by this record." `PaystackGateway.RefundAsync` even documents "acceptance is not settlement." However, the webhook pipeline (`PaystackWebhookParser`) only parses `charge.success` — Paystack's `refund.processed` / `refund.failed` / `refund.pending` events are ignored. Consequently: if the refund later **fails** at the provider, the order stays `Refunded`, the receipt stays `IsRefunded`, and `Customer.ReverseSpending` has already run — with no reconciliation surface, no alert, and no retry other than the staff noticing and the coordinator throwing on a manual second attempt (`EnsureCanRefund` blocks refunded orders from being refunded again).

**Why it matters.** The customer-facing state says "money returned" while it may not be; the accounting reversal (spending, invoice `Refunded`) is committed on an unconfirmed premise. The system's own audit copy admits the gap but nothing operationalises it.

**Suggested approach.** Parse `refund.*` events in `HandlePaystackWebhook` (idempotent by `RefundReference`), add a `RefundStatus` (Requested → Processed → Failed) on `Payment`, and surface `refund.failed` as an `attention` business-activity row + Developer-Portal alert. Keep order-status change on *acceptance* but let settlement confirmation/failure update the payment-level truth and notify staff.

**Why better:** closes the loop with the provider the same way `charge.success` already is; no change to the customer-visible flow; adds a genuine reconciliation trail.

**Risk of leaving as-is:** silent mismatches between the books and Paystack; support escalations when customers say "no money received".

**Dependencies / impact:** PaystackGateway/webhook parser, Payment entity + migration, BusinessActivityCapture, Dev portal fee ledger (already distinguishes "accepted" vs settlement). Complexity: **Medium**.

---

### AUD-006 — Authorization claims never refresh after sign-in (stale permissions, stale disable)

**Category:** Faulted Implementation · **Area:** Authentication / authorization · **Importance:** 7/10 · **Current:** 3/10 · **Suggested:** 9/10 · **Status: Confirmed**

**Evidence.** Sign-in bakes roles/permissions into the cookie once (`AccountController.SignInAsync`); `AddAppAuthentication` uses an 8-hour sliding cookie whose claims are never re-issued (`ServiceCollectionExtensions.AddAppAuthentication`). Consequences, all confirmed in code:
- `AssignRolesCommand` / `UpdateRolePermissionsCommand` / `SetUserActiveCommand` (UserManagementFeature.cs) change the DB but do **not** invalidate sessions; a demoted or **disabled** staff member keeps full access for the remaining cookie life (sliding renewal can extend it indefinitely during continuous use). `LoginHandler` checks `IsActive` only at sign-in.
- The codebase itself acknowledges the problem for the Developer Portal — `DevPortal/DevAccess.cs`: "the Developer Portal never trusts permissions cached in the auth cookie alone … revoking … takes effect on the very next request" — and `NotificationHub` audiences are also claim-based. The business side has no equivalent.
- `ViewBag.Can*` UI flags (e.g. `AdminUsersController.Index`) derive from the same stale claims.

**Why it matters.** "Disable this person *now*" and "revoke refunds permission" are security operations, not UX preferences; an 8-hour lag (or longer) is a real access-control gap, especially for offboarding.

**Suggested approach.** Add a cheap per-request authority check the way `DevAccess` already does: a small `IClaimsTransformation`/authorization-handler step that verifies the user's `IsActive` (and optionally a `SecurityStamp`/`PermissionsVersion` column on `User`, bumped by the three commands above) and re-issues the cookie or rejects when stale. One indexed column read per request is well within budget at this scale.

**Why better:** consistent freshness semantics across the whole app (dev portal already behaves this way); revocation becomes immediate; no change to controllers.

**Risk of leaving as-is:** offboarding and permission changes are advisory, not enforced.

**Dependencies / impact:** cookie auth setup, User entity (+ migration), the three admin commands, tests. Complexity: **Medium**.

---

### AUD-007 — Admin order list: in-memory `Type` filter breaks pagination and the query over-fetches

**Category:** Faulted Implementation · **Area:** Admin orders / performance · **Importance:** 6/10 · **Current:** 4/10 · **Suggested:** 9/10 · **Status: Confirmed**

**Evidence.** `src/DeChimex.Persistence/Queries/OperationsQueries.cs` `OrderQueries.SearchAsync`: it `.Include(o => o.Customer).Include(o => o.Items).Include(o => o.Services)` for every list row, pages in SQL, **then** applies `filter.Type` in memory over the already-paged `mapped` list. With a `Type` filter, `total` counts unfiltered rows and a 20-row page can render fewer than 20 (filtered out after the fact); every list view loads full line-item graphs it never displays.

**Why it matters.** Wrong totals/pagination are visible staff-facing defects as soon as anyone filters by type; the over-fetch makes the heaviest admin table heavier than necessary.

**Suggested approach.** Compute `Type` from `Items.Any()/Services.Any()` inside the SQL `WHERE` (`hasProducts/hasServices` mirrors the domain's `OrderType` derivation), and project list rows to `OrderSummaryDto` directly (`Select`) with only the customer name needed — no `Include` of Items/Services. The read-side already does exactly this style elsewhere (`CatalogQueries` projections).

**Risk of leaving as-is:** incorrect admin lists; degrading query as order volume grows.

**Complexity:** **Low**.

---

### AUD-008 — `AllowedActionsFor` drifts from the domain: "Refund" offered where the domain refuses

**Category:** Faulted Implementation · **Area:** Order lifecycle UI/domain consistency · **Importance:** 5/10 · **Current:** 4/10 · **Suggested:** 9/10 · **Status: Confirmed**

**Evidence.** `AdminOrdersController.AllowedActionsFor` (`src/DeChimex.Host/Controllers/Mvc/Admin/AdminOrdersController.cs`, bottom): `"AwaitingProcurement" => new[] { "StartProduction", "Refund", "Cancel" }`. But `Order.EnsureCanRefund()` (`Domain/Entities/Order.cs`) allows refund only from `Paid, Completed, Delivered, InProduction, Ready` — **not** `AwaitingProcurement`. A staff member following the UI gets a business-rule error. The duplication is explicitly two hand-maintained lists (the comment even says the UI list is "derived from the domain guard rules", but it isn't, here).

**Suggested approach.** Derive the allowed actions from the domain (e.g. a static `Order.AllowedTransitions(Status)` next to `EnsureCanRefund`/transition guards) and have the controller consume it — one source of truth for both UI and enforcement.

**Complexity:** **Low**.

---

### AUD-009 — Admin receipts list loads the entire table into memory

**Category:** Faulted Implementation · **Area:** Admin receipts / performance · **Importance:** 5/10 · **Current:** 3/10 · **Suggested:** 9/10 · **Status: Confirmed**

**Evidence.** `AdminReceiptsController.Index`: `var all = await _receipts.ListAsync(...)` (repository base = `AsNoTracking().ToListAsync()` — **every receipt ever**) then filters, counts and pages with LINQ-to-Objects. Every other admin list uses `Skip/Take` SQL paging. This is O(total receipts) per page view and will degrade linearly forever.

**Suggested approach.** Push `Contains`-filters, ordering, count and paging into SQL exactly like `AdminInvoicesController.Index` does (same shape, same page).

**Complexity:** **Low**.

---

### AUD-010 — Registration endpoint confirms whether an email is already registered

**Category:** Faulted Implementation · **Area:** Auth / user enumeration · **Importance:** 5/10 · **Current:** 4/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** `RegisterHandler` throws `ConflictException("auth.email.exists", "An account with this email already exists.")` and `AccountController.Register` renders it in the form. Meanwhile the *other* auth surfaces are deliberately anti-enumeration (`ForgotPasswordHandler` "Always returns success to avoid user enumeration"; `ResendVerification` "always reports success") — so the protection is inconsistent within the same controller.

**Suggested approach.** On email-exists, take the anti-enumeration branch: pretend success ("we emailed you a verification link / you can sign in") and optionally email the existing account a "someone tried to register with your email" notice. Same UX, no oracle.

**Complexity:** **Low**.

---

### AUD-011 — Payment row is persisted only after a successful gateway initialize

**Category:** Faulted Implementation · **Area:** Payments / auditability · **Importance:** 5/10 · **Current:** 5/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** `InitializePaymentHandler` calls `gateway.InitializeAsync(...)` **before** `payments.AddAsync` + `SaveChangesAsync`. If the save fails after Paystack accepted the transaction, a live gateway transaction exists with **no local Payment row**; when the customer later completes it, the webhook hits `"Webhook for unknown payment reference"` (`HandleWebhook`) and is dropped — a real payment with no order confirmation and no trace except a warning log. Repeated Pay clicks also leave accumulating `Initialized` attempts (never cancelled/abandoned; `PaymentStatus.Abandoned` is documented as never set).

**Suggested approach.** Persist the `Payment` row (Initialized) **before** the gateway call, then update with the authorization URL — the same persist-then-act ordering the verification path already uses. Optionally let `DevMaintenance.ReverifyAsync`-style tooling close stale attempts.

**Complexity:** **Low–Medium**.

---

### AUD-012 — Manual confirmation can mark a fee-inclusive Paystack attempt as paid with a different method

**Category:** Faulted Implementation · **Area:** Payments (staff counter) · **Importance:** 4/10 · **Current:** 4/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** `ConfirmOrderPaymentHandler` picks `existing = payments.Where(Status is Pending or Initialized).FirstOrDefault()` **regardless of method** and marks it succeeded, keeping the existing row's `Amount` (which for a Paystack attempt includes the customer-borne service fee) even when the staff-declared method is Cash/BankTransfer for the bare order total. The payment record then says "Cash, ₦(order+fee)". New-attempt creation uses the order total correctly; only the reuse path mislabels.

**Suggested approach.** When the confirmed method differs from the existing attempt's method (or when the existing attempt is a Paystack attempt being confirmed manually), create a fresh payment for `order.GrandTotal` with the declared method instead of reusing the gateway attempt.

**Complexity:** **Low**.

---

### AUD-013 — Design-review "previews" are placeholder file copies (including PDFs)

**Category:** Faulted Implementation · **Area:** Files / design review UX · **Importance:** 4/10 · **Current:** 3/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** `LocalFileStorageService.GenerateImagePreview` is an honest-to-goodness `File.Copy(source, preview, overwrite: true)` with comments "a real implementation would use ImageSharp"; `GeneratePdfPreview` does the same ("For now, create a simple placeholder") while its doc comment promises "first page as PNG". So a 40 MB PSD-style TIFF "preview" is a full-size duplicate on disk, and a PDF "preview" is the PDF itself stored twice under `preview_*.pdf`. Disk doubles per design upload and the UI shows a "preview" that is the original.

**Suggested approach.** Either implement real thumbnails (ImageSharp is already on the radar; PDF: first page via QuestPDF is not a renderer — use a light Pdfium wrapper) or drop preview generation for now and render file-type icons for non-inline types — the DB already models `HasPreview` so the UI degrades cleanly.

**Complexity:** **Low** (drop) / **Medium** (real thumbnails).

---

### AUD-014 — Production stock-out can silently under-deduct against reservations

**Category:** Faulted Implementation · **Area:** Inventory integrity · **Importance:** 4/10 · **Current:** 5/10 · **Suggested:** 7/10 · **Status: Confirmed** (behaviour), **Potential** (impact depends on stock churn)

**Evidence.** `UpdateOrderStatusHandler.DeductReservedStockAsync` deducts `Math.Min(line.Quantity, inventory.QuantityOnHand)`. Payment-time reservation (`Inventory.Reserve`) reserved against *available* then; if stock was adjusted down, sold elsewhere, or another order's production deducted first, the reservation can exceed on-hand — the deduction quietly takes less, releases reservation (`StockOut` caps the reservation release), and the order proceeds under-stocked with no `attention` row. The shortage event fires only at *reservation* time, not at deduction time.

**Suggested approach.** Deduct against the reservation: `deduct = Math.Min(line.Quantity, Math.Min(inventory.QuantityReserved, QuantityOnHand))`, and when `deduct < line.Quantity` raise an `InventoryShortageDetectedEvent`/business-activity `attention` row so procurement re-triggers. Small, localised change inside the existing aggregate.

**Complexity:** **Low**.

---

### AUD-015 — Two parallel design-approval mechanisms with different semantics

**Category:** Architectural Weakness · **Area:** Design workflow · **Importance:** 6/10 · **Current:** 5/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** One order carries **both** `OrderDesignProof` (staff-only, versioned, superseding, *drives* `OrderStatus` and gates payment: `AwaitingDesignApproval → RequirementsConfirmed`) **and** `OrderDesignReview` (customer or staff, independent non-superseding history, own Pending/Approved/Rejected state, *drives nothing* — purely advisory bell notifications; the code comments themselves stress the contrast, e.g. `OrderDesignReview` doc comment and the `Enums.DesignReviewStatus` doc). Approving a design **review** does not satisfy the proof requirement and vice versa; staff must know which of the two artifacts actually authorises production. Separate permissions, separate timeline entries, separate notification types exist for each.

**Why it matters.** "What exactly was approved?" (audit question §7) has two competing answers on the same order. The proof workflow already answers every requirement of §7 correctly on its own; the review workflow adds a second, weaker truth that can lull staff into starting production discussions from an approved *review* while the *proof* is still pending.

**Suggested approach.** Keep one authoritative approval concept (the versioned proof) and demote design reviews to what their own docs say they are — customer-supplied *reference files* (which `OrderFile` already models) — or, if both are to stay, make the workspace UI unambiguously label which artifact gates production and surface "proof approved? yes/no" as a first-class status chip.

**Risk of leaving as-is:** staff error on production go-ahead; audit ambiguity.

**Complexity:** **Medium**.

---

### AUD-016 — Revenue date-basis differs per reporting surface (figures never reconcile)

**Category:** Architectural Weakness · **Area:** Financial reporting consistency · **Importance:** 6/10 · **Current:** 4/10 · **Suggested:** 9/10 · **Status: Confirmed**

**Evidence.** For the same period, revenue is summed on **two different economic dates**: `DashboardQueries.GetKpisAsync/GetRevenueTrendAsync` filter `o.PaidAtUtc >= monthStart` (cash-basis), while `AdminAnalyticsController` and `AdminReportsController.Sales` filter `o.CreatedDateUtc` (order-basis) plus the (broken, AUD-001) status predicate. A custom order created 30 Sep and paid 2 Oct lands in different months on different screens. The Developer-Portal fee ledger, by contrast, is scrupulous about using each row's *economic date* ("the fee's economic date: paid date for collected rows…", `ServiceFeeReportService.LedgerQueryAsync`) — the standard exists in the codebase.

**Suggested approach.** Standardise every revenue report on cash basis (`PaidAtUtc`, with a defined policy for manual payments) via one shared read-side query (`IRevenueQueries`) used by Dashboard, Analytics and Reports — the same consolidation the fee report already embodies.

**Complexity:** **Medium** (touches three surfaces + tests).

---

### AUD-017 — No forwarded-headers strategy: client IP, rate limiting and audit keyed to the proxy

**Category:** Architectural Weakness · **Area:** Deployment / security · **Importance:** 6/10 · **Current:** 4/10 · **Suggested:** 9/10 · **Status: Likely** (depends on production topology, which the repo does not yet fix)

**Evidence.** No `UseForwardedHeaders` anywhere (`grep ForwardedHeaders src/` → none). Everything that keys on `RemoteIpAddress` — the global fixed-window rate limiter partition (`AddRateLimitingConfigured`), login lockout IP recording (`LoginCommand` IpAddress), `AuditLog.IpAddress` via `CurrentUser.IpAddress`, `RecordAuthFailure` — will see the reverse-proxy/LB address for **all** clients once deployed behind one. Behind a proxy, one abusive client can exhaust the single shared 600/min window for everyone (a site-wide self-DoS), and audit IPs become worthless.

**Suggested approach.** Configure `ForwardedHeadersOptions` (X-Forwarded-For/Proto) with a strict `KnownProxies` allowlist at the top of the pipeline, and set the auth cookie `SecurePolicy.Always` in the same change (see AUD-023). Document the required proxy headers in `docs/08-deployment.md`.

**Complexity:** **Low**.

---

### AUD-018 — Cancelling a paid order keeps the money with no guided refund path

**Category:** Architectural Weakness · **Area:** Order lifecycle / financial workflow · **Importance:** 6/10 · **Current:** 4/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** `Order.Cancel` is legal from `Paid`, `InProduction`, `Ready` etc. (`CancelRefundOrder.cs`, `AllowedActionsFor` lists both Cancel and Refund as independent buttons). Cancelling a paid order: order → `Cancelled`, customer gets the `OrderStatusUpdate` "Cancelled" email — and **nothing** tells staff (or the customer) that the succeeded payment is still held. The refund is a separate action with separate permission (`orders.refund`), easy to miss; there is no outstanding-refund queue, and `ConfirmPaidOrderAsync`-recorded spending stays until someone manually refunds. A paid-then-cancelled order with no refund is a legitimate business case (store credit), but the system doesn't record which of the two happened.

**Suggested approach.** On `CancelOrderCommand`, when a Succeeded payment exists, require an explicit outcome: prompt "refund now (gateway) / mark resolved (store credit) / refund later", write it to the timeline + business activity, and add an "orders cancelled with unrefunded payments" tile to the admin dashboard (the query is trivial: `Status == Cancelled && Payments.Any(Succeeded)`).

**Complexity:** **Medium**.

---

### AUD-019 — Single-instance assumptions across workers, storage and presence

**Category:** Architectural Weakness · **Area:** Deployment / scalability · **Importance:** 5/10 · **Current:** 6/10 · **Suggested:** 8/10 · **Status: Potential** (only when scaling out; today's single-box deployment is fine)

**Evidence.** Everything long-running is in-process: `BackgroundEmailQueue` (Task.Run), `MarketingBroadcastBackgroundService`, `BusinessActivityDispatcher`, `MetricsFlushService`, `DiagnosticsWriterService/RetentionService`, `GalleryVideoReEncodeBackgroundService`. Files live on local disk (`wwwroot/uploads`, `StorageOptions.LocalRootPath`) and are served by `UseStaticFiles` from the same box. `OrderPresenceTracker` is a singleton dictionary (its own docs: "after a restart every side simply shows offline"). With two instances: duplicated marketing/email sends (marketing is DB-guarded at-most-once per recipient, the email queue is not), split-brain presence, and uploads that exist on one node only.

**Suggested approach.** No rewrite needed now — document the single-instance constraint in the deployment docs and name the three swap points that already exist behind interfaces (`IFileStorageService` "so Cloudinary/Azure Blob can replace it later", `IEmailQueue` "Swap this for a durable queue later", SignalR backplane) so a future scale-out is a configuration change, not a discovery exercise. Add `Affinity`-free safety: make the marketing worker safe via its existing DB row claims (already true) and note the email outbox (AUD-003) as the fix that also enables multi-instance.

**Complexity:** **Low** (documentation) / **High** (actual scale-out, not recommended now).

---

### AUD-020 — Live provider credentials committed to a public repository — CRITICAL

**Category:** Security · **Severity: Critical (High)** · **Area:** Secrets management · **Importance:** 9/10 · **Status: Confirmed**

**Evidence (locations only, values not reproduced).** `src/DeChimex.Host/appsettings.json` and `appsettings.Development.json` contain, committed and pushed to the public `github.com/BakareAhmad/HeroRestore` repo: a **live Brevo API key** (`xkeysib-…`, verified in use by the final E2E run's `/health` "brevo" check against the real Brevo API and by `BrevoEmailService` real sends), Paystack `sk_test_`/`pk_test_` keys, the SQL Server **`sa` password** in both connection strings, `Seed:OwnerPassword`, and (Development) `Seed:DeveloperPassword`. The commit history (single "initial" commit + successors) means rotation is required, not just deletion. This is a deliberate owner directive ("test keys intentionally committed… MUST ship in every deliverable zip" — `tools/make-deliverable-zip.sh`), but the *Brevo* key is a production sending key for the business's sender identity, and the repo is world-readable.

**Why it matters.** Anyone can currently send authenticated email **as DE-CHIMEX PRINT HUB** via Brevo (phishing from a trusted sender, burning the domain's reputation, exhausting Brevo credits — the code itself documents credit exhaustion as failure mode 402). The `sa` password is reusable infrastructure credential hygiene debt. The Paystack keys are test-mode (lower impact).

**Suggested approach (when implementation is authorised).** Rotate the Brevo key and the DB password immediately; move secrets to environment variables / user-secrets / a managed secret store and keep `appsettings*.json` as blank templates; keep the *deliverable-zip* behaviour via an explicit owner-provided env step rather than committed values; add a pre-commit secret scan. Audit Brevo's activity log for third-party sends since exposure.

**Complexity:** **Low** (rotation + config move).

---

### AUD-021 — Uploads are served publicly and unauthenticated; inline SVG is a same-origin script vector

**Category:** Security · **Severity: High** · **Area:** File access / authorization · **Importance:** 8/10 · **Current:** 3/10 · **Suggested:** 9/10 · **Status: Confirmed**

**Evidence.** All uploads are written under `wwwroot/uploads/{category}/{guid}.ext` (`LocalFileStorageService`, `StorageOptions.LocalRootPath = "wwwroot/uploads"`) and served by the global `app.UseStaticFiles(...)` with `Cache-Control: public,max-age=604800` (`Program.cs`). There is **no authorization of any kind** on these URLs: private order attachments (customer briefs), chat media, design proofs and design-review files (the app's own docs call these confidential — "customer design files and private order media") are readable by anyone who obtains the URL; the 7-day immutable cache also means a leaked/deleted file keeps serving. Crucially, `.svg` is allowed for design reviews (`StorageOptions.AllowedDesignReviewExtensions`), and navigating to an SVG renders it as a **document** where its inline scripts execute under the site's CSP — which permits `'unsafe-inline'` (AUD-022) — i.e., stored XSS on the application origin with access to the session cookie's authority (HttpOnly mitigates theft, not in-page actions; anti-forgery tokens are readable by same-origin script from any form). Upload *write* validation is good (extension allow-list per category, size cap, GUID names, traversal-guarded delete) — the gap is read-side authorization and content-type handling.

**Suggested approach.** Keep public static serving only for `productimage`/`gallerymedia`; move order-bound categories (`orderattachment`, `chatmedia`, `designproof`, `designreview`) outside `wwwroot` and serve them through a small authorized controller action (owner-of-order or `orders.view` staff) with `Content-Disposition: attachment` for non-image types; explicitly disallow `.svg` (or serve it with `Content-Security-Policy: sandbox` / as a download). URLs stored in the DB already exist as paths — the change is confined to storage root + one controller.

**Why better:** authorization enforced where the data's confidentiality actually lives; kills the stored-XSS vector class; cache headers become per-response.

**Risk of leaving as-is:** confidential customer artwork/briefs are one leaked URL away from public; a malicious design upload can act as the victim in the app.

**Complexity:** **Medium**.

---

### AUD-022 — CSP permits `unsafe-inline` and `unsafe-eval` for scripts

**Category:** Security · **Severity: Medium** · **Area:** Headers / XSS defense-in-depth · **Importance:** 5/10 · **Current:** 4/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** `SecurityHeadersMiddleware`: `script-src 'self' 'unsafe-inline' 'unsafe-eval' https://js.paystack.co https://cdn.tailwindcss.com`. `'unsafe-eval'` is not needed by SignalR or the app's vanilla JS; `cdn.tailwindcss.com` is the *playground* JIT script (the repo ships compiled `tailwind.build.css`), so the CDN entry is likely dead weight — both materially weaken XSS containment (and directly amplify AUD-021's SVG vector).

**Suggested approach.** Remove `'unsafe-eval'` and the Tailwind CDN origin; move inline scripts to nonce-based policy (`'nonce-…'` via the existing middleware) incrementally, starting with layouts that embed JSON islands. Verify Paystack inline checkout still functions (it loads from its own origin via `frame-src`/redirect, typically not needing app-page eval).

**Complexity:** **Medium** (nonce plumbing) / **Low** (drop eval+CDN first).

---

### AUD-023 — Auth cookie `SecurePolicy.SameAsRequest`

**Category:** Security · **Severity: Medium** · **Area:** Session/cookies · **Importance:** 6/10 · **Current:** 5/10 · **Suggested:** 9/10 · **Status: Likely** (manifests when TLS terminates at a proxy without proto forwarding — see AUD-017)

**Evidence.** `AddCookie(... Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest ...)` (`ServiceCollectionExtensions.cs`). With `App.FrontendBaseUrl = https://…` production intent this is safe only if the app itself sees HTTPS; behind a TLS-terminating proxy without `X-Forwarded-Proto` handling, the app sees HTTP and issues the **session cookie without `Secure`** over what the user believes is an HTTPS site. `Program.cs` sets HSTS and `UseHttpsRedirection` only in non-Development, which helps direct deployments but not proxy topologies.

**Suggested approach.** `CookieSecurePolicy.Always` in non-Development (the site is HTTPS-only by design), together with the forwarded-headers fix in AUD-017.

**Complexity:** **Low**.

---

### AUD-024 — No anti-automation on public forms and OTP flows

**Category:** Security · **Severity: Medium** · **Area:** Abuse resistance · **Importance:** 5/10 · **Current:** 4/10 · **Suggested:** 7/10 · **Status: Confirmed**

**Evidence.** `grep -ri captcha src/` → nothing. The only abuse control is the global 600 req/min/IP fixed window. Public unauthenticated surfaces that trigger cost or email: `/Account/Register` (sends a 6-digit OTP email each submission — OTP-bombing a victim's inbox costs the business Brevo credits), `ResendVerification` (same), `ForgotPassword`, `/contact` (staff-inbox flooding; each message is persisted + emailed), and login (lockout exists after repeated failures on a *known* account — good — but the limiter's per-IP window is the only brute-force damper and is shared/blind behind proxies per AUD-017).

**Suggested approach.** No third-party CAPTCHA needed at this scale to start: tighten focused rate limits per sensitive route (`RequireRateLimiting` partitioned policies already exist as a mechanism in the framework wiring), e.g. register/resend/forgot at 3–5/hour/IP, contact at 5/hour/IP, and add a honeypot field on register/contact. Escalate to a CAPTCHA only if abuse appears.

**Complexity:** **Low**.

---

### AUD-025 — PBKDF2-SHA256 at 100k iterations is below current OWASP guidance

**Category:** Security · **Severity: Low** · **Area:** Password storage · **Importance:** 4/10 · **Current:** 6/10 · **Suggested:** 8/10 · **Status: Confirmed**

**Evidence.** `PasswordHasher`: PBKDF2-HMAC-SHA256, 100,000 iterations, 16-byte salt, 32-byte key, fixed-time compare, self-describing format ("so parameters can evolve"). The format already supports evolution — but `Verify` re-derives with the *stored* iteration count and nothing upgrades weak hashes on successful login. OWASP's 2023+ guidance for PBKDF2-HMAC-SHA256 is 600,000 iterations.

**Suggested approach.** Raise `Iterations` to 600k and add the classic rehash-on-login: on successful `Verify` where the stored iteration count is below the current constant, re-`Hash` and persist. The self-describing format makes this a two-line-per-login change with zero migrations.

**Complexity:** **Low**.

---

### AUD-026 — Missing: viewer for raw audit rows (Old/New value diffs)

**Category:** Missing Feature · **Area:** Audit / staff & owner tooling · **Importance:** 5/10

1. **What is missing?** `AuditableSoftDeleteInterceptor` writes full `AuditLogs` rows — action, entity, id, actor, IP, **OldValues/NewValues JSON** — for every aggregate create/update/delete, atomically with the change. But the only audit UI (`/admin/activity` via `ActivityQueries`) filters `a.ActivityType != null`, i.e. exclusively the *business-activity projection*. Rows with only generic `Action/EntityName/OldValues/NewValues` (e.g. an `Order` update whose diff fields are in `NewValues`) have **no reader anywhere** — the data is written and never surfaced.
2. **Why relevant?** §12/§13 of the audit brief: an Owner must be able to answer *who changed what, from what, to what, when*. The hardest part (capture) is already done; only a read screen is missing.
3. **Integrates with:** existing `AuditLogs` table + indexes (`AuditLogConfiguration`), the admin shell, the activity filter conventions.
4. **Roles:** Owner/Manager (`activity.view`-holding roles).
5. **Problem solved:** forensic answers after incidents (price changed by whom? refund note edited?) without DB access.
6. **Importance:** 5/10.
7. **Dependencies:** none new; a paged query + one admin view + `Permissions.Activity.View` policy.
8. **Direction:** add `/admin/audit/raw` (paged, filter by entity/id/actor/date) reusing `ActivityQueries` patterns; link from business-activity rows via `EntityName+EntityId`.
9. **Priority rationale:** moderate — high value for incidents, no current daily pain.

---

### AUD-027 — Missing: database backup / disaster-recovery runbook

**Category:** Missing Feature · **Area:** Business continuity · **Importance:** 5/10

1. **What is missing?** `PRODUCTION_CHECKLIST.md` itself still lists, unchecked: "Set up database backup schedule" and "Test disaster recovery procedures". The repo contains no backup script, retention policy, or restore-tested runbook; migration history exists but is not a backup.
2. **Why relevant?** This database *is* the business: orders, payments with fee snapshots, receipts, chat history, audit trail. SQL Server is the production store (`DatabaseSeeder.MigrateAsync` at startup). Losing it loses financial records the fee ledger explicitly depends on ("The Payments table IS the ledger" — `ServiceFeeReportService`).
3. **Integrates with:** existing `docs/08-deployment.md`, health checks (a "last backup age" health/portal tile would slot into the existing Dev-portal Jobs/Health surfaces).
4. **Roles:** Owner / operator.
5. **Problem solved:** recoverability of financial and customer data; rollback after a bad deploy or a bad admin action (soft-delete covers app-level deletes, not corruption).
6. **Importance:** 5/10 (pre-production blocker rather than a code defect).
7. **Dependencies:** hosting environment (where SQL Server runs).
8. **Direction:** SQL Server native backup (full+differential+log) to off-box storage, documented RPO/RTO, a scripted restore drill, and a checklist update. No application code required.
9. **Priority rationale:** must land before real money flows; cheap to do, catastrophic to skip.

---

### AUD-028 — Missing: export (CSV/PDF) for admin business reports

**Category:** Missing Feature · **Area:** Reporting / staff experience · **Importance:** 3/10

**What/why:** `/admin/reports/*` and `/admin/analytics` are HTML-only; the owner routinely needs sales/product/service figures in spreadsheets for accounting (the Developer Portal already exports the service-fee ledger to CSV — `evidence/final-service-fee-export.csv` — so the pattern and appetite exist). Integrates with existing report queries and QuestPDF (already used for invoice PDFs). Benefits Owner/Manager. Solves: month-end accounting without copy-paste. Importance 3/10. Direction: a `?format=csv` switch on the existing report actions; complexity Low.

---

### AUD-029 — Missing: installable PWA / service worker

**Category:** Missing Feature · **Area:** Mobile / PWA · **Importance:** 4/10

**Evidence.** `wwwroot/site.webmanifest` exists but declares `"display": "browser"` (i.e., deliberately *not* an installable app), there is no service worker, and no offline/connection-state UX. For a mobile-first customer application (the storefront and portal are responsive and the workspace chat is realtime), the current state is a coherent choice, but two low-cost gaps stand out: (a) iOS/Android "Add to Home Screen" gives a plain bookmark; (b) a dropped connection during order chat gives only the browser's default error surface. A minimal service worker (network-first with a friendly offline fallback page for navigations, **never** caching API/hub responses) plus `display: standalone` would make the app installable and give the offline state a designed response, without cache-invalidation risk. Importance 4/10 — meaningful polish for the primary mobile audience, not urgent. Complexity Low–Medium. (Interacts with AUD-021: the SW must never cache `/uploads/*`.)

---

### AUD-030 — Missing: SignalR and storage health checks

**Category:** Missing Feature · **Area:** Observability · **Importance:** 3/10

**Evidence.** `/health` covers database, Paystack, Brevo (`Health/`); the Dev portal adds jobs and request metrics. Not covered: (a) SignalR — hub negotiation failures (sticky-session/proxy misconfig at deployment) would be invisible; a trivial self-check hub or `IHubContext` send heartbeat on `/health`-tagged check closes it; (b) storage — uploads are on local disk; a disk-space/writability check (e.g., write-probe the uploads root) prevents the classic "orders keep working but uploads silently fail" state. Both are one small class each, both fit the existing `IHealthCheck` + dev-tag pattern. Importance 3/10 each — cheap completeness, not urgency.

---

### AUD-031 — Sales-inbox bucket counts materialise all orders

**Category:** Optimization · **Area:** Sales inbox / performance · **Importance:** 4/10 · **Current:** 6/10 · **Suggested:** 8/10

**Evidence.** `SalesInboxQueries.GetBucketCountsAsync` loads `Select(o => new { Status, ConversationState, AssignedSalesUserId })` for **every order in the database** and counts in memory (a comment-friendly but O(n) approach), on every `/admin/sales` page view alongside `GetInboxAsync`. Correct and fast today; grows linearly with lifetime order count.

**Suggested approach.** One grouped `GROUP BY (Status, ConversationState, AssignedSalesUserId)` SQL aggregate returning counts per bucket — same shape the code already computes, pushed into the database. Complexity **Low**.

---

### AUD-032 — Duplicate service registrations in the Persistence DI module

**Category:** Optimization · **Area:** DI hygiene · **Importance:** 2/10 · **Current:** 7/10 · **Suggested:** 9/10

**Evidence.** `src/DeChimex.Persistence/DependencyInjection.cs` lines 106–109 register `ICategoryOptionsQueries → CategoryOptionsQueries` and `IOrderWorkspaceQueries → OrderWorkspaceQueries` **twice each** (copy-paste remnant; last registration wins, so behaviour is unchanged). Harmless but confusing and a template for drift.

**Suggested approach.** Delete the duplicate lines. Complexity **Low**.

---

### AUD-033 — `Payment.RawResponse` stores full gateway/webhook bodies indefinitely

**Category:** Optimization · **Area:** Data governance / PII minimisation · **Importance:** 4/10 · **Current:** 5/10 · **Suggested:** 8/10

**Evidence.** `Payment.RawResponse` (nvarchar(max)) receives the complete verify response body and, from the webhook path, the **entire raw `charge.success` payload** (`HandleWebhook` passes `command.RawBody`) — which embeds customer email/phone, channel, authorization metadata. Diagnostics tables have retention (`DiagnosticsRetention`), but Payments are business records kept forever, so this PII copy has no lifecycle. The codebase elsewhere is disciplined about redaction (`SensitiveDataRedactor`, masked recipients in `IntegrationCall`s, "Never stores the payload (it contains customer data)" on the webhook tracker) — this column is the exception.

**Suggested approach.** Keep the column (audit value) but store a redacted/summarised payload (strip customer/contact/authorization blocks), or apply the same retention policy as diagnostics after N months while preserving the financial snapshot fields (which already exist as first-class columns — `PaystackFeeActual`, amounts, references — so nothing financial is lost).

**Complexity:** **Low–Medium**.

---

### AUD-034 — `Customer.LifetimeSpending` has no reconciliation path if it drifts

**Category:** Optimization · **Area:** Financial integrity · **Importance:** 4/10 · **Current:** 6/10 · **Suggested:** 8/10

**Evidence.** Spending is denormalised: `RecordSpending(order.GrandTotal)` on confirmation (`PaymentConfirmationService`) and `ReverseSpending` on refund (`RefundOrderHandler`), "runs in the same SaveChanges" — good design, and the write paths are exactly paired. But because AUD-002 can double-fire the increment, and because any manual data fix bypasses both, nothing can detect or repair drift: the customer report (`/admin/reports/customers`) would silently disagree with the order history. The codebase already contains the reconciliation *pattern* — `ServiceFeeReportService.LedgerAsync` recomputes from rows and compares to the headline ("They must match to the kobo").

**Suggested approach.** Add a Dev-portal/owner maintenance action (the safe-maintenance surface already exists: `DevMaintenanceController`) that recomputes `LifetimeSpending` from qualifying orders' `GrandTotal` and reports deltas before writing; optionally show drift on the customer report. This also gives AUD-002's historical cleanup a tool.

**Complexity:** **Low–Medium**.

---

## 4. Missing Important Features (consolidated)

| ID | Feature | Why DeChimexPrints needs it | Integrates with | Roles | Importance |
|---|---|---|---|---|---|
| AUD-026 | Raw audit-trail viewer (Old/New diffs) | Capture exists atomically but no human surface; forensic answers currently need DB access | `AuditLogs`, admin shell, activity filters | Owner/Manager | 5/10 |
| AUD-027 | Backup/DR runbook | The DB is the financial ledger (payments, fee snapshots, receipts); checklist item still open; no rollback path for corruption | `docs/08-deployment.md`, Dev-portal health/jobs, SQL Server | Owner/operator | 5/10 |
| AUD-029 | Installable PWA (SW + standalone) | Mobile-first customers; chat offline states; manifest already present but `display: browser` | `site.webmanifest`, customer layout, chat UX | Customers | 4/10 |
| AUD-028 | Report export CSV/PDF | Month-end accounting; fee ledger already exports CSV, reports don't | `/admin/reports`, `/admin/analytics`, QuestPDF | Owner/Manager | 3/10 |
| AUD-030 | SignalR + storage health checks | Hub/proxy misconfig and disk exhaustion are invisible today | `IHealthCheck`, dev-tag pattern | Operator/Developer | 3/10 |

Deliberately **not** listed as missing (checked and rejected to avoid false positives): guest checkout (ordering is owner-mandated to the signed-in portal), admin password reset for staff (self-service forgot-password covers staff accounts on the shared `Users` table), order reopen after completion (refund path exists by design), multi-currency (NGN-only is deliberate), message brokers/Redis (no workload evidence), microservices/GraphQL (contrary to the architecture's strengths).

---

## 5. Faulted / Problematic Implementations (consolidated)

| ID | Current behaviour | Evidence (file) | Underlying problem | Current → Suggested | Recommended alternative | Affected workflows |
|---|---|---|---|---|---|---|
| AUD-001 | Unpaid custom requests inflate revenue/AOV/top-products | AdminAnalyticsController, AdminReportsController | `Status >= Paid` vs additive enum | 3 → 9 | Shared `IsPaidLike` + `PaidAtUtc` basis | Analytics, all reports |
| AUD-002 | Concurrent confirm double-counts spending / duplicate emails & receipts | PaymentConfirmationService, HandleWebhook, VerifyPayment, SalesConfigurations (no rowversion; Receipts lacks OrderId unique) | Check-then-act idempotency | 3 → 9 | Transaction + rowversion/lock + filtered unique indexes | Payment, refunds, portal |
| AUD-003 | Queued emails lost on restart; no retry | EmailQueue.cs | In-memory fire-and-forget | 4 → 9 | Durable outbox drained by a worker (marketing-worker pattern) | Signup, orders, payments, design |
| AUD-004 | Duplicate `DCX-/INV-/RCT-` numbers under concurrency → 500 | ReferenceGenerator.cs; unique indexes | Read-count-then-increment | 3 → 9 | Retry-on-unique-violation or sequence table | Checkout, staff orders, receipts |
| AUD-005 | Refund "done" at acceptance; failed settlements invisible | PaymentGatewayRefundCoordinator, PaystackWebhookParser | `refund.*` events unhandled | 4 → 8 | Parse refund events; RefundStatus + attention alerts | Refunds, fee ledger |
| AUD-006 | Disabled/demoted staff keep access up to cookie life | ServiceCollectionExtensions, UserManagementFeature | Claims frozen at sign-in | 3 → 9 | Per-request activity/stamp check (DevAccess pattern) | All staff areas |
| AUD-007 | Type filter breaks totals/paging; over-fetch | OperationsQueries.OrderQueries.SearchAsync | Post-page in-memory filter | 4 → 9 | SQL-translated Type predicate + projection | Admin orders |
| AUD-008 | UI offers Refund on AwaitingProcurement; domain rejects | AdminOrdersController.AllowedActionsFor; Order.EnsureCanRefund | Duplicated transition tables | 4 → 9 | Domain-owned `AllowedTransitions` | Admin order details |
| AUD-009 | Whole receipts table loaded per page view | AdminReceiptsController.Index | In-memory search/paging | 3 → 9 | SQL paging like AdminInvoices | Admin receipts |
| AUD-010 | Register reveals existing accounts | RegisterHandler, AccountController.Register | Enumeration oracle | 4 → 8 | Anti-enumeration success path | Signup |
| AUD-011 | Gateway transaction can exist with no local Payment row | InitializePaymentHandler | Persist-after-external-call | 5 → 8 | Persist-then-initialize | Payments |
| AUD-012 | Cash confirm on a Paystack attempt records fee-inclusive amount | ConfirmOrderPaymentHandler | Method-blind reuse | 4 → 8 | Fresh attempt on method mismatch | Counter payments |
| AUD-013 | "Previews" are file copies (PDFs duplicated as previews) | LocalFileStorageService | Placeholder implementation | 3 → 8 | Real thumbnails or no-preview + icons | Design review |
| AUD-014 | Production deduction may lag reservation silently | UpdateOrderStatusHandler.DeductReservedStockAsync | min(onHand) without reservation reconciliation | 5 → 7 | Reserve-aware deduction + shortage alert | Fulfilment, procurement |

---

## 6. Existing Features With Better Available Approaches

| Feature | Current approach | Suggested approach | Current /10 | Suggested /10 | Why |
|---|---|---|---|---|---|
| Revenue reporting (Dashboard/Analytics/Reports) | Three independent inline recomputations of order totals with two different date bases and one broken status predicate (AUD-001/016) | One shared read-side `IRevenueQueries` (cash basis, `IsPaidLike`) consumed by all three | 3 | 9 | Correctness + guaranteed reconciliation; mirrors the fee-ledger precedent |
| Allowed admin order actions | Hand-maintained string list per status | Derived from domain transition guards | 4 | 9 | Single source of truth; UI can never offer an illegal action (AUD-008) |
| Email delivery | In-process Task.Run queue | Durable outbox + worker | 4 | 9 | Crash-safe, retryable, observable; callers unchanged (AUD-003) |
| Receipts admin list | `ListAsync` + LINQ-to-Objects | Same SQL paging as the invoices screen | 3 | 9 | O(page) instead of O(table) (AUD-009) |
| Bucket counts on sales inbox | Load + count in memory | `GROUP BY` aggregate | 6 | 8 | Linear → constant; trivial change (AUD-031) |
| Preview generation for design files | `File.Copy` placeholders | Real thumbnails or explicit no-preview UI | 3 | 8 | Disk halves; UI stops pretending (AUD-013) |
| Login lockout & audit IP fidelity | Per-IP keyed on `RemoteIpAddress` | Forwarded-headers-aware `RemoteIpAddress` + per-route limits | 4 | 9 | Correct behind any proxy (AUD-017/024) |
| Payment attempt persistence | Save after gateway init | Save before gateway init | 5 | 8 | No orphaned gateway transactions (AUD-011) |

---

## 7. Cross-System Architectural Findings

1. **Order × Payment (AUD-002, AUD-011):** the order lifecycle and the payment ledger meet in `PaymentConfirmationService`; idempotency there is enforced in application code only. The single most valuable hardening in the system is making that junction atomic (DB-enforced single-succeeded-per-order). Everything else in the payment chain (references, fee snapshots, amount checks) is already correct.
2. **Order × Reporting (AUD-001, AUD-016):** adding the additive custom statuses changed the meaning of a numeric status comparison in reports. Cross-system lesson encoded in the codebase's own comments ("values 10+ are additive") never reached the analytics predicates. A shared status predicate fixes the class, not the instance.
3. **File Storage × Authorization (AUD-021):** the domain carefully guards *metadata* access (workspace, order details, redacted customer projections) while the *payload bytes* of the same confidential artifacts are public. Authorization stops at the URL boundary.
4. **Authentication × Admin UI × Notifications (AUD-006):** claims frozen in the cookie drive `[Authorize]` policies, `ViewBag.Can*` button visibility, and SignalR audience groups alike — one staleness defect manifests coherently in all three, which is also why it is easy to miss: everything agrees with everything else, just with the past.
5. **Payment × Refund × Accounting (AUD-005, AUD-018, AUD-034):** the refund path touches payments, invoice, receipt, order status, and lifetime spending in one save — well-designed — but its premise (provider settlement) is unverified, and cancelled-without-refund orders leave the money state decoupled from the order state with no reconciliation surface.
6. **Design workflow × Payment gating (AUD-015):** proof approval genuinely gates payment (`PendingPayment → AwaitingDesignApproval` hides the pay button); design-review approval gates nothing — the same word "approved" means two different levels of authority on one order.
7. **Audit × Business actions (strength, with one gap):** `BusinessActivityCapture` projects business-meaningful events at the save boundary with `SourceKey` idempotency — an exemplary pattern; the gap is only that the *raw* diff rows (AUD-026) have no consumer.

---

## 8. Duplicate Logic & Source-of-Truth Risks

| Duplicate | Where | Safe today? | Consolidate? |
|---|---|---|---|
| "Paid" predicate for revenue | Dashboard (whitelist) vs Analytics/Reports (range — broken) | **No — active defect** (AUD-001) | Yes — one `OrderStatus.IsPaidLike` |
| Revenue date basis | `PaidAtUtc` (Dashboard) vs `CreatedDateUtc` (Analytics/Reports) | No — figures disagree by design (AUD-016) | Yes — one cash-basis query |
| Allowed status actions | Domain guards vs `AllowedActionsFor` string table | Mostly; one confirmed drift (AUD-008) | Yes — derive from domain |
| Active sales workload | `Order.IsActiveSalesWork` vs `SalesInboxQueries.ActiveSalesStatuses` (comment: "keep the two in sync") | Yes (currently identical) | Optional — expose the domain set |
| Order totals | `Order.GrandTotal` (computed) vs raw SQL re-expression `OrderRawTotal` (Dashboard) vs Analytics inline | Yes — floor applied consistently; value-object owned types make divergence unlikely | Acceptable; a shared expression tree would remove the third copy |
| Document totals | Order (live) vs Invoice/Receipt/Payment snapshots | **Yes — snapshots are deliberate and immutable**; do *not* consolidate | No |
| `LifetimeSpending` | Denormalised on Customer vs order/payment history | Yes on write paths; no drift detection (AUD-034) | Keep + add reconcile tool |
| Author identity for staff messages | `OrderMessage.AuthorName` denormalised, rewritten on username change (`RenameStaffAuthorAsync`) | Yes — deliberate with migration path | No |
| Design approval | Proofs vs DesignReviews (AUD-015) | Two truths coexist on one order | Yes — clarify or merge |
| DI registrations | `CategoryOptionsQueries`/`OrderWorkspaceQueries` ×2 (AUD-032) | Cosmetic | Yes |

---

## 9. Security Findings

**Critical / High**
- **AUD-020 (9/10):** live Brevo key + Paystack test keys + `sa` password + seed passwords committed to a public repo (locations in §3; values withheld). Rotate + move to secret store.
- **AUD-021 (8/10):** unauthenticated public serving of confidential uploads; inline-SVG stored-XSS vector amplified by CSP (AUD-022). Authorized delivery endpoint for order-bound categories; drop/sandbox SVG.

**Medium**
- **AUD-023 (6/10):** cookie `SecurePolicy.SameAsRequest` — force `Secure` in non-Development (with AUD-017).
- **AUD-017 (6/10):** no forwarded-headers strategy — rate limiting, lockout and audit IPs break behind a proxy.
- **AUD-006 (7/10):** stale authorization claims — delayed revocation of access (also listed as a faulted implementation; it is both).
- **AUD-022 (5/10):** CSP `unsafe-inline`/`unsafe-eval` + likely-dead Tailwind CDN origin.
- **AUD-024 (5/10):** no anti-automation on register/contact/OTP flows.
- **AUD-010 (5/10):** registration enumeration oracle (inconsistent with the app's own anti-enumeration elsewhere).

**Low**
- **AUD-025 (4/10):** PBKDF2 100k iterations (rehash-on-login upgrade path is trivial).
- **AUD-033 (4/10):** unbounded PII retention in `Payment.RawResponse`.

**Explicitly verified as good (do not "fix"):** webhook signature check before any state change (HMAC-SHA512, fixed-time compare); `[ValidateAntiForgeryToken]` on every mutating MVC action observed; HttpOnly+SameSite=Lax cookie; IDOR scoping on portal controllers (order/invoice/payment/design-review all ownership-checked, e.g. `PortalOrdersController`, `VerifyPaymentCommand.ExpectedCustomerId`, `OrderChatHub.IsAuthorizedAsync`); 401/403 JSON contract for `/api`; permission-claim model with a dynamic policy provider; brute-force lockout on login; sensitive-data redaction in logs and dev diagnostics; generic error pages without stack traces.

---

## 10. Financial & Business-Integrity Findings

| ID | Finding | Importance |
|---|---|---|
| AUD-001 | Unpaid custom requests counted as revenue in Analytics/Reports | 8/10 |
| AUD-002 | Concurrent confirmation double-counts spending; duplicate receipts possible | 8/10 |
| AUD-005 | Refund reversals committed before provider settlement; failed refunds invisible | 7/10 |
| AUD-016 | Revenue date-basis inconsistency makes surfaces unreconcilable | 6/10 |
| AUD-018 | Cancelled paid orders retain money without a guided refund outcome | 6/10 |
| AUD-012 | Manual confirm can mislabel method/amount on gateway attempts | 4/10 |
| AUD-034 | `LifetimeSpending` has no drift detection/reconciliation | 4/10 |
| AUD-033 | Gateway payload PII retained indefinitely inside the financial ledger | 4/10 |

**Verified as sound (evidence):** fee snapshots per attempt (`Payment.OrderAmount/ServiceFeePercent/CustomerBorneCharge/PaystackFeeExpected/PaystackFeeActual`), amount **and** currency equality checks on both verify and webhook, idempotent `MarkSucceeded`, shared confirmation pipeline for all three confirmation paths, two-step service-fee change with reviewed-value concurrency + audit, future-only fee application ("Applies to future transactions"), full-order-only refunds with per-payment checkpointing, quote invoices adopting order totals via `AdoptForOrder` from the order's own snapshot, invoice `OrderId` unique index, price-snapshot order lines (client can send only quantities), review purchase-verification whitelist.

---

## 11. Workflow Failure Matrix

| # | Workflow | Failure scenario | Current behaviour | Expected behaviour | Finding | Importance |
|---|---|---|---|---|---|---|
| 1 | Payment | Duplicate callback: webhook + browser return verify concurrently | Race: double spending/emails, possible duplicate receipt | Exactly-once confirmation | AUD-002 | 8 |
| 2 | Payment | Gateway down during verify | Payment stays Initialized, retryable; honest UI copy | Same (already correct) | — (strength) | — |
| 3 | Payment | Save fails after gateway initialize | Orphaned gateway transaction; webhook later "unknown reference" | Local attempt persisted first | AUD-011 | 5 |
| 4 | Payment | Two Pay clicks / retried payments | Multiple Initialized attempts accumulate | Fine functionally; stale attempts untidy | AUD-011 note | 4 |
| 5 | Payment | Cash confirm on Paystack attempt | Fee-inclusive amount recorded as Cash | Method-correct amount | AUD-012 | 4 |
| 6 | Refund | Paystack accepts then settlement fails | Order/Receipt/Spending already reversed; no alert, no retry path | Refund-status reconciliation + attention alert | AUD-005 | 7 |
| 7 | Refund | Refund retry after partial coordinator success | Checkpointed per payment; `EnsureCanRefund` blocks order already Refunded — accepted refunds not resent | Same (already correct) | — (strength) | — |
| 8 | Order | Cancel a paid order | Money retained silently; no refund prompt/queue | Guided outcome + outstanding-refund visibility | AUD-018 | 6 |
| 9 | Order | Staff clicks Refund on AwaitingProcurement | UI offers it; domain throws business-rule error | UI never offers it | AUD-008 | 5 |
| 10 | Design | Customer approves proof v1 after v2 uploaded | Superseded proof cannot be approved (throws) | Same (already correct) | — (strength) | — |
| 11 | Design | Staff uploads new proof on a PendingPayment order | Reopens AwaitingDesignApproval; pay button hides until re-approval | Same (already correct) | — (strength) | — |
| 12 | Design | Customer disconnect mid-approval | State persisted; reconnect refetches `/workspace/state`; SignalR best-effort | Same (already correct) | — (strength) | — |
| 13 | Chat | SignalR reconnect after downtime | Client refetches state; missed pushes irrelevant; delivery backfills on open | Same (already correct) | — (strength) | — |
| 14 | Chat | Duplicate read beacon / unknown ids | Idempotent, own-messages excluded | Same (already correct) | — (strength) | — |
| 15 | Sales | Two staff finalize the same order concurrently | Last-writer-wins pricing (no concurrency token) | Conflict detection or second-save rejection | AUD-002 (same mechanism class) | 5 |
| 16 | Sales | Reassignment while customer typing | Assignment independent; conversation state machine no-ops when resolved | Same (already correct) | — (strength) | — |
| 17 | Catalogue | Product deleted while in a cart | Cart line renders "Product / ₦0"; checkout fails with TempData error | Proactive line removal or clear message | AUD-007 adjacent (UX) | 4 |
| 18 | Catalogue | Price changes mid-checkout | Cart shows live price; order snapshots at creation; client cannot send prices | Same (already correct) | — (strength) | — |
| 19 | Catalogue | Category deactivated | Products hidden from storefront grid+nav consistently (`ActiveOnly` joins category) | Same (already correct) | — (strength) | — |
| 20 | Auth | Disabled staff member keeps working | Cookie valid up to 8h+ (sliding) | Immediate revocation | AUD-006 | 7 |
| 21 | Auth | Unauthorized order/chat/file access | Portal + hub + metadata all ownership-checked | Same for bytes too | AUD-021 | 8 |
| 22 | Upload | Invalid type/size upload | Rejected with per-category allow-list + 10 MB cap | Same (already correct) | — (strength) | — |
| 23 | Upload | Preview generation fails | Original kept; upload succeeds (best-effort) | Same (already correct, but preview is a copy anyway) | AUD-013 | 4 |
| 24 | Notifications | SignalR push fails | Persisted first; push best-effort; dispatcher retries activity rows | Same (already correct) | — (strength) | — |
| 25 | Email | Brevo outage / app restart | Transactional emails lost silently (no retry/persistence) | Durable outbox with retry | AUD-003 | 7 |
| 26 | Email | Duplicate event handlers | MediatR handlers fire once per published event; event publication after save is in-process only | Same (already correct); race covered by AUD-002 | — | — |
| 27 | Mobile | Offline / flaky connection during checkout or chat | Browser default error; workspace state refetch on reconnect handles chat | Designed offline state | AUD-029 | 4 |
| 28 | Ops | DB corruption / bad deploy | No backup/restore runbook (checklist open) | Tested restore path | AUD-027 | 5 |
| 29 | Ops | Behind reverse proxy | Shared rate-limit bucket, useless audit IPs, possible insecure cookie | Forwarded headers + Secure cookie | AUD-017/023 | 6 |

---

## 12. Existing Strengths (preserve these; do not refactor)

1. **Architecture.** Clean six-project layering, CQRS with MediatR pipeline behaviors (validation/logging/performance), domain events dispatched **after** a successful save (`UnitOfWork`), aggregate repositories with careful change-tracker handling, value objects (Money/Email/Phone/Slug/Username) with correct EF conversions.
2. **One order lifecycle.** Direct and custom flows share a single `Order` aggregate discriminated by `OrderKind` — no duplicated status machine; transitions are guarded in the domain; `OrderStatusPresentation` derives customer-facing labels.
3. **Payment ledger.** Per-attempt Payment rows with unique references, fee snapshots, dual amount+currency verification (verify + webhook), idempotent success marking, one shared confirmation pipeline, per-payment refund checkpointing, and a Dev-portal fee ledger that **independently re-reconciles** headline totals against row sums "to the kobo".
4. **Service-fee governance.** Two-step preview/confirm change flow with reviewed-value optimistic check, future-only application, and automatic old→new audit capture — a model pattern for configuration changes.
5. **Audit at the save boundary.** `AuditableSoftDeleteInterceptor` + `BusinessActivityCapture` write audit and business-activity rows atomically with the change, with `SourceKey` idempotency, an allowlisted (non-secret) diff snapshot, and IP/actor capture.
6. **Observability.** Correlation-ID middleware, Serilog sink → technical events with fingerprinting and dedup, request-metric buckets by route template, integration-call ledger with masked recipients, job monitor for every background service, `/health` with database/paystack/brevo checks, safe Dev-portal maintenance actions (each POST + anti-forgery + confirm + audit event).
7. **Error handling & resilience.** Global exception middleware with typed status mapping, styled browser error pages everywhere (including rate-limit 429 with Retry-After and re-executed 404s), blank-page safety net, `Response.HasStarted` re-throw discipline.
8. **Realtime design.** Persist-then-push, order-scoped group authorization in `OrderChatHub`, in-memory presence with honest last-seen semantics, batched read beacons, reconnect resync via state endpoints, `RealtimeMonitorHubFilter` feeding the dev portal.
9. **Marketing broadcasts.** At-most-once per recipient via unique index, consent re-check at send time, per-batch commits, retry-failed affordance, HTML sanitizer, and a default-off master switch.
10. **Testing.** 887 tests: unit coverage of calculators/templates/gateway parsing, and integration coverage of auth flows, RBAC checklists, payment lifecycle, service fees, custom-order and design-review journeys, sales assignment, marketing, notifications scoping, security headers, rate limiting, plus large Playwright E2E suites — uncommon depth for an application of this size.
11. **Frontend discipline.** Server-rendered Razor with progressive enhancement (chat works without JS), no client-authoritative prices anywhere, responsive storefront with genuine alpha-composited hero art and viewport-tested CSS.

---

## 13. Recommended Priority Groups

**Immediate attention (importance ≥ 8)**
- AUD-020 — rotate and externalise the committed credentials (public repo exposure of a live email key).
- AUD-002 — make payment confirmation atomic (rowversion/lock + filtered unique indexes; add a concurrent-confirm regression test).
- AUD-001 — fix the revenue "paid" predicate on Analytics/Reports (shared `IsPaidLike` + cash basis).
- AUD-021 — authorized delivery for confidential uploads; remove/sandbox SVG.

**High priority (7)**
- AUD-006 — fresh authorization semantics (immediate disable/permission revocation).
- AUD-003 — durable email outbox with retry.
- AUD-005 — refund settlement events + reconciliation alerts.
- AUD-004 — collision-proof reference generation (retry-on-conflict minimum).

**Medium priority (5–6)**
- AUD-016, AUD-007, AUD-017, AUD-018, AUD-023, AUD-015, AUD-008, AUD-009, AUD-010, AUD-011, AUD-022, AUD-024, AUD-026, AUD-027.

**Low priority (≤ 4)**
- AUD-012, AUD-013, AUD-014, AUD-019, AUD-025, AUD-028, AUD-029, AUD-030, AUD-031, AUD-032, AUD-033, AUD-034.

---

## 14. Audit Quality Check (§48 confirmation)

- ✅ No application files, configuration, database, packages, migrations, tests, or features were created, modified, or fixed. The only write of this task is this report (`docs/COMPREHENSIVE_AUDIT_REPORT.md`).
- ✅ Every finding cites repository evidence (file/method). Confirmed vs Likely vs Potential labelled. Repository-wide searches were performed before declaring anything missing (e.g. captcha, ForwardedHeaders, Dockerfile, receipt indexes, refund-event parsing, `MarkAbandoned` usage, duplicate DI lines).
- ✅ Ratings are calibrated: only one 9/10, three 8/10; no severity inflation; deliberate design decisions (NGN-only, portal-only ordering, quote invoices, full refunds, snapshot documents, in-process scale) are recorded as strengths/constraints rather than faults.
- ✅ Cross-system interactions, workflow edge cases, financial correctness, and security were audited explicitly; existing strengths are documented to prevent unnecessary refactoring.
