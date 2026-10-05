# Evidence — Provider keys re-inserted (2026-09-25, repo v047-CustomerUXRedesign)

All files come from ONE clean final run: `bash e2e/provider-keys/run_final.sh keysfinal`
(fresh in-memory apps, fresh stubs, fresh evidence folder). Key values are never logged.

| Stage | Result | File |
|---|---|---|
| Full test suite | 748/748 (418 unit + 330 integration) | final-test-log.txt |
| Packaging-guard validations | 14/14 | final-packaging-guard-log.txt |
| /health (app A, REAL api.paystack.co) | Healthy — database, paystack "HTTP 200", brevo (stub) | final-health-log.txt |
| Keys + design-review journey (app A) | 36/36 | final-e2e-log.txt, shots 01–16 |
| Brevo api-key header on every outbound send | 4/4 match configured key | final-brevo-key-audit-log.txt |
| Direct-checkout / Paystack journey (app B, Paystack stub) | 60/60 | final-checkout-e2e-log.txt, shots 17–37 |
| Paystack Authorization header (initialize ×4, refund ×1) | all match configured secret | final-paystack-auth-log.txt |
| Secret-leak sweep over evidence/ | clean (only the PUBLIC pk_test_ key appears, shot 01) | — |

## Screenshots
- 01 `/api/v1/config` serves the Paystack PUBLIC key, no secrets
- 02 owner signed in · 03 customer registered → Brevo OTP → verified → signed in
- 04–05 empty customer/staff workspaces · 06–07 v1 upload, staff sees it live
- 08–09 approve → customer sees approval live · 10 v2 live (independent history)
- 11 VALIDATION: reject requires a reason · 12–13 rejected with reason, customer sees it live
- 14 VALIDATION: `.exe` refused (400 "File type '.exe' is not allowed")
- 15–16 history persists after reload (customer + staff)
- 17–21 catalog filter, direct vs custom buy actions, cart guard rejects custom product, checkout amounts
- 22–23 order placed → Paystack → paid · 24–27 workspace, admin badge, activity log, lifetime spend
- 28–29 review unlocked after purchase / locked when unpurchased
- 30 VALIDATION: payment failed state · 31 retry paid · 32 cancelled · 33 refunded
- 34–36 custom order: assigned workspace, admin badge, paid · 37 My orders coherent

Previous evidence set (customer UX redesign, 111 shots) → `e2e/customer-redesign/evidence/`.
