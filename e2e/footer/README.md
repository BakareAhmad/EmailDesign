# Footer placement harness

`footer_e2e.py` verifies that the footer is a **layout decision** in this app:

| Page group | Layout | Footer expected |
|---|---|---|
| Public storefront (home, shop, product, services, gallery, about, contact, faq, privacy, terms, install-help) | `_CustomerLayout` / `_PublicLayout` | full branded footer |
| Customer shopping (`/portal/shop`, `/portal/shop/{id}`) | `_CustomerLayout` | full branded footer |
| Customer app + transactional (`/portal/**`, `/portal/shop/{id}/order`, cart, checkout, confirmation, payment return) | `_PortalLayout` | slim legal bar |
| Admin / staff (`/admin/**` incl. order workspace) | `_AdminLayout` | none |
| Auth (login, register, forgot/reset password, access denied) | `_AuthLayout` | none |
| Developer portal | `_DevLayout` | none |

Beyond placement it also checks: footer content preserved (brand, motto, Shop/Help/Talk
to us columns, socials, WhatsApp, studio/pickup addresses, legal lines, policy links,
category links), footer links navigate, the slim bar keeps copyright/policies/payment
line, no leftover footer spacing or footer-shaped empty band on footer-less pages, no
horizontal overflow at 320 / 390 / 1440 px, the 4-column to 1-column responsive footer,
customer app navigation still present, and a clean console/network on every page visited.

## Running

```bash
# standalone (app + stubs must be running; see e2e/design-review helpers)
E2E_SHOTS_DIR=/tmp/footer-shots python3 e2e/footer/footer_e2e.py mytag

# one clean final run -> evidence/ at the repo root (footer shots are primary;
# brand, checkout, design-review, fixes, dev-portal, dev-abort, PWA, placeholder
# and impl-fixes journeys run as regressions)
bash e2e/footer/run_final.sh footerfinal

# audit mode: record CURRENT placement without asserting (for before/after evidence)
FOOTER_MODE=audit E2E_SHOTS_DIR=e2e/footer/evidence-before \
  python3 e2e/footer/footer_e2e.py before
```

## Before / after evidence

- `evidence-before/` — 26 shots + `audit-before-log.txt` from the PREVIOUS build: every
  customer-facing page (workspace, cart, checkout, confirmation, payment return, order
  workspace) rendered the full storefront footer.
- The after set is the 28 ordered shots in the repo-root `evidence/` folder, produced by
  the single final run of `run_final.sh` (404/404 footer checks).
