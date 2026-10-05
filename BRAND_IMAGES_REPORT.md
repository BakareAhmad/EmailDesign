# Brand images + responsive layout — 2026-09-26

The owner supplied the three brand files the site was missing (see AI_HANDOFF §89). They are now installed, wired into every layout, and the site was checked in a real browser at every common screen width. That sweep found and fixed several layout problems on small phones in the admin and developer portals.

## 1. Files installed (`DeChimexPrints/src/DeChimex.Host/wwwroot/`)

| Upload | Installed as | Size | Used for |
|---|---|---|---|
| `image-2.jpeg` (1536×1024) | `img/hero.jpg` (original, byte-identical) | 330 KB | home hero, tablets/desktops (JPEG fallback) |
| — derived | `img/hero.webp` / `img/hero-768.webp` / `img/hero-768.jpg` | 80 / 28 / 47 KB | lighter variants the browser picks by screen width and pixel density |
| `image-1.png` (800×1093 portrait) | `img/hero-mobile.png` | 250 KB | home hero on phones ≤720 px (PNG fallback) |
| — derived | `img/hero-mobile.webp` | 59 KB | phones (what modern browsers actually download) |
| `image-2.png` (logo mark, black on transparent) | `img/logo.png` (trimmed, 256×256, still transparent) | 30 KB | headers, footer, auth pages, admin, invoices/receipts |
| — derived | `favicon.ico` (16–64 px), `img/favicon-32.png`, `img/apple-touch-icon.png` (180), `img/icon-192.png`, `img/icon-512.png`, `site.webmanifest` | 2–37 KB | browser tab, bookmarks, phone home screen |

The logo mark is black. Everywhere it appears it sits on a white badge, so it stays visible on the dark admin sidebar. The favicons draw it on a white disc so it also shows in dark browser tabs. iOS home-screen icons use an opaque white square, because iOS paints transparent areas black.

## 2. Where the images appear

- **Home hero** (`Views/StoreHome/Index.cshtml`): a `<picture>` element.
  - ≤720 px: the portrait art (WebP, falling back to PNG).
  - Above 720 px: the landscape art, with `srcset` offering 768 w and 1536 w (WebP, falling back to JPEG).
  - `sizes` matches the real rendered width per breakpoint, so a 1× laptop downloads the 28 KB file and a 2× screen the full-size one.
  - The picture is shown **whole** (no cropping) at its own aspect ratio. Width/height attributes reserve the space, so there's no layout shift.
  - The image now has descriptive alt text.
  - The product-mosaic fallback still renders if the files are ever removed.
- **Customer header and footer, auth pages, error pages:** the real logo replaces the "DC" text badge. `CustomerShellService` still checks the file exists and falls back to "DC" when it's missing.
- **Admin:** sidebar logo, mobile top-bar logo and the off-canvas menu.
- **Legacy `_Layout`:** logo on a white badge instead of the gradient "DC" tile.
- **Invoice / receipt documents** (admin view, public share page, print): 44 px logo beside the business name.
- **Favicon / icons:** a new `Views/Shared/_BrandHead.cshtml` partial, included in the customer, auth, admin, legacy and developer layouts, the public invoice/receipt pages and the unsubscribe page. The middleware error page links `/favicon.ico`.
- Not changed: emails. Images in emails need a public absolute URL and are blocked by many mail clients, so the text header was kept.

## 3. Responsive problems found and fixed

A Playwright sweep measured `scrollWidth` against the viewport on every page, at 320/360/375/390/414/768/1024 px. Customer pages were already clean. These pages were not:

