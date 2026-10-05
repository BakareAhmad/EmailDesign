# Customer Portal & Public-Facing UI/UX Redesign — Architecture Review

_Status: complete. Sections 2–12 describe what shipped; §11 is the final validation record and §12 the evidence index._

## 1. Phase 1 — Audit findings (before any change)

### 1.1 Surfaces and layouts found
| Surface | Layout | Verdict |
|---|---|---|
| `/` `/products` `/products/{id}` `/services` `/gallery` `/about` `/faq` `/contact` `/privacy` `/terms` | `_PublicLayout` (`.pnav` top nav, `.pfoot` footer) | Storefront-ish but page-band headers, 220px sticky category card, table-like toolbars. |
| `/portal/**` (dashboard, shop, product, order form, orders, workspace, profile, addresses, wishlist, payments, invoices) | `_PortalLayout` | **Root cause of the "dashboard/school-portal" look**: it re-uses the admin `app-shell` / `sidebar` / `topbar` / `content` classes from `app.css` — dark 250px sidebar, breadcrumbs, topbar title, data tables for orders/payments. |
| `/cart` `/checkout*` | switches: `_PortalLayout` for signed-in customers, `_PublicLayout` otherwise | Two visual experiences for the same page. |
| `/Account/*` | `Layout = null` (6 self-contained HTML pages) | Own duplicated `<head>`, admin-oriented hero copy on Login. |
| `/admin/**` | `_AdminLayout` | Operational, dense — **out of scope, untouched**. |

### 1.2 Duplication
* `Views/StoreProducts/Index` ≈ `Views/PortalShop/Index` (catalog), `Views/StoreProducts/Details` ≈ `Views/PortalShop/Product` (product page). Same filters, same grid, different chrome.
* Cart / checkout / payment-result pages contain `if Customer → /portal/shop else /products` branches everywhere.

### 1.3 CSS architecture
* One 51 KB `wwwroot/css/app.css` with good tokens (`--primary #4338CA`, warm neutrals, radii, shadows) shared by admin **and** customers; storefront block (`.pnav/.pcard/.page-band/.pfoot`) lives inside it.
* `tailwind.build.css` is a 7.6 KB **prebuilt** utility subset; there is no Tailwind build pipeline in the repo → new utility classes cannot be added. Custom CSS is the only safe path.
* Heavy inline `style=""` in nearly every view; per-page `@section Styles` blocks (home, product, gallery, contact).
* Fonts: Inter via Google Fonts (async, non-blocking).

### 1.4 Ordering model (locked business rules — preserved)
* `DirectCheckout = !(product.IsCustomizable || category has active options)`.
* Direct → Add to cart (`POST /portal/shop/{id}/add-to-cart`) → `/cart` → `/checkout` → `POST checkout` → `/checkout/complete/{id}` → `POST /checkout/{id}/pay` (Paystack) → `/checkout/return`. Order.Kind = Direct.
* Custom → `/portal/shop/{id}/order` (category options form) → `/portal/orders/{id}/workspace` (chat, files, proofs, design reviews, timeline, summary, pay). Order.Kind = Custom. Custom products are never added to the cart (server guard).
* Customer status labels come only from `OrderStatusPresentation.CustomerLabel/NextStep/ActionRequired`.
* Reviews: only verified purchasers (`HasPurchasedProductQuery`).
* Live sync: `order-workspace.js` + `workspace-chat.js` patch DOM by ids (`#chatLog #chatCard #proofList #proofCard #designReviewList #fileList #timelineList #summaryBody #payBlock #docsBlock`, `[data-ws-status]`, `[data-proof-id]`, …). The same script serves admin → **DOM contract must be preserved, only styling may change.**

### 1.5 Data gaps that blocked the desired UI
* `ProductSummaryDto` (cards) had no capability flag → cards could not show "Add to cart" vs "Customize".
* `CartItemDto` had no image URL → cart could not show product thumbnails.
* No "Buy now" path (only Add to cart → cart → checkout).

