# Final evidence — email design system (run `emailfinal`, 2026-10-05)

Single clean run: `bash e2e/email-ux/run_final.sh emailfinal` → **FINAL RUN PASSED — 44
screenshots in /home/user/ContinualDevelopment/evidence**. The email journey
(`e2e/email-ux/email_e2e.py`, stage 8h) is the primary evidence; every other journey ran as a
regression in the same run.

## Stage results

| Stage | Result |
|---|---|
| 1. full test suite (sequential, -m:1) | 467 unit + 438 integration = **905/905** |
| 2. app + stubs + `/health` | Healthy (database, paystack, brevo, runtime) |
| 3. developer smoke | 29/29 |
| 4. checkout regression | 60/60 |
| 5. brand images + responsive | 282/282 |
| 6. design-review regression | green (`final-design-review-regression-log.txt`) |
| 7. fixes journey | 39/39 |
| 8. developer-portal | 108/108 |
| 8b. dev sign-in + dropped requests | 37/37 |
| 8d. PWA + mobile | 27/27 |
| 8e. placeholder audit | 18/18 |
| 8f. implementation fixes | 17/17 |
| 8g. footer placement | 404/404 |
| **8h. email design system** | **636 passed, 0 failed** |
| 9. app log + leak sweep | 0 unhandled exceptions, clean |

Email journey detail (`final-email-e2e-log.txt`): all 19 rendered templates audited against
the app palette (A), every live email captured by the Brevo stub during the run audited with
its plain-text part and CTA links (B), live drives for the templates the other journeys do not
reach (C), and the screenshot pass (D).

## Shot index (44)

- `01-Welcome-…` … `17-ProviderTestEmail-…`, `98-Marketing-…`, `99-OrderConfirmation-Minimal-…`
  at `-desktop` (1280) and `-mobile` (390), rendered from `email-previews/` in this run.
- `02-EmailVerification-dark.png`, `06-OrderConfirmation-dark.png` — dark-scheme 390.
- `06-OrderConfirmation-320px.png`, `16-ContactReply-320px.png` — narrow pass.
- `app-reference-home.png`, `app-reference-auth.png` — the app surfaces the emails mirror.

## Before / after

- `e2e/email-ux/evidence-before/` — the retired email language: the audit's captured live-send
  HTML plus fresh 1280/390 shots of it (12 PNGs).
- `e2e/email-ux/evidence/captured-html/` + `capture-summary.md` + `consistency-matrix.md` —
  the pre-redesign audit's own captured emails and matrix.

Logs: `final-email-e2e-log.txt` (+ every other stage log above). `email-previews/` holds the
exact `.html`/`.txt` previews audited in this run.
