# Changelog: DE-CHIMEX PRINT HUB

All notable changes to this project will be documented in this file.

---

## Email UI redesign: app-true design system

**Date:** 2026-10-05
**Status:** Complete

- **`src/DeChimex.Infrastructure/Email/EmailTemplateRenderer.cs`** (the single email layout,
  17 templates + `RenderMarketing`): palette, type, radii, status pills, alert tones and the
  brand lockup now come from the app's own stylesheets (`app.css :root` + `customer.css`
  `.cs-*`). The email-only violet/rainbow language (`#7C3AED`, `#EC4899`, `#F97316`,
  `#F6F5FC`, `#3B3752`, `#1E1B2E`, `#6B6880`, `#ECEAF5`, `#F3F0FF`, Segoe UI) is gone.
  Shell = preheader, app top bar (40px indigo tile with the real `/img/logo.png` +
  DE-CHIMEX / PRINT HUB wordmark), 600px white card on the warm `--canvas` paper, slim
  footer strip (tagline, Privacy/Terms/Contact/support email, copyright, Paystack/NGN line;
  marketing keeps the server-appended unsubscribe). Components mirror app components
  (`Title`/`.cs-page-head h1`, `Rows`/workspace table, `Total`/`.ck-sum-row.is-pay`,
  `Badge`/`.cs-status`, `Panel`/`.cs-callout`, `Message`/chat bubble, `Code`/token input,
  `Alert`/`.flash`, `Button`/`.btn-primary`). Placeholder contract, subjects, triggers,
  recipients, routes and queueing unchanged; plain text keeps action URLs inline.
- **`src/DeChimex.Application/Features/Contacts/Commands/ContactMessageCommands.cs`**:
  staff reply greeting no longer sets its own colour (the shell owns the palette).
- **`src/DeChimex.Host/Views/AdminMarketing/Compose.cshtml`**: the broadcast preview now
  mirrors the real email (canvas, lockup, 600px card, slim footer, unsubscribe line).
- **`src/DeChimex.Host/Views/Unsubscribe/Unsubscribe.cshtml`**: brand lockup updated to the
  app lockup with app tokens (standalone page, so the `.cs-*` rules are inlined).
