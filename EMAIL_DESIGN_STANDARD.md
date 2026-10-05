# DeChimex Hub Email Design Standard

**The rule: an email from DeChimex Hub is the DeChimex Hub UI translated into email.**
Not a generic template wearing the brand, and never a design the app itself does not use.
This document is the contract for the single email layout
(`src/DeChimex.Infrastructure/Email/EmailTemplateRenderer.cs`) and for every template added
from now on. Read it before touching an email; the drift-guard test enforces it.

Date: 2026-10-05 · Supersedes the violet email language documented in `EMAIL_UX_REPORT.md`.

---

## 1. One source of truth: the app's own stylesheets

The email palette, type, radii and tone labels are copied from the app, and a unit test
(`tests/DeChimex.UnitTests/Infrastructure/EmailDesignSystemTests.cs`) re-reads
`wwwroot/css/app.css` and `wwwroot/css/customer.css` and fails if any colour in a rendered
email does not exist in those files.

| Email token | Value | App source |
|---|---|---|
| Ink (headings) | `#17161C` | `--n-900` |
| Strong (emphasised values) | `#1E293B` | `--n-800` |
| Body text | `#475569` | `--n-600` |
| Muted (labels, notes) | `#64748B` | `--n-500` |
| Hairline (block borders) | `#E7E4DC` | `--border` |
| Hairline light (row rules) | `#F2F0EB` | `--n-100` |
| Card | `#FFFFFF` | `--n-0` |
| Quiet surface (footer strip) | `#FCFBF9` | `--n-25` |
| Page background | `#F7F6F2` | `--canvas` (warm paper) |
| Primary (CTA, links) | `#4338CA` | `--primary` |
| Primary deep (logo tile, amounts) | `#312E81` | `--primary-800` |
| Primary soft (tinted panels) | `#EEF2FF` | `--primary-50` |
| Status ok / warn / bad / info text | `#065F46` `#92400E` `#991B1B` `#3730A3` | `.cs-status` |
| Status ok / warn / bad / info tint | `#ECFDF5` `#FFFBEB` `#FEF2F2` `#EEF2FF` | `.cs-status` |
| Alert borders (ok/warn/bad/info) | `#A7F3D0` `#FDE68A` `#FECACA` `#BFDBFE` | `.flash` |
| Link-fallback line | `#94A3B8` | `--n-400` |
| Type stack | `'Inter',-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif` | app font stack |

The retired pre-redesign email palette (`#7C3AED`, the pink/orange gradient strip,
`#F6F5FC`, `#3B3752`, `#1E1B2E`, `#6B6880`, `#ECEAF5`, `#F3F0FF`, `#EC4899`, `#F97316`)
is banned; the guard test fails if it reappears.

## 2. Shell anatomy (every email, transactional and marketing)

1. **Hidden preheader** summarising the message.
2. **Top bar** = the app's brand lockup, identical to `.cs-logo` + `.cs-brand-text`:
   40px indigo (`#312E81`) tile, radius 11, with the real `/img/logo.png` on white,
   "DE-CHIMEX" (15px/800 ink) over "PRINT HUB" (9.5px/700, letter-spaced, primary).
3. **Content card**: 600px, white, 1px `--border`, radius 18, on the warm canvas.
4. **Body cell** (`class='dc-pad dc-body'`) carrying the template.
5. **Slim footer**: quiet strip with the "Creativity with Ease." line, Privacy · Terms ·
   Contact · support email, copyright, "Secure payments by Paystack · Prices in NGN",
   and, for marketing only, the server-appended unsubscribe line. No columns, no address
   block, no social wall, no oversized corporate footer.

Structure rhythm for every message: brand, contextual heading, short message, the relevant
information (rows/panel/code), ONE primary action, supporting note, footer. Transactional
messages stay short; marketing may tell a longer story in the same shell and type scale.

## 3. Components and the app component each one mirrors

