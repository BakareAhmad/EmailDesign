# Direct Checkout — Architecture Evaluation & Chosen Ordering Model (2026-09-23)

Written **before implementation** per the brief (§17). Answers the ten evaluation
questions from the audit of the current code (commit `959e55f` contents).

---

## 1. How the existing Order infrastructure works

One `Order` aggregate (`Domain/Entities/Order.cs`) is the transactional core: product/
service/custom lines, totals, and a single status machine. Two entry points already exist:

- **`PlaceOrder()`** — Draft → `PendingPayment` immediately (cart/checkout path:
  `CheckoutController` → `CreateOrderCommand` → invoice issued → Paystack via
  `InitializePayment`/`VerifyPayment`/`HandleWebhook` → `MarkPaid` → fulfilment statuses).
- **`SubmitOrderRequest()`** — Draft → `AwaitingReview` (custom path: category option
  answers + files snapshotted, auto **sales assignment** (`CustomOrderCommands.cs:108`),
  conversation/design/proof children, `FinalizeOrder()` later → `PendingPayment`).

Downstream of `PendingPayment` both paths share everything: payments (Paystack),
invoices/receipts, lifetime spending, notifications/emails (domain events →
`OrderEventHandlers`), refunds (`Refund()` + spending reversal), reviews, and the
business-activity audit feed (`Persistence/Interceptors/BusinessActivityCapture.cs`).

Customer-facing statuses are already projected (`OrderStatusPresentation`), so customers
never see internal custom states ("Payment Required" → "In Production" → …).

## 2. Where the current workflow assumes Sales involvement

Only one place auto-assigns sales: `SubmitOrderRequestHandler` (custom path). The cart/
checkout path never assigns sales and never enters conversation/design states. Product
pages already auto-route (owner decision 2026-09-09, `CUSTOM_ORDER_FLOW_PLAN.md` §1:
category has active options **or** `Product.IsCustomizable` → “Order / Customize”;
otherwise → “Add to cart” → checkout). Residual sales/custom assumptions found:

- `PortalOrders/Workspace.cshtml` always renders the **Design proofs** and **Design
  Reviews** cards (with empty-states) — a direct buyer sees custom-workflow furniture
  for steps that never happened (brief §13).
- The admin order page has no way to tell a **Direct Product Order** from a custom order
  once both are `PendingPayment` (brief §12).
- Nothing enforces the routing rule **server-side**: `AddCartItemCommand` and
  `CreateOrderCommand` accept any product, so a custom-only product can be carted and
  checked out directly (bypassing requirements/pricing review).

## 3. Is an ordering-mode/type required?

**Yes — one minimal discriminator: `OrderKind { Direct = 0, Custom = 1 }` on `Order`**
(string column, same pattern as `ConversationState`), set deterministically at creation:
`Order.CreateDraft(number, customerId, kind)`. `SubmitOrderRequestHandler` → Custom;
`CreateOrderHandler` → Direct (Custom for staff quote-conversions — those were negotiated).
Statuses alone cannot carry the distinction (both paths converge on `PendingPayment`), and
deriving it from child collections misclassifies custom orders that have no attachments or
answers yet. This is the smallest architectural adjustment; no new aggregate, table or
status. (See §9 for the rejected alternatives.)

## 4. Is product-level direct-purchase configuration required?

**No.** Eligibility is derived from the existing product model (owner's standing
auto-detect rule):

> **Direct-purchase eligible ⇔ `!Product.IsCustomizable` AND the product's category has
> no active `CategoryOption`s.**

Adding a `DirectPurchaseEnabled` flag would create a third source of truth that can
disagree with `IsCustomizable`/category options. Admins already control eligibility
(today: set `IsCustomizable`, or add ordering questions to the category; the product
admin already exposes `IsCustomizable`). Documented on the admin product form help text.

## 5. How Direct Checkout reuses the existing Order aggregate

Identical to the current cart path, unchanged: product page → **Add to cart** (existing
`ShoppingCart`) → `/checkout` (fulfilment choice + address) → `CreateOrderCommand`
(server-side price snapshot from the catalog) → `PlaceOrder()` → `PendingPayment` →
existing Paystack flow. No `DirectOrder`/`ProductPurchase`/second table/parallel payment
lifecycle (§5 of the brief bans it; §24/§25 of the custom-order plan already settled the
“one coherent order lifecycle” principle). Orders land in the same admin workspace,
history, invoices, receipts and activity feed.

## 6. How it interacts with the existing status model