- **Tests**: `EmailTemplateRendererTests` locks updated (app primary `#4338CA`, "Creativity
  with Ease.") and extended (plain-text link reachability, no em/en dashes, `.txt` previews);
  new **`EmailDesignSystemTests`** re-reads the app stylesheets and fails on any invented
  colour, on the retired palette, or on a non-Inter type stack.
- **Harnesses**: new `e2e/email-ux/email_e2e.py` + `run_final.sh` (pipeline stage 8h,
  `PRIMARY=emailux`) auditing all 19 rendered templates, every live send captured by the
  Brevo stub in the run, live drives for the templates other journeys miss, WCAG AA contrast
  and desktop/mobile/dark/320px screenshots; the shared verification-code extractor is now
  palette-agnostic (`verification_code()`); evidence archive guards identify a previous run
  by a shot name unique to its primary journey.
- **Docs**: new **`EMAIL_DESIGN_STANDARD.md`** (the contract future templates must follow);
  `EMAIL_UX_AUDIT.md`/`EMAIL_UX_REPORT.md` marked superseded where the design layer changed.
- **Verification**: one clean run `bash e2e/email-ux/run_final.sh emailfinal` -> 905/905
  tests, `/health` Healthy, checkout 60/60, brand 282/282, design-review green, fixes 39/39,
  dev-portal 108/108, dev-abort 37/37, PWA 27/27, placeholder 18/18, impl-fixes 17/17,
  footer 404/404, **email design 636 passed / 0 failed** (47 live emails, 15 templates, all
  CTA links resolving on the app), 0 unhandled exceptions, leak sweep clean; 44 shots in
  `evidence/`.

---

## Footer placement by layout

**Date:** 2026-10-05
**Status:** Complete

- **`src/DeChimex.Host/Views/Shared/_CustomerLayout.cshtml`**: the storefront footer
  (`.cs-foot`) now renders only when `ViewData["Footer"]` is `"full"` (the default for the
  public storefront). `"slim"` renders a one-line legal bar (`.cs-foot-slim`: copyright,
  Privacy/Terms/Contact, payment line) and any other value renders no footer. Footer markup,
  links and classes are unchanged.
- **`src/DeChimex.Host/Views/Shared/_PortalLayout.cshtml`**: no longer a pass-through. It is
  the customer app + transactional shell (`Footer = "slim"`) with the existing active-nav
  inference.
- **18 views** moved from `_CustomerLayout` to `_PortalLayout` (dashboard, orders, order
  details, tracking, workspace, profile, password, addresses, wishlist, payments, invoices,
  custom-order form, cart, checkout, confirmation, payment return, no-profile). Public
  storefront pages and `/portal/shop` keep the full footer; admin, auth and dev layouts were
  already footer-free.
- **`wwwroot/css/customer.css`**: `.cs-foot-slim` styles added next to the existing footer
  block; the bar wraps on phones and clears the fixed bottom nav via the existing body padding.
- **Verification**: new `e2e/footer/footer_e2e.py` (+ `run_final.sh`, pipeline stage 8g)
  covering 38 page groups across 320/390/1440 px; one clean final run
  (`bash e2e/footer/run_final.sh footerfinal`) = 900/900 tests, 404/404 footer checks,
  60/60 checkout, 282/282 brand, 36/36 design-review and all other journeys green;
  evidence = 28 ordered shots + 13 stage logs in `evidence/`;
  before/after pair in `e2e/footer/evidence-before/`.

## Migration fix: PwaPushSubscriptions creates its table

**Date:** 2026-10-04
**Status:** ✅ Fixed

- **`src/DeChimex.Persistence/Migrations/20261004110951_PwaPushSubscriptions.cs`**: the migration only
  diffed an index against a stale model snapshot — no migration ever created the `PushSubscriptions`
  table, so `Update-Database` against any real SQL Server database failed with SQL error 3701:
  *"Cannot drop the index 'PushSubscriptions.IX_PushSubscriptions_UserId_Endpoint', because it does
  not exist or you do not have permission."* The migration now creates the table (all 14 columns,
  PK), the `IX_PushSubscriptions_Endpoint` index and the filtered unique index
  `IX_PushSubscriptions_UserId_Endpoint` (`WHERE [IsDeleted] = 0`) in one transactional step;
  `Down` drops the table. Guarded by `MigrationChainTests` (3 regression tests that regenerate the
  migration DDL and assert: hop = table + indexes with no `DROP INDEX`; table created exactly once
  in the full chain; nothing is ever dropped before its table exists).

---


## v002: Phase 2: Server-Side Cart & Checkout

**Date:** 2026-07-21
**Phase:** 2 of 10
**Status:** ✅ Complete

### Summary

Replaced localStorage cart with the server-side Cart API for authenticated users. Built a full checkout flow with real order creation, Paystack card payment integration (inline popup + redirect fallback), bank transfer with manual confirmation, and pay-on-delivery. Created order confirmation and payment callback pages.

### Cart: Server-Side

- **`public/product.html`**: "Add to cart" now calls `POST /cart/items` for authenticated users (falls back to localStorage for anonymous). "Save to wishlist" button calls `POST /wishlist/items` (prompts login if anonymous).

