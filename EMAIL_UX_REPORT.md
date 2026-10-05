# DeChimexPrints — Email UI/UX Overhaul (Task 6) — Final Report

> **Superseded 2026-10-05.** This report describes the first email overhaul, whose palette
> (violet `#7C3AED`, gradient strip, `#F6F5FC` canvas) was **not** the app's own design
> system. That language has been removed: every colour, radius, component and type rule now
> comes from the DeChimex Hub app itself. Current contract: **`EMAIL_DESIGN_STANDARD.md`**.
> Kept for history only.

Date: 2026-09-24 · Branch state: v047-DesignReview-Fixed-v4-Wishlist-Fix (all prior tasks preserved)
Scope: presentation/UX of every outgoing DeChimex email. Triggers, recipients, notification
logic, provider integration, URLs/routes and auth rules preserved (criterion 13).

---

## 1. Audit first (criterion 1)

Full inventory completed BEFORE any edit → **`EMAIL_UX_AUDIT.md`** (19 outbound emails,
trigger sites with file:line, recipients, links, dynamic data, gaps). Highlights:

- Trigger sites: `UserEventHandlers` (4), `PasswordManagement` (1), `PaymentConfirmationService` (3),
  `OrderEventHandlers` (7 order/payload flows), `MarketingBroadcastProcessor` (1),
  `BrevoEmailHealthCheck` (1 test email), contact intake + staff reply (2, previously raw-HTML).
- Two orphan templates (`Otp`, `DesignProofApproved`) had no send site — redesigned for
  consistency but **left unwired** (no new notification types, criterion 16).
- The admin-side design-proof email (`DesignProofApproved`) is in-app only today — kept so.

## 2. Design system (criterion 2)

Built once in `src/DeChimex.Infrastructure/Email/EmailTemplateRenderer.cs` (single shell +
component library; no new projects, services or dependencies):

| Token | Value | Use |
|---|---|---|
| Ink | `#1E1B2E` | headlines |
| Body | `#3B3752` | body copy |
| Muted | `#6B6880` | labels, notes |
| Line | `#ECEAF5` | rules, card borders |
| Page | `#F6F5FC` | outer page tint |
| Violet | `#7C3AED` | CTA, wordmark rule |
| Badge ok / danger / warn / info | `#0B6B4F/#E7F6EF`, `#B42318/#FDECEC`, `#B45309/#FFF4E5`, `#3730A3/#EEF2FF` | status pills |

Shell: 600px table-based card · 5px brand gradient rule · text wordmark
**DE-CHIMEX PRINT HUB** + tagline **CREATIVITY WITH EASE** · restrained header · shared footer
(`© {year} DE-CHIMEX PRINT HUB. Need help? hello@dechimexprinthub.com` — configured support
address) · hidden preheader · `lang='en'` · viewport + `format-detection` meta ·
`@media ≤620px` stacking (cards/rows/labels stack, CTAs go full-width).
Unsubscribe block ONLY on marketing (`RenderMarketing`), where it is mandatory.

**Components** (criterion 12 — smallest set that covers every template):
`Title` `Lead` `Note` `Rows/RowsOf/Row` `Badge` (text-labelled, never colour-only)
`Callout` (message quote / reply body) `Code` (6-digit tokens) `Alert` (danger/warn/info,
label-prefixed e.g. **Important:**) `Button` (bulletproof table+bgcolor, action verb label,
fine-print URL fallback) and the `Wrap` shell itself.

## 3. Templates reviewed & redesigned (criterion 4)

All 17 enum cases + marketing: Welcome · EmailVerification · ForgotPassword ·
PasswordResetConfirmation · Otp (orphan) · OrderConfirmation · OrderStatusUpdate ·
PaymentReceipt · PaymentFailed · OrderRequestReceived · OrderMessageNotification ·
OrderQuoteReady · DesignProofReady · DesignProofApproved (orphan) · ContactMessageReceived ·
ContactReply · ProviderTestEmail. Per-email review covered the brief's full list:
order confirm, payment success/failure(pending/alert copy), refund (status-update path +
receipt keeps fee line from task 5), status updates, design submit/approve(/reject alert),
messages, quotes, account/auth, staff/admin (contact copy, password-changed, provider test),
marketing. Each has: scannable title + short lead · status badge · human-labelled key/value
rows · ONE primary CTA · separated secondary details · reference info · shared footer.
Verification emails keep the prominent 6-digit code (E2E contract `(\d{6})` preserved) plus a
secondary button.

