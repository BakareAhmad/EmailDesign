# Paystack Payments — Complete Audit Report (2026-09-23)

Scope: full lifecycle audit of the Paystack payment integration. The existing payment
architecture (server-side `IPaymentGateway`, per-attempt `Payment` records, order status
machine, receipts/invoices/spending) was preserved; this audit verified it end-to-end and
fixed what was broken or unsafe. Suite after changes: **650/650 tests green**, plus a live
E2E run of the real lifecycle in this sandbox (**62/62 checks**, `evidence/`).

---

## 1. Architecture (as found, verified, kept)

- **Gateway** `Infrastructure/Payments/PaystackGateway : IPaymentGateway` — `initialize`
  (creates hosted-checkout transaction, returns authorization URL), `verify` (server-side
  `GET /transaction/verify/{ref}`), `refund`. Keys/base URL from configuration only
  (`Paystack:*`), read at runtime; never sent to the browser.
- **Checkout** `Host/Controllers/Mvc/Store/CheckoutController` — cart → order created
  `PendingPayment` → "Complete" page shows order totals + the customer-payable amount
  (GrandTotal + configured Paystack fee, display only) → `Pay` POST → `InitializePaymentCommand`
  → 302 to hosted checkout. Server sends the expected amount (kobo) + NGN + callback
  `/checkout/return` on init and **re-validates everything server-side afterwards**.
- **Confirmation is server-side only** — returning from checkout never marks paid:
  `/checkout/return` runs `VerifyPaymentCommand` (async channels may still be `ongoing` →
  the page shows "Payment pending … you have not been marked paid yet"); the webhook
  `POST /api/v1/payments/webhook/paystack` (HMAC-SHA512 `x-paystack-signature`, fixed-time
  compare) runs `HandleWebhookCommand`. Both paths share one idempotent confirm routine:
  `MarkSucceeded` (once-only) → `RecordSpending(GrandTotal)` → receipt + invoice → customer
  + owner notifications → receipt email. The legacy manual confirm (bank-transfer evidence)
  path is untouched.
- **Payments are attempts** — each try creates a fresh `Payment` with reference
  `PAY-{timestamp}-{10 hex}`; `MarkFailed`/`MarkSucceeded` are idempotent; a failed or
  abandoned attempt keeps the order retryable (`Pay` again = new attempt, new reference,
  no duplicate order).
- **Statuses (no new statuses invented)** — order: `PendingPayment → Paid → Refunded`
  (plus existing lifecycle/production statuses); payment: `Pending / Succeeded / Failed /
  Refunded`. Provider status map (decision A): `success` → Succeeded;
  `failed|abandoned|reversed` → Failed (no throw); `pending|processing|queued|ongoing` →
  Pending. Transport/HTTP/JSON errors during verification → **Pending, never Paid**, order
  stays retryable.

## 2. Payment channels found

`Paystack:Channels` is passed through to the initialize payload. It ships **empty = omitted
= every channel enabled on the Paystack account** (per current Paystack docs the valid
values are `card, bank, apple_pay, ussd, qr, mobile_money, bank_transfer, eft, capitec_pay,
payattitude` — we do not hard-code an assumed list). The hosted checkout therefore always
offers exactly the store's currently enabled options (card, bank transfer, USSD, mobile
money, …). The Complete page copy names those and defers to "any other option Paystack
enables for this store". E2E evidence `03-paystack-checkout-options.png` shows the enabled
set on the test integration.

## 3. Problems found (and severity)

| # | Problem | Severity | Resolution |
|---|---------|----------|------------|
| P1 | CSP `form-action 'self'` blocked the cross-origin redirect to hosted checkout — payment could never start in a real browser | Critical | `SecurityHeadersMiddleware` form-action allowlist now includes the configured `Paystack:ApiBaseUrl` origin (prod: `https://checkout.paystack.com`; no weakening of other directives). Verified live in the response header |
| P2 | Verification transport/HTTP/JSON failures could leave the state machine ambiguous; risk of paid-on-client-info in edge paths | High | Such failures map to **Pending**; paid only on server-verified `success` |
| P3 | Currency (and amount) mismatch could be confirmed from a provider echo | High | Amount & currency validated server-side against the order's expected values; mismatch → `Failed` ("Amount/Currency mismatch"), never paid; covered by `PaymentLifecycleTests.Currency_mismatch_is_never_confirmed_on_verify_or_webhook` + E2E |
| P4 | Verify/webhook with foreign or unknown references reached the gateway / probed orders | Medium | Ownership-first: unknown/foreign reference → `NotFound` **before** any gateway call; E2E proves zero provider traffic |
| P5 | Refund retry could refund twice | High | `Order.EnsureCanRefund` + refusal "Only a paid order can be refunded"; E2E proves exactly one provider refund request and no second spending reversal |
| P6 | Init could start a charge on an already-paid order or on a non-positive payable | Medium | Paid order → clear rejection ("already being paid"); `customerPayable <= 0` → `Conflict`; E2E both |
| P7 | `MarkFailed` after success could demote a paid payment | High | Guard: Succeeded is terminal except `Refunded` |
| P8 | Raw/ambiguous payment messages | Medium | Distinct failed / pending / successful states with the existing notification/toast patterns; pending copy states "you have not been marked paid yet … not charged twice for checking"; no raw API errors shown |
| P9 | `AI_HANDOFF.txt` §12 claimed a `PaymentRefundedEvent` flow exists | Low | Corrected in the handoff (it does not exist; refunds are acceptance-based — see §6) |

