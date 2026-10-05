# Evidence — brand images + responsive layout (2026-09-26)

Produced by ONE clean run of `bash e2e/brand/run_final.sh` (TAG `final`). No shots from any other run are mixed in.

| Stage | Result |
|---|---|
| Full test suite | 867/867 (459 unit + 408 integration) — `final-test-log.txt` |
| Public `/health` | Healthy (database, Paystack, Brevo) — `final-health-log.txt` |
| Developer smoke | 29/29 — `final-dev-smoke-log.txt` |
| Checkout regression (real payments) | 60/60 — `final-checkout-regression-log.txt` |
| **Brand images + responsive journey** | **245/245** — `final-brand-e2e-log.txt` |
| Design-review regression | 36/36 — `final-design-review-regression-log.txt` |
| Fixes journey | 39/39 — `final-fixes-e2e-log.txt` (shots in `e2e/fixes/evidence-final-run/`) |
| Developer-portal regression | 107/107 — `final-dev-portal-regression-log.txt` |
| App log unhandled exceptions | 0 — `final-app-log-summary.txt` |
| Secret-leak sweep over all evidence | clean |

## Screenshots

| File | Shows |
|---|---|
| 01–10 `home-hero-<width>px` | home page at 320, 360, 375, 390, 414, 768, 1024, 1280, 1440, 1920 px — portrait art on phones, landscape above 720 px, whole image, no sideways scroll, header logo |
| 11 `footer-logo-375px` | footer logo on a phone |
| 12 `login-invalid-credentials-320px` | validation/error state: wrong password on the login page, brand logo at 320 px |
| 13 `login-1440px` | login page on desktop |
| 14 `error-404-360px` | 404 page with logo |
| 15 / 16 `customer-portal-*` | signed-in customer portal at 320 and 1440 px |
| 17 `admin-dashboard-sidebar-logo-1440px` | admin sidebar logo, real data |
| 18 `admin-dashboard-topbar-logo-320px` | admin top bar at 320 px — logo, title not under the avatar, tables fit |
| 19 `admin-menu-open-logo-320px` | admin off-canvas menu with logo |
| 20 / 21 `shared-invoice-logo-*` | public invoice at 320 and 1440 px — logo in document, readable bar links, line items fit |
| 22 `developer-portal-email-375px` | developer portal on a phone — compact nav strip, table scrolls in its card |