## 4. Transactional scannability (criterion 5)

- Human-readable labels everywhere ("Order number", "Amount paid", "Payment status",
  "Interested in", …); statuses humanized (`In Production` via word-boundary insert — replaces
  raw DB-style `InProduction` dumps).
- Currency follows app convention: `Common.MoneyText.Format` → `₦{N2}` (e.g. `₦9,560.86` in
  live-captured mail). No `NGN`/raw decimal dumps.
- Order emails show order number · amount (where the event/repository provides it) · payment
  status · next-action CTA ("Track your order", "Open conversation", "Review your quote").

## 5. Dynamic-content safety (criterion 11)

- `{{Key}}` substitutes HTML-encoded; `{{Raw…}}` reserved for trusted fragments
  (internal reply body HTML only). User text (names, chat previews) is always encoded —
  proven live: a contact message containing `<script>` rendered escaped in staff mail.
- `{{#Key}}…{{/Key}}` optional blocks disappear when the value is missing (e.g. contact
  "Phone" row vanishes for `POST /contact` with empty phone — unit-tested).
- Sensible fallbacks: missing amount → `—`; empty fields never leave `{{placeholders}}`
  (every render asserts zero leftover `{{`).
- Long values wrap (`word-break`), table layout survives long names/emails (visual tests
  used `O'Neil <b>Attridge-Smith-Worthington the Third</b>` and 100+-char messages).

## 6. CTAs & links (criterion 6)

All URLs come from `IAppUrlProvider` (`App:FrontendBaseUrl`) — production
`https://dechimexprinthub.com`, Development `https://localhost:7061` (environment config,
not template output). Customer emails link only public routes (`/portal/orders/track`,
`/portal/orders/{id}/workspace`, `/Account/…`); **no `/admin` route is ever exposed to
customers** (capture audit: 17/17 PASS). Buttons: violet on white, 8px radius, ≥14px padding,
label = action ("Try payment again", "Verify email address", "View order"); every CTA prints
its URL beneath for clients that block buttons.

## 7. Responsive + email-client compatibility (criteria 7–9)

- Table-based layout, inline styles, `bgcolor` + padded bulletproof buttons (Outlook-safe),
  no webfonts/JS/CSS animations, hidden preheader with `&zwnj;` padding, plain-text
  alternative auto-built from the HTML (tags stripped, entities decoded).
- `@media (max-width: 620px)` full-width CTAs + stacked label/value rows (verified at 390px).
- Gmail / Outlook desktop+web / Apple Mail / mobile notes: no `position`, no flex/grid,
  `mso`-safe tables, `meta name="format-detection" content="date=no"`, explicit `role='presentation'`
  tables, ≥15px body text (13px minimum for secondary notes), contrast AA+ on all text.
- Images: the design uses **zero remote images** by construction — layout cannot break on
  blocked images, no dev/local asset URLs can leak, nothing to mis-scale (criterion 9
  satisfied trivially; the brand is a styled text wordmark).

## 8. Accessibility (criterion 10)

Semantic heading order (`h1` title, `p` lead/notes), real link text (no "click here"),
status conveyed by words ("Payment failed", "Paid") + tone, not colour alone; labels and
values separated for screen readers; ≥15px reading size; strong ink-on-white contrast.

## 9. Bugs fixed within scope (criterion 13 allowance)

1. **Contact staff-copy subject substitution** — now verifiably `New contact form message
   from Ada Lovelace` in the real Brevo payload (was a {{Name}} risk point; live capture proves
   substitution, no braces).
2. **Subject-line encoding regression** (found by the new tests) — subjects substitute raw
   plain text (entities in a subject would render visibly as `&lt;i&gt;`).
3. **Mobile CTA width** (found by the side-by-side audit) — button anchor missing `dc-btn`
   class + wrapper table collapsed to content; fixed (full-width at ≤620px, re-verified).
4. **Composed-body typography** (found by the mechanical consistency matrix) — owner-authored
   marketing HTML had no design-system heading/paragraph/list/image styles; normalized via
   `.dc-body` base CSS (RenderMarketing contract unchanged).
5. `OrderPaidEventHandler` referenced a non-existent event field while adding the Amount row —
   resolved by loading the order via `IOrderRepository` (same pattern as the status handler),
   with `—` fallback.