### 1.6 Assets
* Origin repo lacks `wwwroot/img/logo.png`, `hero.jpg`, `hero-mobile.png`, `prod-*.png` (only `img/reviews/*` are tracked). Every brand slot already had an `onerror` "DC" fallback; the hero referenced a missing image. Older repos that held the assets are no longer reachable.

### 1.7 Existing regression harness
* `e2e/direct-checkout/workflow.py` (60 checks: browse/search/filter, direct purchase via Paystack stub incl. decline+retry, refund, lifetime spending, review gate, custom request → workspace → finalize → pay, admin views).
* Unit + integration tests: 748.


## 2. What changed (summary)
* **One customer shell.** `Views/Shared/_CustomerLayout.cshtml` is now the single layout for every customer-facing page (public storefront **and** signed-in portal). `_PublicLayout.cshtml` and `_PortalLayout.cshtml` still exist but are one-line pass-throughs (`Layout = "_CustomerLayout"`), so no controller or view had to change its `Layout` and nothing that referenced them broke. Account pages (login/register/forgot/reset/verify/access-denied) use a small `_AuthLayout.cshtml` (centered card, "Back to store" link) instead of six duplicated self-contained HTML documents.
* **New stylesheet, no framework.** `wwwroot/css/customer.css` (≈ 880 lines, hand-written) carries the whole customer design system and is loaded *after* `app.css` only by `_CustomerLayout`/`_AuthLayout`. It re-uses the existing tokens (`--primary`, neutrals, radii, shadows) and existing primitives (`.btn*`, `.field/.label/.input`, `.alert-*`, `.badge`, `.card`) and only adds customer-prefixed classes (`.cs-*` shell, `.pc-*` product card, `.sh-*` shop, `.pd-*` product detail, `.ck-*` cart/checkout, `.ac-*` account, `.oc-*` order card, `.ws-page` workspace skin, `.hm-*` home, `.pg-*` public pages). `app.css` and `tailwind.build.css` were **not edited**, so the admin portal is byte-for-byte unaffected (verified with `git diff` and screenshots).
* **Two tiny scripts.** `wwwroot/js/customer-shell.js` (mobile search toggle, account menu, bottom sheets, quantity steppers, password reveal, auto-submit selects) and `wwwroot/js/customer-product.js` (gallery thumbs/swipe, lightbox). Both are plain ES5, no dependencies. Existing scripts (`signalr`, `dcx-toast`, `portal-notifications`, `order-workspace.js`, `workspace-chat.js`, `attachment-preview.js`) are loaded exactly as before.
* **Shared partials replace duplicated pages.** `Views/Shared/Customer/_Catalog.cshtml`, `_ProductCard.cshtml`, `_ProductDetail.cshtml`, `_OrderCard.cshtml`, `_AccountTabs.cshtml`. `StoreProducts/Index` and `PortalShop/Index` now both render `_Catalog`; `StoreProducts/Details` and `PortalShop/Product` both render `_ProductDetail`; the dashboard and the orders list both render `_OrderCard`.
* **Small, additive backend changes only** (no new business systems):
  * `ProductSummaryDto` += `IsCustomizable`, `CategoryHasOptions`, `IsMadeToOrder` (derived with the *existing* `DirectCheckout` rule) so cards can show the right CTA; populated in `CatalogQueries`.
  * `CartItemDto` += `ImageUrl` (populated in `OperationsQueries`) so the cart can show thumbnails.
  * `PortalShopController.AddToCart` accepts an optional `buyNow` flag that redirects to `/checkout` instead of `/cart` (same cart/order pipeline — "Buy now" is just "add to cart, then go to checkout").
  * `StoreHomeController` passes featured products / categories using the existing `GetFeaturedProductsQuery`; `CustomerShellService` (new, scoped) supplies the whitelisted public business settings + major categories to the layout so portal pages get the same header/footer as the storefront.
  * `CustomerPresentation.cs` (customer-facing helpers: status → pill class, tracker step, friendly labels) and `CustomerIcons.cs` (inline SVG icon set, 50 icons, no icon font).

