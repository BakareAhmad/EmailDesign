# Service Fee — Implementation & Evidence Report (task 5)

Date: 2026-09-23 · Run tag `sf-0923` · Suite: **724/724** (`final-test-log.txt`) · E2E: **54/54, 0 failed** (`final-service-fee-e2e-log.txt`)

## What shipped

The service fee is **Y% of the Paystack transaction fee** charged to the customer on top of the
order amount (Y = `service_fee.percent`, default **100** = unchanged legacy behaviour; master
switch `Paystack:CustomerBorneCharge` = true). Y is configurable **0–100** (2 dp) under
**Admin → Settings → Service Fee** with the brief's **two-step confirmed flow**.

Formula (`PaystackChargeCalculator`): with Paystack fee `fee(T)=1.5%·T+₦100` (flat waived below
₦2,500 charge, cap ₦2,000 local):

- **Solution:** `T = O + y·fee(T)` solved by inverse-gross-up
  `T=(O+y·100)/(1−y·0.015)` (cap zone: `T=O+y·2000`), `ServiceFee=T−O`.
- **Requested-only alternative (S1, documented in SERVICE_FEE_EVALUATION.md):**
  `S = y·fee(O)` does not solve the model — e.g. O=9500,y=100% gives S=₦242.50 but the real fee
  on the collected amount is ₦246.20 → would book an unexplained ₦3.70. The general solution
  keeps the invariant `ServiceFee = y·fee(collected)` exact at every setting.

Golden values (pinned in unit tests): y=100: 9,500→**9,746.20** (S=246.20), 125,900→127,900.00
(cap), 1,000→1,015.23 (flat waived); y=50: 9,500→**9,622.17** (S=122.17); y=25: 9,500→9,560.85
(S=60.85), 12,000→12,070.27 (S=70.27). Invariant tested across 11 orders × 12 settings:
`T=O+S` and `|S − y·fee(T)| ≤ 0.02`.

## §18 checklist

1. **Configurable 0–100** — `ServiceFeeSettings.TryParse` accepts "50%" / "50" / 2 dp, clamps
   −5→0 and 150→100; rejects >100, negatives, NaN/Infinity, >2 dp (unit + HTTP tests).
2. **Percentage of the Paystack fee** — calculator + receipts show "Service fee" as its own line;
   the provider fee is admin-facing only.
3. **New line item** — checkout shows **"Service fee (added to your payment)"** (copy kept),
   receipts show **Order total / Service fee / Amount paid**, admin payment card shows
   **Order total / Service fee (Y%) / Paystack fee (expected / charged)**.
4. **Added to what the customer pays** — "You will pay" = order + service fee (evidence 05).
5. **Shown in every customer amount context** — checkout (05), placed-order/complete (06),
   payment history "incl. ₦X service fee (Y%)" (08), receipt document (09).
6. **Admin config display + confirmation required** — settings card (01), review → "Change
   Service Fee?" dialog (02) with current/new values, "affect future transactions",
   "will not be recalculated", "Are you sure you want to continue?". Save happens ONLY on
   confirm (preview alone never persists; stale confirmation never overwrites a newer change).
7. **Customer-visible names only** — never "Paystack processing fee"; customer amounts labelled
   "You will pay"; "incl. service fee" where bundled.
8. **Changing the fee does NOT rewrite history** — historical receipts/payments/admin cards
   show the originally applied split forever (12); refunds keep the applied fee and reverse
   spending exactly (16).
9. **Refund rules** — full refund includes the fee the customer paid as one charge reversal;
   lifetime spending counts order values net of refunds (31,000 → 21,500 in evidence 16).
10. **Audit** — "Service fee configuration changed" with "from X% to Y%" and "Applies to future
    transactions" (04); payment/refund events present (17).

## Evidence index (`evidence/` — run `sf-0923`, 17 shots, ordered)

| Shot | What it proves |
|---|---|
| 01-settings-at-100.png | Service Fee card at the 100% default, confirmation-required copy on the form |
| 02-confirm-dialog-100-to-50.png | The "Change Service Fee?" dialog: 100% → 50%, future-only wording, explicit consent |
| 03-settings-at-50.png | After confirm: rate saved at 50%, current-value hint updated |
| 04-audit-service-fee-changed.png | Business activity entry "Service fee configuration changed … from 100% to 50%" |
| 05-checkout-service-fee-50.png | Checkout: "Service fee (added to your payment) ₦122.17", "You will pay ₦9,622.17" on ₦9,500 |
| 06-order-placed-payment-required.png | Complete page: Status: Payment Required + service fee row retained |
| 07-payment-successful.png | Paystack (stub) success at 50% |
| 08-payment-history-incl-fee.png | My payments: "incl. ₦122.17 service fee (50%)" + ₦9,622.17 |
| 09-receipt-fee-breakdown-50.png | Receipt: Order total ₦9,500.00 / Service fee ₦122.17 / Amount paid ₦9,622.17 |
| 10-admin-payment-snapshot-50.png | Admin payment card: Order total, Service fee (50%), Paystack fee (expected / charged) |
| 11-settings-at-25.png | Second confirmed change 50% → 25% |
| 12-receipt-immutable-after-reconfig.png | Same order's receipt still 122.17 / 9,622.17 after the rate change |
| 13-payment-failed-no-fee.png | Declined attempt is not a success and generated no receipt |
| 14-retry-paid-at-25.png | Retry on the same order priced at current config (₦60.85 @ 25%), one receipt only |
| 15-custom-paid-receipt-at-25.png | Custom-order receipt with ₦70.27 service fee (25% on ₦12,000) |
| 16-refunded-receipt-keeps-fee.png | Refunded receipt keeps the applied ₦122.17; spending reverses exactly the order value |
| 17-activity-feed-fees.png | Activity trail: payment received + refund recorded |

Task-4 Direct Checkout evidence preserved at `e2e/direct-checkout/evidence/`.

## Notable bug caught by this task's E2E

`StoreControllerBase.LoadBusinessInfoAsync` whitelists settings keys for the storefront;
`service_fee.percent` was missing, so checkout silently priced at the 100% default while the
admin page (direct DB read) showed the configured value. Fixed by whitelisting
`ServiceFeeSettings.Key` (comment in code). Integration tests had passed because the payment
commands read settings through the full dictionary path.

## Test matrix (§16) — all green (724/724)

- Unit (394): `ServiceFeeSettingsTests` (parse/format/valid+invalid theories),
  `ServiceFeeChargeCalculatorTests` (golden values + `OracleFee` invariant ±0.02 + cap/waiver
  boundaries + y=100 ≡ legacy + toggle-off ⇒ T=O).
- Integration (330): `ServiceFeeFeatureTests` — configuration: owner/manager change through the
  confirmed flow, sales staff denied (AccessDenied), 6 invalid values rejected with nothing
  saved, preview alone never saves, stale confirmation rejected, audit entry written, current
  value displayed, generic settings save cannot touch the rate. Transactions: 0/50/100% splits,
  snapshot fields on Payment (OrderAmount/ServiceFeePercent/PaystackFeeExpected/Actual via
  `FakePaymentGateway.VerifyFees`), historical immutability, failed attempt (no receipt/spend),
  retry without double charge, refund + spending reversal, custom orders.
