# Service Fee — Pre-Implementation Architecture Evaluation

**Task:** configurable Service Fee = Paystack transaction fee × (Y / 100)
**Date:** 2026-09-23 · Status: APPROVED DESIGN (audit produced before implementation, per brief §1/§17)
**Sources verified today:** paystack.com/pricing + Paystack API reference (verify-transaction response carries the authoritative `fees` field in kobo).

---

## 1. Where payment fees currently originate

- `IPaymentChargeCalculator` (`Application/Common/Interfaces/Services.cs`) with its only
  implementation `PaystackChargeCalculator` (`Infrastructure/Payments/PaystackChargeCalculator.cs`).
  Owner directive 2026-09-08: the business order value must never absorb the gateway fee — the
  customer-borne Paystack charge is added on top of the order amount (checkout shows
  "Paystack processing fee (added to your payment)" and "You will pay").
- Master switch `Paystack:CustomerBorneCharge` (appsettings; documented caveat: turn it OFF if the
  Paystack dashboard "Pass fees to customers" toggle is ever enabled, to avoid double charging).
- Display sites: `Checkout/Index.cshtml`, `Checkout/Complete.cshtml`, `PortalOrders/Workspace.cshtml`
  ("Includes a ₦X card-processing fee (Paystack)"), `PortalOrders/Details.cshtml`.

## 2. How Paystack fees are obtained

- **Before the charge (what we can know):** computed locally from Paystack's published NGN local
  pricing — 1.5% + ₦100 per transaction, ₦100 waived for charges under ₦2,500, total fee capped at
  ₦2,000. Re-verified against paystack.com/pricing today: still current. International cards are
  3.9% + ₦100 (no cap); terminal/transfer rates differ. Paystack exposes **no fee-calculation
  endpoint**; the fee must be embedded in the initialized amount, so a schedule-based calculation is
  the only pre-charge source ("Do not assume the fee can simply be calculated locally" — it is the
  only option pre-charge, but it is NOT the whole story).
- **After the charge (authoritative):** the verify-transaction response includes `fees` (kobo) —
  the actual fee Paystack charged for that transaction (varies by channel: local vs international
  card). The app currently checks `AmountPaid` + currency only and **discards `fees`**. We will
  capture it as the transaction-level actual fee.
- Payment channel is chosen by the customer inside Paystack's checkout, so the pre-charge quote uses
  the local-card schedule (the store's primary instrument and the existing calculator's assumption).

## 3. Can the payment model store the snapshot? — YES, additively

`Payment` is already a **per-attempt** aggregate (one row per gateway attempt; unique `Reference`
enforces idempotent verification; states Initialized → Succeeded/Failed → Refunded; stores `Amount`
= customer payable incl. charge, `CustomerBorneCharge` = fee portion, `PaidAtUtc`, `GatewayReference`,
`RawResponse`, `RefundReference`, `RefundedAtUtc`). Brief §5's list maps as follows; only the
**bold** columns are new (nullable/defaulted, additive migration, no rewrite of history):

| Brief §5 item | Storage |
|---|---|
| Order amount | **`OrderAmount`** (new) |
| Paystack fee | **`PaystackFeeExpected`** (new, schedule) + **`PaystackFeeActual`** (new, from verify `fees`) |
| Service-fee percentage applied | **`ServiceFeePercent`** (new) |
| Service-fee amount | `CustomerBorneCharge` (existing — under this feature it IS the service-fee amount; doc-comment updated) |
| Customer-facing payable amount | `Amount` (existing) |
| Total paid | `Amount` at success (existing) |
| Payment status | `Status` (existing) |

No new entity or table is introduced (brief §17: use the existing architecture where it is cleaner).

## 4. Settings infrastructure — EXTEND, do not replace

Existing: `ApplicationSetting` key/value entity (Key, Value, Description) + `IApplicationSettingRepository`
+ `CacheKeys.SettingsAll` 15-minute cache invalidated on save + `/admin/settings` MVC page
(GET `settings.view`, POST `settings.manage`) + seeded defaults pattern (`delivery.default_fee`).

Plan: add key **`service_fee.percent`** (seeded `"100"`). The generic all-keys settings form is
unsuitable for a financially significant change (no confirmation, no per-field validation), so the
service fee gets a dedicated **two-step change flow** on `AdminSettingsController` (same controller,
same policy attributes — not a parallel system): `GET /admin/settings` shows the current value with a
dedicated form; `POST /admin/settings/service-fee` with `step=preview|confirm`.

## 5. Service fee ↔ Paystack fee — the circularity (brief §7), resolved

**Finding: case 2.** The service fee becomes part of the single amount submitted to Paystack, so
Paystack's fee is computed on (Order + Service Fee). This is exactly the circularity the brief warns
about — and the codebase already solved it for the 100% case via the gross-up formula in
`PaystackChargeCalculator` ("final = (price + flat) / (1 − percent)"). The clean solution is to
generalize that closed form.

With order amount `O`, service-fee percentage `y = Y/100`, Paystack rate `r = 0.015`, flat `c`
(₦100, waived when the charge < ₦2,500), fee cap `K = ₦2,000`, and total charged `T = O + S`:

- `S = y × fee(T)` and `T = O + S`  ⇒  **`T = (O + y·c) / (1 − y·r)`** in the uncapped regime
  (`c = 0` while `T < ₦2,500`, else `c = 100`), and **`T = O + y·K`** in the capped regime
  (valid when `r·T + c ≥ K`).
- Pick the regime-consistent solution (the regimes partition T-monotonically; exactly one solution
  is self-consistent), then round `T` **up to the kobo** (existing convention keeps the net safe).
