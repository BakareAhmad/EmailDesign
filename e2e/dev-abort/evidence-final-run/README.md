# Evidence — Developer Portal sign-in / dropped requests + blank-404/400 fix (2026-09-26)

Produced by ONE clean run of `bash e2e/dev-abort/run_final.sh` (TAG `final`). Nothing from any other run is mixed in.
Details: `DEV_SIGNIN_ABORT_REPORT.md`.

| Stage | Result |
|---|---|
| Full test suite | 879/879 (459 unit + 420 integration) — `final-test-log.txt` |
| Public `/health` | Healthy — `final-health-log.txt` |
| Developer smoke | 29/29 — `final-dev-smoke-log.txt` |
| Checkout regression | 60/60 — `final-checkout-regression-log.txt` |
| Brand images + responsive (regression) | 245/245 — `final-brand-e2e-log.txt` |
| Design-review regression | 36/36 — `final-design-review-regression-log.txt` |
| Fixes journey (regression) | 39/39 — `final-fixes-e2e-log.txt` |
| Developer-portal regression | 107/107 — `final-dev-portal-regression-log.txt` |
| **Dev sign-in + dropped requests + blank-404/400** | **37/37 — `final-dev-abort-e2e-log.txt`** |
| App unhandled exceptions / secret-leak sweep | 0 / clean — `final-app-log-summary.txt` |

| Screenshot | Shows |
|---|---|
| 01-developer-sign-in-page | sign-in form |
| 02-sign-in-empty-fields-validation | validation: empty email + password refused by the server ("Email is required", "Password is required") |
| 03-sign-in-wrong-password-error | error state: "Invalid email or password", no portal access |
| 04-dashboard-after-sign-in | correct sign-in lands on the rendered dashboard, no cancellation noise in the log |
| 05-dashboard-after-double-submit-and-reload | Sign in clicked twice plus a reload mid-load: still the dashboard, no exception |
| 06-dashboard-after-abort-storm | after 120 requests were reset mid-flight: everything still Healthy |
| 07-technical-log-no-cancellation-errors | technical log (Error filter): no OperationCanceled entries |
| 08 / 09-admin-missing-customer-*-styled-404 | missing customer (details / edit): styled 404 with the "Admin dashboard" link (was a blank page) |
| 10-admin-form-without-antiforgery-token-styled-400 | form posted without its anti-forgery token: styled 400 (was blank), nothing created |
