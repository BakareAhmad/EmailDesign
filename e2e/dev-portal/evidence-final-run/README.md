# Developer Portal — final E2E evidence (single clean run)

Produced by `e2e/dev-portal/run_final.sh devfinal` in one run: full test suite → app with Paystack + Brevo stubs → HTTP smoke → checkout regression journey (real payments) → developer-portal journey → secret-leak sweep. Nothing from earlier or dry runs is mixed in.

| Log | Result |
|---|---|
| final-test-log.txt | 829/829 tests passed (454 unit + 375 integration) |
| final-health-log.txt | public /health Healthy (payload unchanged) |
| final-dev-smoke-log.txt | 29/29 — every /dev page 200 for the developer; /admin + /portal denied |
| final-checkout-regression-log.txt | 60/60 — customer/admin/checkout/payment/refund behaviour unchanged |
| final-dev-portal-e2e-log.txt | 107/107 — the developer-portal journey |
| final-service-fee-export.csv | the CSV exported in the run (no customer PII) |

## Screenshots (brief §44 order)

- `01-developer-login.png` — Developer login (seeded account)
- `02-developer-login-invalid-password.png` — Validation: wrong password rejected
- `03-dashboard.png` — Dashboard — status tiles (click-through), 24 h counts, recent errors, jobs, fees
- `04-health.png` — System health — uptime, env, version, runtime, checks, DB/migrations, storage, realtime, pipeline counters
- `05-technical-log.png` — Technical log
- `06-technical-log-filtered-email-failures.png` — Log filtered: severity ≥ Warning + category Email → the Brevo outage error
- `07-technical-log-event-detail.png` — Log event detail — correlation, route, environment, redacted metadata
- `08-error-center.png` — Error center — grouped, occurrences, first/last seen
- `09-error-group-occurrences.png` — Error group drill-down — individual occurrences
- `10-statistics-performance.png` — Statistics & performance (24 h)
- `11-integrations.png` — Integrations — Paystack & Brevo config status, reachability, failures, last success
- `12-payment-diagnostics.png` — Payment diagnostics — duplicate/mismatch/rejected webhooks, verify, refunds
- `13-email-diagnostics.png` — Email diagnostics — counts by template/recipient category, provider errors (no contents)
- `14-service-fees-overview.png` — Service fees — totals by period, previous periods, percentages used, rules
- `15-service-fee-ledger.png` — Service-fee ledger — 50 % and 100 % rows, reversed refund, reconciliation
- `16-service-fee-transaction-detail.png` — Service-fee transaction detail — historical 50 % kept after config returned to 100 %
- `17-service-fees-invalid-range.png` — Validation: inverted custom date range
- `18-background-jobs.png` — Background jobs
- `19-database-migrations.png` — Database & migrations (read-only)
- `20-configuration-overview.png` — Configuration overview — statuses only, no values
- `21-maintenance-tools.png` — Maintenance — the five safe tools
- `22-maintenance-confirmation-required.png` — Validation: server refuses maintenance without confirmation
- `23-maintenance-success-and-audit.png` — Maintenance success (clear cache ×2 idempotent, re-run health, no-op re-verify) + audit
- `24-maintenance-reverify-unknown-reference.png` — Validation: re-verify with unknown reference
- `25-dashboard-paystack-unavailable.png` — Paystack outage → dashboard Unavailable (overall escalates)
- `26-integrations-paystack-unreachable.png` — Integrations during the outage
- `27-developer-password-validation.png` — Validation: developer password change with mismatched confirmation
- `28-access-denied-developer-admin-settings.png` — Access denial: developer → /admin/settings
- `29-access-denied-customer-dev-portal.png` — Access denial: customer → /dev
- `30-access-denied-owner-without-grant.png` — Access denial: owner without explicit dev grant → /dev

Key values are never shown in evidence; the leak sweep checked every file.