- `S = T − O` equals `y × fee(T)` to the kobo. There is no self-reference: the fixed point is solved
  analytically, never iteratively.
- Sanity limits: `y = 1` reduces to today's behaviour exactly; `y = 0` gives `T = O` (S = ₦0.00).

This also fixes a latent boundary gap in the current capped-branch test (`fee(O)` slightly below the
cap while `fee(T)` exceeds it mis-prices by up to ~₦30); the generalized solver closes it.

**Exposure note (brief §6):** the customer sees `Order subtotal / Service fee / Total` — never the
internal provider-cost breakdown. The historical record stores both expected and actual Paystack
fees for admin/audit views.

## 6. Refunds and the service fee

- The service fee is part of the Paystack transaction amount (one charge) — it cannot be refunded
  independently. `RefundOrderHandler` already refunds the payments in full and reverses lifetime
  spending by "exactly the amount recorded at confirmation". Lifetime spending is the **order value**
  (established behaviour: ₦9,500.00 for the ₦9,746.20 charge) — the service fee never enters
  spending, so refunds need no spending change beyond what exists.
- Paystack does not return its own fee on refunds (published behaviour: "fee not refunded"); the
  refunded service-fee amount returns to the customer with the rest of the payment.

## 7. Configuration protection — permissions + confirmation + concurrency

- **Permissions (existing system only):** `Permissions.Settings.Manage` is held by **Owner and
  Manager** by the canonical role maps (verified in `Permissions.All()` + `DatabaseSeeder` role
  definitions; Sales/Inventory/Designer/Customer have no `settings.*`). The change endpoint requires
  `settings.manage`; viewing the value on the settings page requires `settings.view` (same split as
  the rest of the settings page — other staff can be granted view-only via the existing role editor
  and cannot change it).
- **Confirmation (brief §3):** two-step flow. `preview` renders the required dialog — current %,
  new %, "This will affect future transactions", "Existing orders and completed transactions will
  not be recalculated" — and only `confirm` saves. Cancelling (or navigating away) changes nothing.
- **Concurrency (brief §15):** the confirm request carries the reviewed previous value; the server
  compares it against the **database** value inside the save and rejects stale overwrites with a
  conflict message (value unchanged). The audit entry records the actual old→new pair that was
  saved. This is optimistic concurrency on the reviewed value — deterministic last-confirmed-wins.
- **Audit (brief §12):** `BusinessActivityCapture` entry "Service fee configuration changed",
  description "…changed service fee from X% to Y%. Applies to future transactions." (previous %,
  new %, actor, timestamp come with the standard capture; no secrets logged).
- **Real-time (brief §13):** the app uses SignalR push for order-workspace events only — there is no
  existing real-time configuration broadcast to integrate with, so per the brief we rely on the
  server-side current-value validation above (plus GET always rendering live values).

## 8. Edge cases discovered

1. Cap-boundary gap (§5) — closed by the regime-consistent solver.
2. ₦100 waiver threshold applies to the **charge** (T), not the order amount.
3. Channel variance: international cards cost 3.9% uncapped → the *expected* fee (local schedule)
   can differ from the *actual* fee (`fees` on verify). The customer's amount is locked at
   initialization; the record keeps both numbers. Verified payments still require
   `AmountPaid == Amount` (existing invariant untouched).
4. `y = 0` must yield exactly `T = O` (no kobo drift); `Paystack:CustomerBorneCharge = false` also
   yields `T = O` regardless of Y (safety switch semantics preserved).
5. Retries (brief §9): each attempt is its own `Payment` row with its own snapshot, priced at
   initialization from the then-current configuration (a retry is a new gateway transaction with its
   own Paystack fee). A failed/abandoned attempt records its snapshot with `Status=Failed` and never
   a success/spending entry; the service fee is included once per successful charge — impossible to
   double-charge by construction.
6. Gateway amounts are integer kobo (`ToMinorUnit`) — rounding rule: round `T` up to the kobo.
7. Historical immutability (brief §4): nothing recomputes from global config after initialization;
   invoices/receipts/admin views read the stored snapshot (`ServiceFeePercent`, `CustomerBorneCharge`).
8. Zero/negative/NaN/∞/non-numeric/over-max percentage values are rejected server-side.

## Decisions (locked)

| # | Decision |
|---|----------|
| S1 | **Formula:** `Service Fee = Paystack fee (on the charged amount) × Y/100`, solved as the generalized gross-up in §5. Not a % of the subtotal; not a delivery/platform fee. |
| S2 | **Setting:** `service_fee.percent` in `ApplicationSetting`, default **100** (preserves the 2026-09-08 owner directive and all 660 existing tests until the owner reconfigures). Range **0–100**, 2 decimal places. |
| S3 | **Maximum = 100%** — the business requirement defines the fee as "a configurable **portion** of Paystack's transaction cost": the pass-through cannot exceed the cost being passed through; >100% would be markup, which is out of scope. Minimum 0% (fee fully absorbed by the business). |
| S4 | **Customer copy:** `Order subtotal / Service fee / Total`; provider internals ("Paystack processing fee") are replaced by "Service fee" terminology on customer surfaces. |
| S5 | **Snapshot on `Payment`** per §3 table; verify-time `fees` captured as `PaystackFeeActual`. |
| S6 | **Retries re-price at initialization** from the current configuration (each gateway attempt is a new transaction with its own fee); each attempt is fully reconstructable. |
| S7 | **Refunds** return the full payment (incl. service fee); spending is order-value based and unchanged in behaviour. |
| S8 | **Change flow:** two-step confirm on `/admin/settings` + reviewed-value concurrency guard + `BusinessActivityCapture` audit; gated by existing `settings.manage` (Owner/Manager). |