## 3. Pages redesigned
| Area | Views |
|---|---|
| Shell | `_CustomerLayout`, `_AuthLayout`, `Components/CartBadge/Default` |
| Home & public | `StoreHome/Index`, `StoreServices/Index`, `StoreGallery/Index`, `StorePages/{About,Contact,Faq,Privacy,Terms}` |
| Shop | `StoreProducts/Index`, `PortalShop/Index` (→ `_Catalog`), `StoreProducts/Details`, `PortalShop/Product` (→ `_ProductDetail`), `PortalShop/OrderForm` (custom order request) |
| Cart / checkout / payment | `StoreCart/Index`, `Checkout/{Index,Complete,Return,NoProfile}` |
| Auth | `Account/{Login,Register,ForgotPassword,ResetPassword,VerifyEmail,AccessDenied}` |
| Account | `PortalDashboard/Index`, `PortalOrders/{Index,Details,Track,Workspace}`, `PortalProfile/{Index,ChangePassword}`, `PortalAddresses/{Index,Add}`, `PortalWishlist/Index`, `PortalPayments/Index`, `PortalInvoices/Index` |

Left as they were (still work, not part of the customer journey or already standalone): `Views/Home/Index` (staff foundation page, `_Layout`), `PublicInvoice/{Invoice,Receipt}` and `Unsubscribe/Unsubscribe` (self-contained print/e-mail landing documents), every `Views/Admin*` view and `_AdminLayout`, `_Layout`.

## 4. Navigation
* **Top bar (all widths):** logo → brand, search (inline on ≥1024px, icon-toggle that reveals a full-width search row on mobile), notification bell (signed-in customers; same `#portalNotifBell/#portalNotifBadge/#portalNotifDrop` ids the existing `portal-notifications.js` expects), cart with live count (existing `CartBadge` view component), account avatar menu (signed-in) or *Sign in / Create account*.
* **Mobile bottom nav (<1024px):** `Home | Shop | Cart | Orders | Account` (Account becomes *Sign in* for guests). Chosen from real features: Orders is where custom-order conversations, design approvals and payments live, so it earns a permanent slot; wishlist/addresses/payments/invoices/profile sit in the account menu and the account page instead. Cart shows the same live count badge. 44×44 targets, safe-area padding, current page highlighted.
* **Desktop primary links:** Shop · Custom printing · Our work · About · Contact.
* **Account menu / account tabs:** My account, My orders, Track an order, Wishlist, Addresses, Payments, Invoices & receipts, Profile & password (+ *Admin dashboard* link for staff-capable users, Sign out). Signed-in account pages show a horizontal chip row (`_AccountTabs`) on every width instead of the admin sidebar.
* **Footer:** brand, Shop / Help / Talk to us columns, WhatsApp button, social links, Paystack/NGN note — all values come from the business settings (`business.*`), nothing hard-coded except the fallback WhatsApp number that already existed.

## 5. Home page
Hero (eyebrow, headline, one-line value proposition, search box, two CTAs: **Shop products** / **Start a custom order**), optional business figures (rendered **only** if the owner has set `business.stat_*` settings — no invented numbers), product mosaic built from real featured products, *Shop by category* (major categories with counts when configured, otherwise the flat categories), *Popular right now* (featured products via `GetFeaturedProductsQuery`), the ready-to-buy vs custom explainer, *How a custom order works* (the real 4-step workflow: request → we confirm → approve design → pay/produce/deliver), trust strip (payments via Paystack, design approval, delivery or pickup, chat with the team — all true of the system), reviews (existing static testimonials with real logos), and a contact band.

