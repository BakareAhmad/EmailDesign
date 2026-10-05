# Direct Checkout — Implementation & Evidence Report

**Task:** 4th task — "Direct Checkout" (per `DIRECT_CHECKOUT_EVALUATION.md` §17 pre-implementation evaluation)
**Date:** 2026-09-23 · **Status:** COMPLETE — 660/660 tests · 60/60 E2E checks · 21/21 evidence shots
**Scope guard:** This report covers Direct Checkout only. `PAYSTACK-PAYMENTS-AUDIT.md` (task 3) is untouched.

---

## 1. What shipped

Two customer journeys with one coherent lifecycle:

- **Direct path** — browse/filter catalog → eligible product (**Add to cart**) → cart → checkout
  (amounts + customer-borne Paystack fee, "You will pay") → order placed → **Pay with Paystack**
  → gateway → verified/paid → production statuses → fulfilment → verified-purchase reviews →
  retry after decline / cancel / refund, with coherent lifetime spending.
- **Custom path** — custom product (IsCustomizable or category ordering options) →
  "Order / Customize" request → persisted workspace (assigned sales, conversation) →
  admin finalize (requirements & pricing) → customer pays → production; plus the guards
  that keep custom products **off** the direct path (no Add to cart; rogue `POST /cart/add` refused 4xx, no cart line).

## 2. Locked decisions (implemented exactly)

| ID | Decision |
|----|----------|
| G1 | Eligibility is **derived**, never stored twice: `DirectCheckout = !(IsCustomizable \|\| CategoryHasActiveOptions)`; persisted `OrderKind {Direct, Custom}` (string col `Kind`, nvarchar(20), default `'Custom'` + backfill — migration `20260923173005_AddOrderKind`). |
| G2 | Checkout flow creates `Kind=Direct`; custom request/quote/staff-creates → `Kind=Custom`. |
| G3 | Reviews = verified purchase gate (Paid…Delivered) auth-checked first; 3-branch copy (form / locked "Only customers who have purchased this product can review it." / signed-out); review is per-product. |
| G4 | Badges: **"Direct purchase"** / **"Custom order"** (admin details "Ordering", workspace subtitle Kind-aware). |
| G5 | Direct-order audit wording: `"Direct product order created"` / `"Direct order payment received"` (BusinessActivityCapture) — visible in `/admin/activity`. |
| G6 | Design cards (`#proofCard`, `#designReviewCard`) hidden for Direct orders with 0 items; workspace stays available (conversation/notes/files/timeline). **No "Buy Now"** was added (explicitly out of scope). |
| G7 | Status presentation invariant enforced in UI: internal enum names are **never shown verbatim** — `OrderStatusPresentation.CustomerLabel(...)` everywhere customer/admin-facing (Complete banner, portal details badge, my-orders list, admin details badge, admin customer orders). 5 integration assertions realigned from raw enum names to the presentation labels ("Payment Required", "In Production"). |

## 3. Implementation surface

- **Domain/Persistence:** `OrderKind` enum + `Order.Kind` + migration `20260923173005_AddOrderKind` (backfills existing rows to `'Custom'`); eligibility helper on product/category aggregates; `BusinessActivityCapture` direct-order wording.
- **Application:** checkout create sets `Kind=Direct`; custom submit/quote/finalize/staff-create set `Kind=Custom`; review gating queries (verified purchase, per product, one review/customer); cart guard rejects custom/optioned products server-side (`AddCartItemCommand`).
- **Host/Views:** checkout Complete page ("You will pay", Paystack fee line, Pay with Paystack, retry-after-decline); PortalShop buy actions + microcopy ("No quote or design approval needed."); StoreProducts public page review UI (4-branch); workspace subtitle + design-card hiding; admin badges; cart guard; customer-labeled statuses (§2 G7).
- **Tests:** `DirectCheckoutTests` (10/10) + suite growth to **660** (350 unit + 310 integration).

## 4. Test results

`final-test-log.txt` (full `dotnet test`, SDK 9.0.318):

```
Passed!  - Failed: 0, Passed: 350, Skipped: 0, Total: 350  - DeChimex.UnitTests.dll
Passed!  - Failed: 0, Passed: 310, Skipped: 0, Total: 310  - DeChimex.IntegrationTests.dll
```

**660/660 green.** Build: 0 errors / 20 pre-existing warnings.

## 5. E2E evidence (final clean run)

Run: `python3 e2e/direct-checkout/workflow.py direct-0923` against a freshly seeded InMemory app
(:5119) with the Brevo stub (:9999) and Paystack stub (:9090) — full log `final-direct-checkout-e2e-log.txt`:

```
===== RESULT: 60 passed, 0 failed =====
```

21 full-page screenshots in `evidence/` (captured at 1280px width, full-page):

