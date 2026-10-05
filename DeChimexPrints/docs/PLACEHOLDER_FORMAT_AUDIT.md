# Form Placeholder & Generated String Format Audit — 2026-10-04

**Method:** every `placeholder=` in `DeChimex.Host/Views/**/*.cshtml` (96) and PWA/JS-injected inputs was
traced to its binding target → validator → generation/query implementation. Real formats below are taken
from the actual code and seeded/test data, not conventions.

**Source-of-truth formats (traced):**

| Identifier | Generator | Real format | Evidence |
|---|---|---|---|
| Order number | `Persistence/Repositories/ReferenceGenerator.cs:16` | `DCX-{year}-{D6}` → `DCX-2026-000042` | also tests: `DCX-2026-000123` (EmailTemplateRendererTests) |
| Invoice number (invoices **and** quotes) | `ReferenceGenerator.cs:23` | `INV-{year}-{D6}` → `INV-2026-000042` | quotes reuse INV numbering deliberately (quote invoices have no order) |
| Procurement ref | `ReferenceGenerator.cs:31` | `PRC-{year}-{D6}` | admin list search only |
| Receipt number | `ReferenceGenerator.cs:37` | `RCT-{year}-{D6}` | display only, no input |
| Payment reference | `ReferenceGenerator.cs:42` | `PAY-{yyyyMMddHHmmss}-{10 upper-hex}` → `PAY-20261004123456-8F3A2C91D0` | tests: `PAY-20261231235959-ABCDEF1234` |
| Product SKU | `DeChimex.Shared/Constants/ProductSkuGenerator.cs:22` | `^[A-Z0-9]{3}-[A-Z0-9]{5}$` (3-char name prefix + 5-char code) | `product-sku.js` mirrors it client-side |
| Email-verification token | `AccountController.VerifyEmailPostAsync` | 6-digit OTP **or** the full emailed link (link is parsed for `token`/`email`) | Brevo template HTML carries 6 digits |
| Password (all forms) | `Register.cs`/`PasswordManagement.cs`/`UserManagementFeature.cs` | min 8, must contain `A-Z`, `a-z`, `0-9` | FluentValidation rules |
| Username | `Domain/ValueObjects/Username.cs` | 3–30, letters/digits/`._-`, starts+ends alnum | staff create form hint matches |
| Phone | `Domain/ValueObjects/PhoneNumber.cs:26` | `^\+?[0-9\s\-()]{7,20}$` | `+234…` hints valid |
| WhatsApp setting | `DeChimex.Shared/WhatsApp.cs` | digits only (accepts raw, `+234…`, local `0…`, or a wa.me link; normalized) | admin settings hint fine |

**Search behavior (traced):**

| Search box | Backed by | Matches |
|---|---|---|
| `/portal/orders/track` | `OperationsQueries.GetByNumberAsync` | **exact** order number (trimmed) |
| Admin Orders `search` | `OperationsQueries.cs:104` | **OrderNumber only** |
| Admin Sales inbox `search` | `SalesQueries.cs` (inbox) | OrderNumber **+ customer name** |
| Admin Invoices `search` | `AdminInvoicesController.cs:58` | InvoiceNumber **+ customer name** + OrderNumber |
| Admin Customers | name + email + phone | Admin Users: first/last/email |
| Admin Messages (contact) | `ContactMessageQueries.cs` | name + email + phone |
| Storefront product search | `CatalogQueries.cs:59` | product name + **SKU** + description |
| Admin Inventory | product name only · Categories: name · Activity: Title/Summary (+ dedicated order-number filter) · Audit raw: Entity **exact**, Action/Actor contains · Dev logs: message/exception/route/source/correlation |

## Audit table (user-facing inputs; grouped by finding)

| Field / Screen | Current Placeholder | Actual Expected/Generated Format | Evidence/Source | Status | Recommended Placeholder |
|---|---|---|---|---|---|
| Track order · `PortalOrders/Track.cshtml:23` | `e.g. ORD-2481` | `DCX-2026-000042` (exact, trimmed) | ReferenceGenerator.cs:20; exact-match query | ❌ Incorrect | `e.g. DCX-2026-000042` |
| Dev maintenance "Payment reference" · `Dev/Maintenance.cshtml:15` | `e.g. DCX-PAY-…` | `PAY-20261004123456-8F3A2C91D0` — nothing generates `DCX-PAY-` | ReferenceGenerator.cs:42; `p.Reference ==` exact lookup | ❌ Incorrect | `e.g. PAY-20261004123456-8F3A2C91D0` |
| Admin orders list · `AdminOrders/Index.cshtml:9` | `Search order number / customer...` | order number **only** (no customer match) | OperationsQueries.cs:103–104 | ⚠️ Misleading | `Search order number...` |
| Register password · `Account/Register.cshtml:48` | `At least 8 characters` | 8+ **and** uppercase + lowercase + digit required | Register.cs validator | ⚠️ Misleading (by omission) | `At least 8 characters, incl. A-Z, a-z & 0-9` |
| Staff create password · `AdminUsers/Create.cshtml:31` | *(none)* | same policy as Register | UserManagementFeature.cs:34–37 | ℹ️ Could be clearer | add `At least 8 characters, incl. A-Z, a-z & 0-9` |
| Contact email · `StorePages/Contact.cshtml:60` | `you@email.com` | valid email; 5 other forms use `you@example.com` | Login/Register/Verify/Forgot | ℹ️ Inconsistent | `you@example.com` |
| Admin invoices list · `AdminInvoices/Index.cshtml:14` | `Search invoice / order number...` | also matches **customer name** | AdminInvoicesController.cs:58–60 | ℹ️ Incomplete | `Search invoice, order number or customer...` |
| Audit raw "Action contains" · `AdminAudit/Raw.cshtml:24` | `e.g. Update, Create` | stored lowercase `create` / `update` / `delete` (contains, SQL CI) | AuditableSoftDeleteInterceptor.cs:65–71 | ℹ️ Could be clearer | `e.g. create, update` |
| Business activity "Order number" filter · `AdminActivity/Index.cshtml:22` | ~~*(none)*~~ → `e.g. DCX-2026-000042` | `DCX-2026-000042` (contains) | ActivityQueries.cs:14 | ✅ Fixed (Task 7) | `e.g. DCX-2026-000042` |
| Category option "Show only if … equals value" · `AdminCategories/Options.cshtml:63` | `equals value…` | the parent answer to match (case-insensitive; typically a choice like `Yes`) | CategoryOption.cs:80–82 | ℹ️ Could be clearer | `e.g. Yes` |
| Procurement "Linked order" · `AdminProcurement/Create.cshtml:14` | ~~`Order id (guid)`~~ → `e.g. DCX-2026-000042` | business order number `DCX-2026-000042` (backend resolver added in Task 7) | AdminProcurementController.cs (number→GUID resolver) | ✅ Fixed (Task 7) | `e.g. DCX-2026-000042` |