- **`public/products.html`**: "Add" button on product cards now calls `api.cart.add()` with event propagation stopped (doesn't navigate away). Shows "Added" confirmation with checkmark animation.

- **`public/cart.html`**: Complete rewrite:
  - Authenticated users: loads from `GET /cart`, updates via `PUT /cart/items/{id}`, removes via `DELETE /cart/items/{id}`
  - Anonymous users: localStorage cart with "Sign in for a better experience" banner
  - "Proceed to checkout" requires authentication (redirects to login with return URL)
  - Proper error handling for all cart operations

### Checkout: Real Order Creation

- **`public/checkout.html`**: Complete rewrite:
  - Loads cart from server (`GET /cart`)
  - Loads customer profile and saved addresses (`GET /customers/me`)
  - Reads delivery fee from settings API (falls back to ₦1,500)
  - Form validation (name, phone, email, shipping address required)
  - Saved address dropdown with auto-fill
  - Three payment methods:
    - **Card via Paystack**: calls `POST /payments/initialize` → opens Paystack inline popup (or redirects to hosted checkout as fallback)
    - **Bank transfer**: calls `POST /payments/initialize` with `BankTransfer` → shows bank details + reference
    - **Pay on delivery**: creates order without payment initialization
  - Clears server cart after successful order creation
  - Handles all error states with user-friendly messages

### New Pages

- **`public/order-confirmed.html`**: Order confirmation page with four states:
  - Payment confirmed (green success state)
  - Bank transfer pending (shows bank details, reference, WhatsApp link to send proof)
  - Pay on delivery (blue info state)
  - Payment pending (yellow warning - Paystack popup closed or redirect incomplete)
  - Links to track order, view orders, continue shopping

- **`public/payment-callback.html`**: Paystack callback handler:
  - Receives `reference` from Paystack redirect
  - Calls `POST /payments/verify` to confirm payment
  - Redirects to order-confirmed page with appropriate status
  - Shows loading spinner during verification

### Backend Changes

- **`src/DeChimex.Infrastructure/Services/AppUrlProvider.cs`**: Changed `BuildPaymentCallbackUrl` to point to the frontend `payment-callback.html` page instead of the API JSON endpoint. This ensures Paystack redirects land on a user-friendly page that verifies the payment and shows confirmation.

### Configuration

- **`assets/js/api.js`**: Added `paystackPublicKey` to `API_CONFIG` for Paystack inline popup integration.

### Files Modified

**Frontend (6 files modified, 2 new):**
- `public/product.html` (server-side cart + wishlist)
- `public/products.html` (quick-add button)
- `public/cart.html` (server-side cart with fallback)
- `public/checkout.html` (real order creation + payment)
- `assets/js/api.js` (Paystack public key config)
- New: `public/order-confirmed.html`
- New: `public/payment-callback.html`

**Backend (1 file):**
- `src/DeChimex.Infrastructure/Services/AppUrlProvider.cs` (callback URL → frontend)

### Build Status

- Backend: **0 errors, 2 warnings** (AutoMapper NU1903 advisory - deferred to Phase 9)
- Frontend: No build step (vanilla HTML/JS/CSS)

### What This Unlocks

- **Phase 3 (Portal)**: Customers now have real orders to track, invoices to view, and payments to see
- **Revenue flow**: The business can now accept real orders and payments end-to-end
- **Phase 5 (Quotation → Order)**: The order creation flow is proven and stable

---

## v001: Phase 1: Security & Auth Hardening

**Date:** 2026-07-21
**Phase:** 1 of 10
**Status:** ✅ Complete

### Summary

Eliminated security bypasses and wired all authentication flows to real backend endpoints. Every auth page now calls the live API with proper validation, error handling, and button state management. Portal pages no longer inject fake demo users.

### Security Fixes

- **Removed demo-user auto-seed from 7 portal pages**: `portal/orders.html`, `portal/invoices.html`, `portal/payments.html`, `portal/wishlist.html`, `portal/track.html`, `portal/profile.html`, and `portal/addresses.html` previously contained `if(!Auth.isAuthed)Auth.save({userId:'cu1',...})` which bypassed authentication and exposed customer data to unauthenticated visitors. All replaced with `if(!Auth.isAuthed){ location.href='../auth/login.html'; }`.

- **Added auth guard to checkout**: `public/checkout.html` now redirects unauthenticated users to the login page, storing the return URL in `sessionStorage` for post-login redirect.

### Auth Pages: Wired to Real API

- **`auth/register.html`**: Added client-side validation (required fields, password min 8 chars, password confirmation match, ToS checkbox). Calls `POST /auth/register` with proper error handling. On success, redirects to verify-email with email in query params.

- **`auth/forgot-password.html`**: Added email validation. Calls `POST /auth/forgot-password`. Shows privacy-safe success message. Redirects to reset-password with email in query params.

- **`auth/reset-password.html`**: Reads `token` and `email` from URL query params. Falls back to manual token entry. Calls `POST /auth/reset-password` with real payload.

- **`auth/verify-email.html`**: Auto-verifies when `token` and `email` are present in URL. Falls back to manual verification. Role-aware redirect after verification.

### Profile Pages: Wired to Real API

- **`admin/profile.html`**: Change password calls `POST /auth/change-password`.
- **`portal/profile.html`**: Loads real customer data, saves via `PUT /customers/me`, change password via `POST /auth/change-password`.
- **`portal/addresses.html`**: Loads from API, add/delete via real endpoints.

### Portal Pages: Fixed

- **`portal/orders.html`**: Auth guard + error handling + track links with order number.
- **`portal/invoices.html`**: Auth guard + error handling.
- **`portal/payments.html`**: Auth guard + error handling.
- **`portal/wishlist.html`**: Changed from `api.products.search()` to `api.wishlist.get()`.
- **`portal/track.html`**: Complete rewrite with real API data and dynamic status progression.

### Backend Fix

- **`CancelRefundOrder.cs`**: Removed unused `payments` parameter (CS9113 warning fixed).

### Files Modified

**Frontend (14 files):** auth/register.html, auth/forgot-password.html, auth/reset-password.html, auth/verify-email.html, admin/profile.html, portal/orders.html, portal/invoices.html, portal/payments.html, portal/wishlist.html, portal/track.html, portal/profile.html, portal/addresses.html, public/checkout.html, public/cart.html

**Backend (1 file):** src/DeChimex.Application/Features/Orders/Commands/CancelRefundOrder.cs

---

## v003: Phase 3: Customer Portal Completion

**Date:** 2026-07-21
**Phase:** 3 of 10
**Status:** ✅ Complete

### Summary

Verified and polished all 8 customer portal pages to ensure they display real data from the API with proper error handling. The portal dashboard now loads real wishlist and addresses counts, and all track links pass order numbers for auto-loading.

### Changes

- **`portal/dashboard.html`**: Updated to load real wishlist count (`api.wishlist.get()`) and saved addresses count (`api.customers.me()`) in parallel with orders. Active order card now shows dynamic status progression based on actual order status. All track links pass order number via query param for auto-loading on the track page.

### Verification Results

All 8 portal pages verified clean:
- ✅ Auth guards present on all pages (`if(!Auth.isAuthed) location.href='../auth/login.html'`)
- ✅ No demo-user auto-seed remaining
- ✅ No localStorage cart references
- ✅ No hardcoded order numbers or customer data
- ✅ All pages call real API endpoints
- ✅ All pages handle network errors gracefully

### Portal Page Status

| Page | API Used | Status |
|---|---|---|
| `portal/dashboard.html` | `orders.mine()`, `wishlist.get()`, `customers.me()` | ✅ Real data |
| `portal/orders.html` | `orders.mine()` | ✅ Real data |
| `portal/track.html` | `orders.track(orderNumber)` | ✅ Real data, dynamic status |
| `portal/invoices.html` | `payments.invoices()` | ✅ Real data |
| `portal/payments.html` | `payments.history()` | ✅ Real data |
| `portal/wishlist.html` | `wishlist.get()` | ✅ Real data |
| `portal/addresses.html` | `customers.me()`, `customers.addAddress()`, `customers.removeAddress()` | ✅ Real CRUD |
| `portal/profile.html` | `customers.me()`, `customers.updateMe()`, `auth.changePassword()` | ✅ Real data |

### Build Status

- Backend: **0 errors, 2 warnings** (AutoMapper NU1903 advisory - deferred to Phase 9)
- Frontend: No build step

### What This Unlocks

- **Phase 4 (Admin Settings)**: Portal is complete; focus shifts to admin operational features
- Customers can now fully self-serve: browse, order, pay, track, manage profile and addresses

---

## v004: Phase 4: Admin Settings & Company Profile

**Date:** 2026-07-21
**Phase:** 4 of 10
**Status:** ✅ Complete

### Summary

Wired the admin settings and company profile pages to save real data to the backend. Settings now collects all form values per tab and sends them as key-value pairs to `PUT /settings`. Company profile uploads the logo to the server via `POST /files/upload` and saves all business details to settings.

### Changes

- **`admin/settings.html`**: Complete rewrite:
  - All input fields now have `id` attributes matching their settings keys (e.g., `s_business.name`)
  - Save button collects all fields from the current tab and sends them as a `Dictionary<string, string>` to `PUT /settings`
  - Handles checkboxes (serialized as `"true"`/`"false"`), selects, and text inputs
  - Loads real values from `GET /settings` on page load
  - New settings keys added: `payments.paystack_enabled`, `payments.bank_transfer_enabled`, `payments.pod_enabled`, `notification.*` (6 keys), `security.2fa_required`, `security.auto_lock`, `security.token_lifetime`
  - Proper error handling and button state management

- **`admin/company.html`**: Complete rewrite:
  - Loads current company details from `GET /settings` (name, motto, about, email, phone, instagram, tiktok, address, logo URL)
  - Logo upload: file is uploaded to `POST /files/upload` with category `"CompanyLogo"`, returned URL is saved to `business.logo_url`
  - All form fields saved via `PUT /settings` with keys: `business.name`, `business.motto`, `business.about`, `business.email`, `business.phone`, `business.instagram`, `business.tiktok`, `business.address`, `business.logo_url`
  - Live preview panel shows real values from form inputs
  - Logo removal clears the URL and updates the Brand object
  - Proper error handling for upload failures and save failures

### Files Modified

**Frontend (2 files):**
- `admin/settings.html`
- `admin/company.html`

**Backend:** No changes (existing `SettingsController` and `FilesController` already support the required operations)

### Build Status

- Backend: **0 errors, 2 warnings** (AutoMapper NU1903 advisory - deferred to Phase 9)
- Frontend: No build step

### What This Unlocks

- **Phase 5 (Supplier & Quotation Persistence)**: Admin operational features are now complete
- Business settings (delivery fee, VAT rate, payment methods) are now configurable and persist across sessions
- Company logo is uploaded to the server and visible to all users

---

## v005: Phase 5: Supplier & Quotation Persistence

**Date:** 2026-07-21
**Phase:** 5 of 10
**Status:** ✅ Complete

### Summary

Promoted `SuppliersController` and `QuotationsController` from in-memory `static List<>` stores to real EF Core aggregates with database persistence. Created domain entities, EF configurations, DbSets, and a migration. Data now survives server restarts.

### New Domain Entities

- **`Supplier`** (`DeChimex.Domain/Entities/Supplier.cs`): Aggregate root with Name, Contact, Email, Phone, Category, Status. Supports Create, Update, Activate, Deactivate. Extends `AggregateRoot` (audit fields + soft delete).

- **`Quotation`** (`DeChimex.Domain/Entities/Quotation.cs`): Aggregate root with QuoteNumber, CustomerName, Total (Money), Status, ValidUntil, Notes. Supports Create, Update, Send, Accept, Expire, Cancel. Owns `QuotationLine` children. Auto-recalculates total when lines change.

- **`QuotationLine`** (`DeChimex.Domain/Entities/Quotation.cs`): Child entity with Name, Qty, UnitPrice, LineTotal (computed), DisplayOrder.

### EF Configurations

- **`SupplierConfiguration`**: Table "Suppliers", max lengths, soft delete query filter
- **`QuotationConfiguration`**: Table "Quotations", unique index on QuoteNumber (filtered), Money value object mapping, cascade delete on lines, status index, soft delete query filter
- **`QuotationLineConfiguration`**: Table "QuotationLines", decimal precision, index on QuotationId

### Database Migration

- **`20260721114153_AddSuppliersAndQuotations`**: Creates Suppliers, Quotations, QuotationLines tables with all columns, indexes, and foreign key relationships

### Controller Updates

- **`SuppliersController`**: Rewritten to use `DeChimexDbContext` and `IUnitOfWork`. Extends `ApiControllerBase` for standard response envelope. CRUD operations persist to database. Soft delete via `MarkDeleted(CurrentUser.Email)`.

- **`QuotationsController`**: Rewritten to use `DeChimexDbContext` and `IUnitOfWork`. Full workflow: search (paged), get, create, update, send, convert-to-order, delete. All operations persist to database. Line items managed via `AddLine()`/`RemoveLine()` with auto-recalculation.

### DbContext

- Added `DbSet<Supplier>`, `DbSet<Quotation>`, `DbSet<QuotationLine>` to `DeChimexDbContext`

### Files Changed

**New files (5):**
- `src/DeChimex.Domain/Entities/Supplier.cs`
- `src/DeChimex.Domain/Entities/Quotation.cs` (includes QuotationLine)
- `src/DeChimex.Persistence/Configurations/ProcurementConfigurations.cs`
- `src/DeChimex.Persistence/Migrations/20260721114153_AddSuppliersAndQuotations.cs`
- `src/DeChimex.Persistence/Migrations/20260721114153_AddSuppliersAndQuotations.Designer.cs`

**Modified files (3):**
- `src/DeChimex.Api/Controllers/SuppliersController.cs`
- `src/DeChimex.Api/Controllers/QuotationsController.cs`
- `src/DeChimex.Persistence/Context/DeChimexDbContext.cs`

### Build Status

- Backend: **0 errors, 2 warnings** (AutoMapper NU1903 advisory - deferred to Phase 9)
- Frontend: No changes (existing UI already works against these endpoints)

### What This Unlocks

- **Phase 6 (Product Image Upload)**: All operational data now persists; focus shifts to catalog polish
- Suppliers and quotations survive server restarts
- Quotation → Order conversion flow has a real data foundation