## 6. Shop, cards, product detail
* `_Catalog`: prominent search, major-category chips (mobile) / sidebar tree (desktop), sub-category chips, **Filter & sort bottom sheet** on mobile (`.sh-sheet`, opened with `data-sheet-open`), sorting demoted into the sheet / a small select on desktop, "Showing 1–12 of 97" as quiet text, empty and no-results states, pagination.
* `_ProductCard`: 1:1 lazy image (`loading="lazy"`, fixed aspect ratio → no layout shift), category, name (2-line clamp), prominent ₦ price ("from" for made-to-order), *Made to order* pill only when true, out-of-stock state, capability-based CTA — **Add to cart** for direct products, **Customize** for made-to-order products.
* `_ProductDetail`: gallery with thumbs/swipe/lightbox, name, price, stock line, description, share/copy link, wishlist, quantity stepper, **sticky mobile CTA bar** (price + Buy now / Order · Customize), trust bullets, reviews list + review form (only for verified purchasers — the existing `HasPurchasedProductQuery` gate), related products.
* Custom order form (`PortalShop/OrderForm`): "Tell us what you need" — product summary card, 3-step explainer (you are here), quantity, category options (same `opt_{id}` inputs, required/conditional rules and inline JS preserved), file attachment, notes, *Submit order request*, "no payment is taken now" reassurance. No aggregate / assignment / workflow-state jargon anywhere.

## 7. Cart, checkout, payment
* Cart: image, name, options, unit price, quantity stepper (POST update), line subtotal, remove, clear; summary with subtotal, delivery (only when applicable), estimated total, **payment service fee** note (never "delivery fee"), *Proceed to checkout* + sticky mobile bar.
* Checkout: 3-step progress (Cart → Delivery → Payment), *How would you like to receive it?* (Deliver to me / Pick up, radio cards), address fields shown only for delivery, notes, order summary with **Payment service fee** line and **You will pay** total, *Place order* (+ sticky mobile bar). Same `PlaceOrder` action, same validation.
* Payment: `Checkout/Complete` shows a clean order-received card with **Pay with Paystack · ₦X**; `Checkout/Return` shows Payment successful / Payment failed with a retry button and links to the order — no Paystack references, transaction internals or raw statuses.

## 8. Account, orders, workspace
* Account overview: greeting, counters (orders / in progress / saved), recent orders as cards, "Your account" list.
* Orders: filter chips (All / In progress / Custom / Completed), `_OrderCard` with kind, number, date, total, customer-facing status pill and next-step line; *Track by number* kept.
* Order details: status callout (action required vs "we're working on it"), 4-step tracker (Order placed → Payment confirmed → In production → Ready/delivered) coherent with `OrderStatusPresentation.CustomerLabel`, items, summary, help card, link to the conversation for custom orders.
* Workspace: restyled as a chat-first page (`.ws-page`): conversation card with bubbles / media / voice notes, design version (proof / design review) cards with **Approve design** and **Request changes**, files, timeline, summary and pay block. **All ids/classes consumed by `order-workspace.js`, `workspace-chat.js` and the admin workspace are unchanged**; only wrappers and CSS changed.

## 9. Customer / admin separation
* Admin keeps `_AdminLayout` + `app.css`; neither file changed. `customer.css` is scoped by the `.cs-body` class set on the customer layouts, so its rules cannot leak into admin pages.
* The shared `order-workspace.js` still drives both admin and customer workspaces; the customer workspace only adds a skin (`.ws-page`).
* Staff who sign in through the shared login page are redirected exactly as before (`Home`/admin), and the customer account menu shows an *Admin dashboard* link for users with admin capability.

## 10. Business-logic preservation
No changes to order/cart/payment/catalogue handlers, validators, state machine, permissions or notifications. All customer actions post to the same MVC actions with the same field names (`quantity`, `Fulfilment`, `Notes`, `opt_{id}`, `files`, `Rating/Title/Body`, …). Verified by the existing 60-check direct-checkout E2E (browse → search → filter → add to cart → checkout → Paystack decline → retry → success → refund → review gate → custom request → workspace → finalize → pay → admin views), which passes unchanged on the new UI.