| Where | Problem at small widths | Fix |
|---|---|---|
| Admin top bar (all admin pages) | 320 px: logo + wordmark + title + avatar + Sign out was 3 px too wide; the page title slid under the avatar | ≤480 px: keep the logo badge and drop the wordmark; the title truncates with an ellipsis |
| Admin list toolbars (products, users, customers, messages, categories, orders…) | the search box had `min-width:220px`, so the Search button ran off-screen (378 px page at 320/360) | ≤640 px: the filter form takes the full row. A plain search form keeps box + button on one line (the box shrinks); forms with extra filters wrap. |
| Admin dashboard | inline two-column grid never stacked (358 px page at 320) | ≤640 px: inline `1fr 1fr` grids become one column |
| Admin analytics | `minmax(320px,1fr)` grid is wider than a 320 px screen | ≤480 px: auto-fit grids become one column |
| Admin activity / categories | rows of buttons didn't wrap (384–388 px) | ≤640 px: button rows wrap |
| Admin tables with real data (dashboard, reports, order/customer/invoice detail) | an empty-database sweep looked clean, but with real orders the tables' natural width pushed the dashboard to 548 px at 320 | ≤1024 px: any block holding a table scrolls sideways inside itself, and grid cells may shrink (`min-width:0`) instead of stretching to the table |
| Admin two-column cards (`.g` grids with fixed columns, e.g. `1.25fr 1fr`) | the cards stayed side by side on phones and tablets in portrait | ≤860 px: fixed-column `.g` grids stack into one column (auto-fit grids are left alone) |
| Shared invoice / receipt pages (`/i/…`, `/r/…`) | the "Copy link" and "Download PDF" links were dark blue on the dark bar (unreadable at every width); at 320 the bar was cramped and the Amount column was cut off | links are now white outlined buttons; ≤560 px: the bar wraps, the document padding and table cell padding shrink, so the line items fit at 320 |
| Developer portal tables (health, email, dashboard, performance, payments, service fees, log detail, DB, maintenance) | 17 tables weren't in a scroll container; `/dev/health` was 1114 px wide even at 768 | each wrapped in the existing `.dv-table-wrap` (scrolls inside its card) |
| Developer portal range picker | the custom date range didn't wrap (424 px) | wraps under the preset chips |
| Developer portal navigation on phones | the 240 px sidebar stacked full-height (~850 px) above every page | ≤760 px: compact header. The nav is a horizontal scroll strip with the current page scrolled into view; account links sit in one row. Desktop layout unchanged. |

All fixes are CSS media queries (`app.css`, `dev.css`, `customer.css`, the `<style>` blocks of `_InvoiceDocument`, `_ReceiptDocument` and the two public invoice/receipt pages) plus the table wrappers and 5 lines in `dev-portal.js`. Nothing changes above the stated widths.

## 4. Tests

- New `IntegrationTests/Controllers/BrandAssetsTests.cs` (20 cases):
  - hero `<picture>` markup: sources, order, `sizes`, intrinsic size, no mosaic;
  - logo + favicon + manifest on customer, auth and error pages;
  - PNG dimensions and content types of every brand file;
  - manifest icons resolve;
  - `CustomerShellService` returns null (so views fall back) when files are missing;
  - `Srcset` helper.
- `QuoteInvoiceReceiptTests`: the shared invoice and receipt pages carry the logo and favicon.

## 5. End-to-end proof (`e2e/brand/brand_e2e.py`)

One clean run of `bash e2e/brand/run_final.sh`. The results are in `e2e/brand/evidence-final-run/` (`final-*-log.txt` and README.md; it was `evidence/` until the next task became primary).

- **Assets:** every brand file is served with the right type, and the manifest icons resolve.
- **Home at 10 widths (320 → 1920):**
  - no horizontal overflow;
  - portrait art ≤720 px, landscape above;
  - hero shown whole: the rendered ratio equals the art's ratio within 1.5%, so nothing is cropped or stretched;
  - inside the viewport and decoded;
  - header logo loaded at 38×38 inside the viewport.
- **Pixel density:** the browser picks the right file at DPR 1/2/3 (phone → `hero-mobile.webp`, 1× laptop → `hero-768.webp`, 2× desktop/tablet → `hero.webp`).
- **Footer, login and 404:** footer logo; login at 320/1440, including the invalid-credentials error state; 404 page.
- **Signed-in customer portal:** 4 pages at 320/768/1440.
- **Admin:**
  - all 22 sections at 320 and 768 px;
  - search box + button on one row;
  - title no longer under the avatar;
  - sidebar, top-bar and off-canvas logos.
- **Admin detail pages with real data:** order, customer and invoice detail at 320 px with no overflow.
- **Shared invoice** (created by the checkout regression): logo in the document header at 320/1440; the line items fit inside the document at 320 (nothing clipped); the bar links are readable (white).
- **Developer portal:**
  - all 15 pages at 375 and 1024 px;
  - mobile nav strip with the active item visible;
  - desktop sidebar unchanged.
- **Favicon** link resolves on every layout.

Regressions run in the same session: full test suite, `/health`, developer smoke, checkout, design review, fixes journey and developer portal. The fixes journey's screenshots from this run are in `e2e/fixes/evidence-final-run/`.

## 6. Final verified numbers (single clean run, 2026-09-26)

| Stage | Result |
|---|---|
| Full test suite | 867/867 (459 unit + 408 integration) |
| Public `/health` | Healthy |
| Developer smoke | 29/29 |
| Checkout regression | 60/60 |
| Brand images + responsive journey | 245/245 |
| Design-review regression | 36/36 |
| Fixes journey | 39/39 |
| Developer-portal regression | 107/107 |
| App errors / secret leaks | 0 / clean |

22 screenshots in `e2e/brand/evidence-final-run/` (index in its README.md); 13 fixes-journey shots in `e2e/fixes/evidence-final-run/`.