Direct orders only traverse: `Draft → PendingPayment → Paid → (AwaitingProcurement) →
InProduction → QualityCheck → Ready → Completed → (OutForDelivery) → Delivered`, plus
`Cancelled`/`Refunded`. The custom pre-payment states
(`AwaitingReview/InDiscussion/RequirementsConfirmed/AwaitingDesignApproval`) are never
entered — `PlaceOrder()` goes straight to `PendingPayment`. No new statuses needed;
`OrderStatusPresentation` already maps both paths to the same customer labels.

## 7. How Sales involvement is bypassed safely

It already is, structurally: auto-assignment lives only in `SubmitOrderRequestHandler`,
and `OrderPlacedEventHandler` only notifies admins. What we add: (a) server-side
eligibility gates so a custom-only product can never arrive through the direct path
(see §2), and (b) hide the design/custom-workspace furniture on direct orders. The order
chat stays available as an optional follow-up channel (existing “messaging stays possible
for follow-ups” design) — never a required step. Direct orders remain fully visible in
the admin orders list/workspace and activity feed.

## 8. Reuse of payment, refunds, reviews, notifications, audit

All keyed to the same `Order`/`Payment` aggregates — nothing to fork. A direct purchase
behaves like any other successful purchase everywhere (receipt, invoice, emails, lifetime
spending, refund reversal, audit rows). One genuine gap: **review purchase-verification
does not exist in code** (`ViewBag.CanReview = reviewer is not null` — any signed-in
customer can review). The brief requires unpaid/failed/abandoned direct orders not to
qualify as a purchase (§14). We implement the rule **uniformly for both paths**:
a review requires an order containing that product in a paid-or-later state
(`Paid, AwaitingProcurement, InProduction, QualityCheck, Ready, OutForDelivery, Completed,
Delivered`) — `Cancelled`/`Refunded` and unpaid orders do not qualify. Enforced
server-side in `CreateReviewHandler` and reflected on the product page copy. Existing
rules kept: one review per customer per product, admin moderation.

## 9. Simpler alternatives considered (and rejected)

- **Derive “direct vs custom” from child collections/status history** — misclassifies
  custom orders with optional-only answers and legacy rows; invisible after convergence.
- **`DirectPurchaseEnabled` product flag** — third source of truth; contradicts the
  owner’s auto-detect decision of 2026-09-09 and adds admin busywork.
- **Separate direct-order entity/flow** — explicitly forbidden (brief §5); forks payments,
  invoices, permissions, notifications.
- **Status-only discrimination** — impossible once both paths are `PendingPayment`.
- **New “Buy Now” parallel flow** — not needed: the app already has a cart and checkout
  (brief §9: “if the existing application does not have cart functionality… may be more
  appropriate” — it does). The purchasing action for eligible products is **Add to cart →
  Checkout**, presented clearly on the product page.

## 10. Recommended implementation (the chosen model)

**Chosen ordering model — one Order aggregate, kind at creation, derived eligibility:**

1. **Domain**: `OrderKind { Direct, Custom }`; `Order.Kind` set via
   `Order.CreateDraft(orderNumber, customerId, kind)`. No other domain changes.
2. **Eligibility rule** (single helper): eligible ⇔ `!IsCustomizable && category has no
   active options`. Enforced server-side in `AddCartItemHandler` (cart add) and in
   `CreateOrderHandler` when `Kind == Direct` (final authoritative gate at order
   creation; catches cart items added before a product became custom). Clear customer
   message pointing to “Order / Customize”. The inverse guard on `SubmitOrderRequestHandler`
   already exists (custom path rejects plain products).
3. **UI**: product pages keep the existing auto-detected CTAs (Add to cart / Order ·
   Customize). `PortalOrders/Workspace` hides design/custom furniture for direct orders.
   Admin order page + list show a **“Direct purchase” / “Custom order”** badge.
4. **Audit**: `BusinessActivityCapture` emits kind-aware entries for direct orders:
   “Direct product order created”, “Direct order payment received”, “Direct order status
   changed” (same `data-type` keys, existing infrastructure).
5. **Reviews**: purchase-verified eligibility (§8) in handler + product page.
6. **Everything else unchanged**: payments (Paystack), retries/fresh references,
   refunds + spending reversal, invoices/receipts, notifications/emails, real-time pushes
   (payment/status/refund already push via `NotificationService` — reused, nothing
   real-time-ified further), customer history (one coherent list), sharing/filtering/
   categories.

Migration: additive `Kind` column (string, default `"Custom"` is wrong for new rows — new
rows always set it explicitly) with a **backfill**: rows with any custom-workflow children
(responses/files/proofs/design reviews) or pre-payment custom statuses → `Custom`, else
`Direct` (matches the historical mix: cart orders were direct).

This fits the architecture because it touches exactly one seam (creation entry points),
reuses the entire post-`PendingPayment` machinery for both paths, and encodes the
owner’s already-agreed auto-detect rule instead of inventing new configuration.