## 10. Reusable architecture (criterion 12)

One renderer class + one tiny `MoneyText` helper + 3 enum members. Call sites switched from
hand-built HTML strings to `renderer.Render(template, model)` (OrderEventHandlers,
PaymentConfirmationService, UserEventHandlers-equivalent paths, PasswordManagement,
ContactMessageCommands, StorePagesController contact intake, MarketingBroadcastProcessor,
BrevoEmailHealthCheck). `RenderMarketing` contract unchanged (subject/HTML from the owner
broadcast, mandatory unsubscribe footer added).

## 11. Test results (criterion 17)

| Suite | Result | Log |
|---|---|---|
| Full solution (418 unit incl. 24 new `EmailTemplateRendererTests` + 330 integration) | **748 / 748** | `final-test-log.txt` |
| Service-fee feature E2E (regression rerun; order+pay+refund+activity with live stubs) | **54 / 54** | `final-service-fee-e2e-log.txt` |
| Live email suite vs running app + Brevo stub (EmailWorkflow + LinkOrigin + Marketing + Contact) | **19 / 20** | `final-email-live-log.txt` |
| Live-captured Brevo payload audit (subjects, recipients, route/https, zero `{{`) | **17 / 17 PASS** | `e2e/email-ux/evidence/capture-summary.md` |

The single live-suite failure is `All_email_workflows_deliver_to_target_inbox`: after
executing all 14 workflow stages it polls **real Brevo's** `statistics/events` API to count
delivered mail — unreachable in this sandbox (opt-in real-Brevo test by design). Its stages
themselves ran (log), and equivalent verification is provided by the captured provider
payloads.

Realistic-data tests (criterion 14): every template rendered with long/markup-injected names,
100+-char messages, missing optionals, and inspected at 1280px + 390px — 38 preview shots.

## 12. Side-by-side consistency audit (criterion 15)

Audited all 38 preview renders + 12 live-captured renders together, then made the audit
**mechanical**: `e2e/email-ux/evidence/consistency-matrix.md` extracts the actual style values
from every render and diffs them across the family (wordmark/tagline, `lang`/viewport,
h1 scale, button geometry, footer, unsubscribe placement, `@media` rule, `role='presentation'`
tables, `NGN`/`{{` leaks). Result: single button style (`#7C3AED` / r8 / 15px / 14px 30px)
in all 12 CTA templates, identical shell/footer/type scale everywhere, unsubscribe only in
marketing. Verified consistent: header/footer/gradient rule · 600px card · type scale
(22/18/15/13px) · spacing rhythm · button geometry and colour · badge pills with labels ·
row tables · ₦{N2} money · "Need help?" footer · alert style with label prefix · URL-fallback
pattern · terminology (order number / payment status / amount paid).

Fixes found and applied by the audit:
1. Mobile CTA width — button anchor missing `dc-btn` class + wrapper table collapsed to
   content (fixed; full-width at ≤620px, re-verified at 390px).
2. Marketing/composed bodies had no forced heading — normalized via `.dc-body h1/h2/h3/p/ul/a/img`
   base styles so campaign HTML lands on the same type scale without altering owner content.

## 13. Evidence (ordered) — `e2e/email-ux/evidence/`

1. `capture-summary.md` + `captured-html/` (6 live payloads as sent to the Brevo API)
2. `live-06-OrderConfirmation-{desktop,mobile}.png` … `live-17-OrderStatusUpdate-*` (12 shots
   of REAL provider payloads: confirm, receipt, failed, quote, contact copy, status update)
3. `01-Welcome-*` … `17-ProviderTestEmail-*`, `98-Marketing-*`, `99-OrderConfirmation-Minimal-*`
   (38 template renders incl. hostile/long data and missing optionals)

Task-5 service-fee screenshots moved to `e2e/service-fee/evidence/` (17); task-4 shots remain
at `e2e/direct-checkout/evidence/` (21).

## 14. Preserved (criterion 13 / 16)

Triggers & recipients unchanged · queue + Brevo provider + retries unchanged · all link
origins via `IAppUrlProvider` · auth rules untouched · payment/order/design notification
logic untouched · no new notification types (except replacing the health check's raw-HTML
test email with `ProviderTestEmail` — a bug-level fix) · no preferences centre, no marketing
platform features, no template builder, no new dependencies, no unrelated refactors.