| # | Shot | Proves |
|---|------|--------|
| 01 | `01-catalog-filter.png` (1280×1026) | public catalog search + category filter, results caption with proper curly quotes |
| 02 | `02-direct-product-buy-actions.png` (720) | direct product: Add to cart + wishlist + "No quote or design approval needed." |
| 03 | `03-custom-product-actions.png` (720) | custom product: "Order / Customize", Customizable badge, NO Add to cart |
| 04 | `04-cart-guard-rejects-custom.png` (720) | after rogue `POST /cart/add` for custom product: cart stays empty (4xx in log) |
| 05 | `05-checkout-amounts.png` (941) | checkout amounts: subtotal/delivery/total + Paystack fee + **"You will pay"** |
| 06 | `06-direct-order-placed.png` (856) | Complete page: order placed, **"Status: Payment Required"** (presentation label), Pay with Paystack |
| 07 | `07-direct-paid.png` (720) | return page "Payment successful" after stub + webhook confirmation |
| 08 | `08-direct-workspace-no-design-steps.png` (788) | direct workspace: subtitle **"Direct purchase ·"**, design cards hidden, conversation/notes/files/timeline |
| 09 | `09-direct-admin-badge.png` (984) | admin order details: "Direct purchase" badge, Payment Succeeded, invoice+receipt |
| 10 | `10-direct-activity-log.png` (3374) | `/admin/activity` feed: "Direct product order created", "Direct order payment received", "Payment initiated", "Order status changed" |
| 11 | `11-direct-lifetime-spending.png` (720) | admin customer: "Lifetime spending ₦9,500.00" after order A |
| 12 | `12-review-unlocked-after-purchase.png` (1646) | verified purchaser sees "Write a review" form (Rating/Title/Review/Submit) |
| 13 | `13-review-locked-unpurchased.png` (1339) | non-purchaser sees "Only customers who have purchased this product can review it." |
| 14 | `14-direct-payment-failed.png` (720) | declined payment: "Payment failed… No charge was recorded… Try again" |
| 15 | `15-direct-retry-paid.png` (720) | retry on the SAME order pays: portal badge "In Production" |
| 16 | `16-direct-cancelled.png` (720) | admin cancels direct order: "Cancelled", actions gone |
| 17 | `17-direct-refunded.png` (795) | refund recorded: "Refunded", invoice/receipt marked refunded, "gateway acceptance ≠ settlement" copy |
| 18 | `18-custom-workspace-assigned.png` (1163) | custom workspace: "Order Received", sales specialist assigned, Conversation, personalisation answer "Black and gold…" |
| 19 | `19-custom-admin-badge.png` (720) | admin sees "Custom order" badge on the custom order |
| 20 | `20-custom-paid.png` (1061) | custom order paid after finalize: workspace "In Production", path intact |
| 21 | `21-my-orders-coherent.png` (720) | my orders: all four coherent — In Production / Cancelled / Refunded / In Production with ₦9,500 & ₦12,000 |

**Screenshot verification (done before this report):** every shot was pixel-verified via the image
viewer; the two surfaces fixed during verification (catalog caption, Complete banner, workspace
subtitle, status badges) were re-verified against **live page HTML** (customer session: subtitle
`Direct purchase` present / `Custom order` absent; details badge `In Production`) and one
shot was pixel-compared against a live re-render at identical framing — **99.97% identity**
(only the unread-notification badge differs). File names were taken directly by the capture helper
(`shot(p, name)` writes `evidence/<name>` verbatim) and the 21 outputs match the §18 matrix names.

## 6. Verification fixes found during evidence review (all shipped + re-tested)

1. `StoreProducts/Index.cshtml` — search caption double-escaped HTML entities → real curly quotes.
2. `Checkout/Complete.cshtml` — "Status: PendingPayment" enum leak → **"Payment Required"** (`CustomerLabel`).
3. `PortalOrders/Workspace.cshtml` — static "Custom order ·" subtitle on direct workspaces → Kind-aware **"Direct purchase ·"**.
4. `PortalOrders/Details.cshtml`, `PortalOrders/Index.cshtml`, `AdminOrders/Details.cshtml`, `AdminCustomers/Details.cshtml` — raw `@Status` badges → `CustomerLabel` (invariant: internal statuses never shown verbatim).
5. `QuoteInvoiceReceiptTests` (3 assertions) + `WalkInOrderTests` (2 assertions) realigned to the presentation labels.

## 7. Re-run recipe

```bash
# stubs
python3 e2e/design-review/brevo_stub.py        # 127.0.0.1:9999
python3 e2e/payments/paystack_stub.py          # 127.0.0.1:9090
# app (from DeChimexPrints/)
export DOTNET_ROOT=/var/tmp/dotnet
export Database__UseInMemory=true ASPNETCORE_URLS=http://0.0.0.0:5119
export Brevo__ApiBaseUrl=http://127.0.0.1:9999 Paystack__ApiBaseUrl=http://127.0.0.1:9090
dotnet run --project src/DeChimex.Host/DeChimex.Host.csproj --no-launch-profile
# E2E + tests
python3 e2e/direct-checkout/workflow.py <tag>      # 60 checks, 21 shots -> evidence/
dotnet test --nologo                                # 660
```

Helper layers reused (proven): `e2e/major-categories/workflow.py` (create_category/create_product/options/order)
and `e2e/design-review/ws_designreview_e2e.py` (owner_login / register_customer + Brevo OTP capture),
payments-style majors loader in `e2e/direct-checkout/workflow.py`.

## 8. Notes

- E2E owner login: `owner@dechimexprinthub.com` (helper rotates the seeded password on first login);
  customers register via Brevo OTP capture (`dr-{TAG}-{suffix}@example.com`).
- Webhook HMAC: `x-paystack-signature` = HMAC-SHA512 hex of the raw body with `Paystack:SecretKey`
  from appsettings (key never printed); endpoint `POST /api/v1/payments/webhook/paystack`.
- The Paystack stub `/checkout/{ref}` exposes anchor controls (`/simulate/{ref}/success|failed|abandon`);
  the workflow drives those plus the real app flow (`POST /checkout/{id}/pay` → redirect → callback verify).
- Notifications/emails/OrderStatusPresentation semantics unchanged from the payments task (reuse map).