| Email component | App equivalent |
|---|---|
| `Title` (24px/800, -.02em) | `.cs-page-head h1` |
| `Lead` (15px/1.6) | `.cs-page-head p` |
| `Note` (12.5px muted) | `.field-hint` / `.ck-sum-hint` |
| `Rows` / `RowsOf` / `Row` (label left, value right, `--n-100` hairline, tabular figures) | workspace summary table (`.ws-page .table`) |
| `Total` (soft indigo panel, 12px radius, 19px/800 deep indigo) | `.ck-sum-row.is-pay` ("You will pay") |
| `Badge` (999px pill, 12.5px/700, leading dot, tone tint) | `.cs-status` |
| `Panel` (14px radius tinted block with a bold title line) | `.cs-callout` |
| `Message` (white bubble, hairline, 18px radius, 6px tail, quiet sender line) | `.chat-bubble.theirs` |
| `Code` (white field, hairline, 12px radius, 30px/800, letter-spacing 8px) | `.cs-body .input` token field |
| `Alert` (tinted panel + matching hairline + tone label: Note / Security / Important / Done) | `.flash` / `.alert-*` |
| `Signature` (hairline then sender line) | `.card-head` divider |
| `Button` (indigo `#4338CA`, radius 12, 15px/700, full-width ≤620px, URL fallback line) | `.btn-primary` large (`.cs-body .btn`) |

Nothing else: no gradients, illustrations, stock imagery, icon fonts, decorative dividers or
"modern email" ornaments. The only image in the system is the logo.

## 4. Copy rules

- Natural, concise, premium tone; write to a person, not to a "valued customer".
- **No em dashes and no en dashes** anywhere in email copy (guard test).
- No "We're excited to inform you", no filler, no robot-notification phrasing.
- Headings are sentence case; labels are 1–2 words; one clear action per message.
- Status is a human phrase ("Payment received", "Awaiting your approval"), never an enum.
- Money renders as `₦1,250,999.50` (currency sign, thousands separators, tabular figures).
- Subjects stay short, brand-consistent and dash-free; the existing subject locks in tests
  are part of the contract.

## 5. Email-client engineering rules

- Table-based layout with `role='presentation'`; one 600px container; no flex, grid or
  absolute positioning (Outlook). `bgcolor` attribute + inline `background-color` on buttons.
- `lang='en'`, `charset`, `viewport`, `x-apple-disable-message-reformatting`,
  `format-detection`, `color-scheme: light` + `supported-color-schemes`.
- Guarded `@media only screen and (max-width:620px)`: full-width CTAs, reduced padding,
  smaller H1; verified at 390px and 320px.
- Contrast: every text/background pair used is ≥ 4.5:1 (asserted in the harness).
- Status never relies on colour alone: pills carry text plus a leading dot; alerts carry a
  tone label.
- Plain-text alternative is generated for every send: template markup stripped, values
  faithful, and each action URL kept inline so a text-only client can still act.
- No HTML entities that Outlook mangles; URLs in `href` attributes are absolute and
  single-quoted, values escaped with `&#39;`/`&amp;` where user data is involved.

## 6. Template contract (do not break)

- `{{Key}}` values are HTML-encoded unless the key starts with `Raw` (trusted fragments
  encoded by the caller); `{{#Key}}…{{/Key}}` blocks vanish when the value is blank.
- Subjects are substituted unencoded (plain text, caller-owned).
- Marketing always routes through `RenderMarketing` (free-form admin HTML inside the same
  shell) and always carries the server-appended unsubscribe link.
- Call sites keep supplying the same model keys; never invent a key a call site does not
  send (the renderer's own storefront link comes from `AppOptions.FrontendBaseUrl`).
- Two templates are intentionally dormant (`Otp`, `DesignProofApproved`: no send site yet).
  They still render inside the system; wiring new senders is out of scope.

## 7. Adding a new email (recipe)

1. Add the enum value + a `switch` arm in `EmailTemplateRenderer` using the components above.
2. Reuse an existing send path/trigger; do not add a parallel layout or a second shell.
3. Keep the subject lock list in `EmailTemplateRendererTests` in sync.
4. Run the guard + renderer tests:
   `EMAIL_PREVIEW_DIR=/tmp/email-after dotnet test tests/DeChimex.UnitTests --filter "FullyQualifiedName~Email"`
   (writes `NN-Name.html` and `NN-Name.txt` previews).
5. Run the email journey: `bash e2e/email-ux/run_final.sh <tag>` — it renders all previews,
   drives the live flows through the Brevo stub, audits every captured send against this
   document, sweeps CTA links against the running app and shoots desktop/mobile/dark/narrow
   evidence plus app reference shots.

Evidence for the 2026-10-05 redesign: `e2e/email-ux/evidence-before/` (old language),
`evidence-pre-redesign/` (audited baseline), `evidence-after/` (new language, live run) and
the pipeline's own run under `evidence/` (see the run's `final-email-e2e-log.txt`).
