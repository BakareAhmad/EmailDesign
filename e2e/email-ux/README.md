# e2e/email-ux — email design-system verification

One harness proves the rule in `EMAIL_DESIGN_STANDARD.md`: an email is the DeChimex Hub UI
translated into email-safe markup. It runs inside the shared pipeline as **stage 8h** and
as **primary evidence** for `bash e2e/email-ux/run_final.sh <tag>`.

```
# verify (default) - needs the app on :5119 with the Paystack/Brevo stubs
python3 e2e/email-ux/email_e2e.py <tag>

# audit mode: records failures without raising (used while studying a change)
EMAIL_MODE=audit python3 e2e/email-ux/email_e2e.py <tag>

# full pipeline with the email journey as primary evidence
bash e2e/email-ux/run_final.sh emailfinal
```

Environment: `E2E_ROOT`, `E2E_BASE_URL` (default `http://127.0.0.1:5119`),
`E2E_PREVIEW_DIR` (rendered previews, default `<root>/email-previews`),
`E2E_SHOTS_DIR` (default `<root>/evidence`), `E2E_BREVO_CAPTURE`
(default `/home/user/e2e-dr-wip/brevo_captured.jsonl`), `E2E_STUB_URL` (`:9090`).

## What it checks

**A. Rendered templates (all 19, from the real renderer).** Previews are written by
`EmailTemplateRendererTests` (`EMAIL_PREVIEW_DIR`, `.html` + `.txt` per template). For each:
every hex colour exists in the app stylesheets (`app.css` `:root` / `customer.css` `.cs-*`),
the retired pre-redesign palette is absent, the shell markers are intact, no `{{placeholder}}`
survives, no em/en dashes, email-safe layout (tables, 600px, media query, no flex/absolute),
the brand lockup uses the real `/img/logo.png`, the footer is the slim bar only, the CTA is
the app primary with a link fallback, unsubscribe appears only on marketing, and the
plain-text part is markup-free and keeps the app link. Then a WCAG AA contrast pass over the
token pairs the shell uses (11 pairs).

**B. Live sends.** Every message captured by the Brevo stub during the run (checkout,
custom orders, payments, registration, the drives below) must pass the same invariants for
HTML and text parts, and every CTA/link in the last captured sends must resolve on the
running app (2xx/3xx).

**C. Live drives for templates other journeys do not reach.** Direct checkout + payment
(OrderConfirmation, PaymentReceipt), forgot password, public contact form
(ContactMessageReceived), staff contact reply (ContactReply), admin settings provider test
(ProviderTestEmail), workspace staff message (OrderMessageNotification), design-proof upload
(DesignProofReady), and the unsubscribe landing page in an anonymous session. Each drive
asserts the capture arrived and audits its HTML.

**D. Screenshots.** Every preview at 1280 and 390 (device scale 2), plus dark-scheme 390 and
narrow 320 for the most-used templates, plus app reference shots (storefront, sign-in) so the
evidence shows email and app in the same frame of reference.

Result line: `RESULT: N passed, 0 failed (TAG=<tag>)`.

## Notes for maintainers

- The verification-code extractor used by the other harnesses
  (`e2e/design-review/ws_designreview_e2e.py` and the proof/placeholder/PWA copies,
  `verification_code()`) is design-independent: it prefers the renderer's code block
  (`letter-spacing:8px`) and otherwise takes the first 6-digit run that is not part of a hex
  colour, because palette tokens such as `#475569` are all digits.
- `evidence-before/` = the old email language (before shots), `evidence-pre-redesign/` = the
  earlier audit's own run, `evidence-after/` = the redesigned language from a live run. The
  pipeline writes the authoritative single-run evidence to the repo-root `evidence/`.