## 11. Issues found during visual QA and how they were fixed
| Found | Fix |
|---|---|
| Home overflowed horizontally at 390px (trust grid: nested `div` matched the tile rule) | `.hm-trust > div` / `> div > div{min-width:0}` |
| Hero search placeholder clipped by the button on mobile | shorter placeholder |
| Mobile shop showed no category chips when categories have no major category | chips and filter-sheet radios now include un-grouped categories; duplicate "Category" block removed when there are no majors |
| "Remember me" checkbox label wrapped (`.checkbox` applied to both label and input) | new `.cs-check` row |
| `hidden` attribute defeated by `display:inline-flex` (Action-required badge visible on paid orders) | `.cs-body [hidden]{display:none!important}` |
| Tracker showed "Payment confirmed" while the pill said "In Production" for `Paid` orders | tracker step for `Paid/AwaitingProcurement` aligned with the customer label |
| About page 4-stat grid overflowed at 320px | responsive `.ac-stats.is-four` (2 cols <640px) |
| Workspace overflowed at 320px (presence text, chat compose input intrinsic width, requirements table `nowrap`, bottom-nav `1fr` tracks) | shrinkable presence line, `width:0` compose input, `.ws-req-table` wrapping, `minmax(0,1fr)` nav tracks |
| Logo / hero photo not visible in evidence (brand files absent from v047.git — see §12a) | asset-aware rendering: real files used when present, clean fallback otherwise; owner action documented in §12a and AI_HANDOFF §89 |
| WhatsApp icon (hand-drawn stroke path) rendered as a broken outline with a stray dot (owner feedback on evidence) | replaced with the official filled WhatsApp glyph; `CustomerIcons.Svg` now renders brand glyphs listed in `Filled` with `fill=currentColor` instead of strokes; evidence regenerated from a fresh final run |

After the fixes an automated overflow scan (`document.scrollWidth > clientWidth`) of 30 customer pages × 7 widths (320/375/390/430/768/1024/1440 = 210 page-loads) reports **0** overflows.

## 12. Final validation record (2026-09-25, one clean run, E2E tag `final`)
| Step | Result |
|---|---|
| `dotnet build DeChimex.sln` | 0 errors |
| `dotnet test DeChimex.sln` | **748 / 748** (418 unit + 330 integration) — `final-test-log.txt`. 9 integration assertions that were coupled to the old markup/copy (`portal-main` class, "Welcome back", "Your brand", "Order this product", `class="pcard"` regex, `&#8358;` entity, "Category filter", "View product / order") were updated to the new copy; their behavioural intent (portal links, cart guard, sorting before paging, wishlist states, no-image tile) is unchanged. |
| `e2e/direct-checkout/workflow.py final` (fresh in-memory app, Paystack + Brevo stubs) | **60 / 60** — `final-customer-redesign-e2e-log.txt`; 21 journey screenshots (`e2e/customer-redesign/evidence/001–021`) |
| `e2e/customer-redesign/visual_qa.py final evidence …` | 70 full-page screenshots — every customer page at 390 px and 1440 px + admin dashboard/orders/products (`e2e/customer-redesign/evidence/022–091`) |
| Responsive overflow scan | 30 customer pages × 7 widths (320/375/390/430/768/1024/1440) = 210 loads, **0 horizontal overflows** (`e2e/customer-redesign/evidence/overflow-scan.txt`), plus viewport captures at 320/430/768/1024 (`e2e/customer-redesign/evidence/092–111`) |
| Admin unaffected | `git diff` on `Views/Admin*`, `_AdminLayout.cshtml`, `app.css`, `tailwind.build.css` = empty; admin screenshots identical in style |

`e2e/customer-redesign/evidence/README.md` indexes the 111 screenshots (final run only; earlier/debug runs deleted). The previous task's evidence moved to `e2e/provider-keys/evidence/`. (2026-09-25 keys re-insert: this set was then relocated to `e2e/customer-redesign/evidence/`.)