## ✅ Verified correct (no change)

Login/Verify/Forgot emails (`you@example.com` = valid format) · passwords `Your password` / `Re-enter password` / reset hints ·
phones `+234…` (Register, Contact — matches PhoneRegex) · names `Jane`/`Doe`/`Jane Doe` · addresses (`House number, street, area`,
`Nepa Road…`/`Awe Crescent…` examples, `e.g. Home, Office` label) · chat `Reply to the customer…`/`Type a message…` and
`What should we change?` (identical in `order-workspace.js:451` and `PortalOrders/Workspace.cshtml:160`) ·
custom-order options `Type your answer here…` · notes fields (`Note (optional)`, `Reason (optional)`, `Reference / note (optional)`,
`e.g. deposit expected before production...`, `e.g. cash received at the counter, transfer reference...` — all free-text) ·
verify-email `Paste the token or the full link from your email` (matches the real link-parsing path) ·
`Search by name, email or phone...` (orders/invoices customer pickers) · `Search customers...` · `Search name, email or phone...` (messages) ·
`Search users...` · `Search categories...` · `Search products...` / `Search product...` / storefront `Search mugs, shirts, flyers, gifts…`
(name+SKU+description backend) · SKU `Automatically suggested` (server suggests; 9-char format enforced) ·
gallery `https://... or /uploads/...` / `e.g. Branding, Large format` · marketing subject/HTML examples (HTML emails really sent) ·
activity `Username or System` (actor is username or literal `System`) / `Description or order number` (summaries embed the order number) /
`e.g. payment.succeeded` (real type slug) · audit `e.g. Order, Product, Invoice` (exact CLR entity names) · `email or system` (stored `email…`/`system`) ·
dev logs `message, exception, route, source` (real columns) · admin settings examples = seeded/approved business values (stats `2,400+`/`180+`/`12+`/`48h`,
`NGN`, `1500`, WhatsApp `2349062051934` digits form) · checkout `House number, street, area` + notes · product reviews.

## 🔧 Implementation inconsistencies — all FIXED (Task 7, same session)

1. **Procurement linked order used the raw GUID** — ✅ FIXED. The form now takes the business order
   number (`e.g. DCX-2026-000042`); `AdminProcurementController` resolves `DCX-…` → the order (friendly
   validation error `No order with the number X was found.` for unknown numbers); the procurement
   read-model (`ProcurementDto.SourceOrderNumber`) resolves the linked order's DCX number and
   `Details.cshtml` links it to `/admin/orders/{id}`. Verified by E2E stage 8f (error + success + link).
2. **Quotes shared the INV- invoice numbering** — ✅ FIXED. Quotes now draw from a dedicated
   `QTE-{year}-{D6}` sequence (`NextQuoteNumberAsync`); the INV- counter excludes `QTE-` documents.
   When a quote is converted to an order, `CreateOrder` adoption **renumbers** the adopted document
   onto a fresh `INV-{year}-{D6}` (the paid final invoice no longer carries a quote-era number).
   Verified by `ReferenceConsistencyTests` (4 facts) + E2E stage 8f (quote → list → conversion → renumber).
3. **`wwwroot/js/ui.js` dead global-search prototype** — ✅ FIXED. File deleted; unreferenced by any
   view. Verified: `/js/ui.js` returns 404 (E2E stage 8f check 17) and static-file regression suites stay green.
4. **Activity search vs "Order number" filter** — ✅ IMPROVED. `ActivityQueries` free-text search now
   also matches the linked order number (not only summaries), and the "Order number" filter placeholder
   was added (`e.g. DCX-2026-000042`). Verified by E2E stage 8f check 16.

**Additional production defect found & fixed during Task 7 final-run hardening:** `PwaWebPushDispatcher`
fired push notifications on a background thread through the request-scoped `DbContext` (fire-and-forget
`Task.Run`), causing intermittent `InvalidOperationException` / HTTP 500 on order submit under parallel
use. It now creates its own DI scope (`IServiceScopeFactory`) per dispatch and resolves its repositories
from that scope. Root cause captured via detailed test logging; full suite green (897/897) and no 500s
in the clean final run.

**UI defect found & fixed during Task 7 E2E hardening:** the new-customer row on the admin quote and
order forms carried a `hidden` CSS class (`.hidden { display:none !important }`) that overrode the
customer-mode toggle's inline style — selecting "New customer" appeared to do nothing. The class was
removed from `newCustomerRow` in `AdminInvoices/New.cshtml` and `AdminOrders/New.cshtml` (the inline
`style="display:none"` + JS toggle remain); toggling now reveals the fields on both forms.