## 4. Changes made

- `Host/Middleware/SecurityHeadersMiddleware.cs` — form-action allowlist += configured
  Paystack origin (P1).
- `Application/Features/Payments/Commands/{InitializePayment,VerifyPayment,HandleWebhook}.cs`,
  `Application/Features/Orders/Commands/CancelRefundOrder.cs`, `Domain/Entities/{Order,Payment,Customer}.cs`,
  `Contracts/Payments/PaymentDtos.cs`, `Infrastructure/Payments/PaystackGateway.cs`,
  `Infrastructure/Options/AppOptions.cs`, `Persistence/Queries/OperationsQueries.cs`,
  `Host/Controllers/Mvc/Store/CheckoutController.cs`, `Host/Views/Checkout/{Complete,Return}.cshtml`
  — decisions A–G as listed in §1–§2 (status mapping A; ownership-first B; refund model C;
  MarkFailed guard D; init rejects E; channels passthrough + copy F; webhook currency check G).
- Tests: `tests/DeChimex.IntegrationTests/Payments/PaymentLifecycleTests.cs` (+ auth checks)
  and `tests/DeChimex.UnitTests` — 650/650.
- E2E: `e2e/payments/paystack_stub.py` (mock provider: initialize/verify/refund, simulated
  hosted checkout + outcome controls + HMAC-able webhook events, full traffic log) and
  `e2e/payments/workflow.py` (the §5 matrix). Harness fix in
  `e2e/design-review/ws_designreview_e2e.py` (`brevo_capture_for` now returns the latest
  capture for an address so reruns don't verify against a stale token).

## 5. Failure scenarios tested (live E2E — `evidence/`, one clean run)

Success path (card) · declined → retryable → fresh reference → success on the **same**
order · webhook confirms with browser closed (no callback) · duplicate callbacks +
duplicate webhooks + late verification of an unused second attempt → records/spending/
receipt exactly once · forged webhook signature acked but confirms nothing · async channel
(bank transfer/USSD) pending → "not marked paid yet" → settles via webhook · abandoned
checkout stays `PendingPayment` · unknown reference and foreign-customer reference → safe
message / not found, zero gateway calls · amount mismatch → never paid · currency mismatch
webhook ignored then matching-currency webhook confirms · init on a paid order rejected ·
refund → `Refunded` + lifetime spending reversed · second refund refused + single provider
refund request · admin **Business activity** log records `payment.initiated` (per attempt),
`payment.verification failed` (declined attempt), `payment.succeeded`/`payment.received`
exactly once, and the refund (`Gateway refund request accepted`). Final arithmetic:
lifetime spending = 3 confirmed × ₦9,500 − ₦9,500 refund = ₦19,000 (screenshot `22`).

Automated tests additionally cover the same invariants at the command level (idempotent
transitions, mismatch, forged/foreign refs, retry reference freshness).

## 6. Refund boundary (decision C) — accepted, documented

Paystack refund API returns an **accepted** request (status `ongoing`/`processing`);
settlement is asynchronous and there is **no** `refund.*` webhook flow in this codebase.
The app therefore moves the order to `Refunded` and reverses lifetime spending on
**acceptance**, and says so honestly in the admin UI ("Gateway requests … were accepted;
final settlement is not confirmed here") and in the activity entry. If settlement fails on
Paystack's side, that is resolved on the Paystack dashboard; no fake precision is claimed.

## 7. What could NOT be tested here (and why)

- **Real Paystack network** (live initialize/verify/refund, 3DS/bank OTP/USSD sessions,
  hosted-checkout UX): the sandbox has no external network; a faithful local stub
  (`e2e/payments/paystack_stub.py`) implements the same REST contract, statuses and HMAC
  webhooks and the app is pointed at it via `Paystack:ApiBaseUrl` only.
- **Actual refund settlement timing/outcome** — asynchronous on Paystack's side (§6).
- **Paystack's webhook retry schedule** — duplicate/replayed deliveries were tested
  (idempotent), but their real cadence is provider-side.
- **Live Paystack fee rounding** — the fee is display-only from config here; verified that
  the server stores/charges `GrandTotal` and the payable is computed server-side.
- **Load/performance and multi-account (multi-currency account) configurations.**

## 8. Evidence inventory (`evidence/`, ordered, from the single clean final run)

01 checkout options + amounts → 02 order placed → 03 provider checkout options →
04 declined → 05 retryable → 06 fresh reference → 07 successful → 08 order paid (production
gate) → 09 invoice + receipt → 10 notification → 11 spending credited → 12 paid by webhook
without callback → 13 duplicate events idempotent → 14 pending (async channel) → 15 pending
settles → 16 abandoned → 17 unknown reference safe message → 18 foreign reference not found
→ 19 amount mismatch rejected → 20 paid order rejects new payment → 21 refund confirmed →
22 lifetime spending reversed → 23 payment events in activity log; plus
`final-payments-e2e-log.txt` (62/62 checks) and `final-test-log.txt` (650/650 tests).
Superseded screenshots/logs from earlier attempts were removed.