## 12. Change log addendum — Brand-asset fix (2026-09-25, later)
* `Services/CustomerShellService.cs`: `LogoUrl` / `HeroDesktopUrl` / `HeroMobileUrl` (file-existence probe, 1-min cache).
* `_CustomerLayout.cshtml` (header + footer), `_AuthLayout.cshtml`: logo `<img>` only when `img/logo.png` exists, else "DC" badge rendered server-side (removed the `onerror` fallback and the 404).
* `StoreHome/Index.cshtml`: hero renders the brand `<picture>` (`hero-mobile.png` ≤720px / `hero.jpg`) when present; product mosaic fallback otherwise. Fourth real testimonial (DE-YEMKING Publishers, whose logo *is* in the repo) restored; reviews grid is 4-up on desktop.
* `customer.css`: `.hm-hero-img` (rounded, `aspect-ratio` 4/3 → 3/2 ≥1024px, `object-fit: cover`, no layout shift).
* `e2e/customer-redesign/overflow_scan.py`: the 7-width overflow scan is now a committed script (30 customer pages incl. the 404 page).
* Re-verified: build 0 errors, 748/748 tests, 60/60 E2E, 70 visual shots, 210/210 overflow-free, evidence regenerated (115 shots incl. 4 labelled stand-in asset checks).

## 12a. KNOWN GAP — brand images are not in the v047.git repository (logo / hero look "missing" in the evidence)
**Symptom in the evidence screenshots:** the header/footer/login show a purple "DC" badge instead of the DE-CHIMEX logo, and the home hero shows a product mosaic (with the E2E's generated green fixture images) instead of the brand hero photo.

**Root cause:** `https://github.com/BakareAhmad/v047.git` (commit `3ab3ee5 "initial"`, the base of this work) contains only `wwwroot/img/reviews/*`. The brand files that earlier repositories/zips carried — `logo.png`, `logo-mark.png`, `logo.jpg`, `hero.jpg`, `hero-mobile.png`, `hero-products.jpg`, `prod-{flask,gift,jersey,mug,notepad,pen}.png`, `stage-*.jpg` — were never pushed to v047.git (they are **not** git-ignored; they were simply absent from the push). The sandbox has no other copy, so they cannot be re-materialised here, and inventing replacement brand artwork is out of the question.

**What the code does now (so the site is never broken):** `CustomerShellService` checks for `wwwroot/img/logo.png`, `hero.jpg`, `hero-mobile.png` at render time (1-minute cache). If present: the real logo is rendered in the top bar, footer and auth pages, and the home hero shows the brand photo (`<picture>`: `hero-mobile.png` ≤720px, `hero.jpg` above). If absent: the "DC" badge and the featured-product mosaic are rendered directly — no broken `<img>`, no 404 round-trip, no `onerror` flicker.

**How to fix (owner, one-off):**
1. Copy the files from your previous working copy or any earlier deliverable zip (path `DeChimexPrints/src/DeChimex.Host/wwwroot/img/`) into `src/DeChimex.Host/wwwroot/img/` of this repo. Minimum set: `logo.png` (1024×1024 RGBA), `hero.jpg` (1536×1024), `hero-mobile.png` (800×1093).
2. `git add DeChimexPrints/src/DeChimex.Host/wwwroot/img && git commit -m "Add brand images"`.
3. Restart the app (or wait ≤1 min) — the logo and hero appear automatically; nothing else to configure. Verify `/img/logo.png` and `/img/hero.jpg` return HTTP 200.

## 13. Areas intentionally left unchanged
Admin/staff portal (all `Views/Admin*`, `_AdminLayout`, `app.css`, `tailwind.build.css`), public invoice/receipt documents, unsubscribe landing page, staff `Home` page, e-mail templates, all Application/Domain/Infrastructure code except the two additive DTO fields and their projections.
