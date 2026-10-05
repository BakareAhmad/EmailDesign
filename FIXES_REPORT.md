# Fixes pass — 2026-09-26

This pass fixes the problems flagged after the Developer Portal delivery, plus others found while reviewing them. Every fix has an automated test, and the user-facing ones are proven in real browsers (`e2e/fixes/`).

## 1. Staff notifications could reach customers (security)

**Problem.** Two separate gaps:
- `NotificationHub` added *every* connection, customers included, to the all-staff group. So any staff broadcast without a required permission was pushed live to customers.
- The stored notification list, unread badge and mark-read used the same rule: a row with no recipient and no permission was visible to *any* user. So a customer could also read such rows in their bell.

No current code path creates such a row: every send today names a recipient or a permission. But the database can still hold legacy rows, and any future send without a permission would have leaked.

**Fix.** One audience rule is applied everywhere: rows with no recipient are for **staff only**, meaning a business staff role (`RoleNames.IsStaff`, the same role list admin login uses).
- A permission alone doesn't count, because customers hold `orders.create`.
- Developer-only accounts are not staff either.
- The rule covers the live hub, the list, the unread count and mark-read.
- Customers and developers still get notifications addressed to them.

**Proof.**
- **Tests:**
  - the hub audience for all five staff roles, a customer, a developer-only account, and a developer who is also staff;
  - the customer portal can't see or mark-read a legacy staff broadcast, while the owner still sees it;
  - the mark-read rules.
- **E2E:** live SignalR listeners on a customer and on the owner.
  - The owner still receives "New order request" and "Design review uploaded" live.
  - The customer receives only their own "Design review approved" and "Design review rejected".
  - The bell feed and dropdown show only the customer's own notifications.
  - The admin notifications page is unchanged.

## 2. Error pages offered customers an "Admin dashboard" button

**Problem.** The MVC error view (404 and status codes) and the exception error page (e.g. a 400 from an invalid upload) both showed an "Admin dashboard" link to every signed-in user.

**Fix.** `ErrorPageWriter.AccountHomeFor(user)` returns the user's own home, and both pages use it:

| Signed-in user | Link shown |
|---|---|
| Staff | Admin dashboard |
| Developer-only account | Developer Portal |
| Everyone else (customers) | My account (`/portal`) |
| Anonymous | Sign in (unchanged) |

**Proof.** Tests cover both pages for every audience. The E2E checks a customer's 400 and 404 pages, and the staff, developer and anonymous 404 pages.

## 3. Design-review live cards (wording plus two stale-UI bugs)

- **Wording mismatch.** The live card said "Awaiting staff review" but the page said "Waiting for the team to review". The live rejection label also said "Rejected:" where the customer page says "Changes requested:". Both now match the server wording for each audience.
- **Stale "waiting" line (found while fixing the above).** The live-update code removes elements by CSS class, but the server-rendered markup lacked those classes. After a reload, when staff approved a file, the customer's card kept saying "Waiting for the team to review".
- **Stale Approve/Reject buttons (same cause).** A second staff tab kept its Approve/Reject buttons after another staff member decided the review. Clicking them then failed.
- **Fix.** The server markup now carries the same classes as the live cards: `design-review-waiting`, `-actions`, `-reason` and `-approved-at`.
- **Proof.** The E2E uses two customer tabs and two staff tabs. It checks that the waiting line and the stale buttons disappear live, and that the wording matches both live and after a reload.

## 4. Dates lost their UTC marker when read from SQL Server (production only)

**Problem.** Every stored date is UTC, but SQL Server returns dates with an unspecified kind. JSON then had no `Z`, and browsers parsed the value as local time. So live relative times ("5 minutes ago") were skewed by the viewer's offset, 1 hour in Lagos. The in-memory test database preserves the kind, which is why earlier tests never caught it.

**Fix.** A DbContext convention re-tags every `DateTime`/`DateTime?` as UTC on read. Values are written unchanged.
- I checked that every date property is named `*Utc` and that nothing uses `DateTime.Now`.
- `dotnet ef migrations has-pending-model-changes` reports no model change, so no migration is needed.

**Proof.** A SQLite test (a real relational provider) checks entities, projections, nullable dates, the JSON `Z`, and UTC filtering. It failed before the fix and passes after. All SQL Server query-translation tests still pass.

## 5. Smaller items

- **Shell scripts lost their executable bit** (e.g. `make-deliverable-zip.sh`). All `*.sh` files are now `100755` in git, so the mode survives restores and zips.
- **`CustomOrderValidation` null safety.** A null answer would have thrown on a conditional question. It's hardened, with 3 unit tests. Today only the web form reaches this code, and it always sends strings.

## Reviewed, deliberately not changed

- **Service slug "regeneration"** (flagged in §72): there is no unique index on `Services.Slug` and nothing looks services up by slug, so it can't fail. Left as is.
- **Marketing preview double-encoding** (flagged in §69): already fixed (`srcdoc="@Model.HtmlContent"`).
- **Brand images (logo and hero):** the files were never in the repo, and no copy exists in the sandbox or either zip. The site already falls back cleanly. **You need to add** `logo.png`, `hero.jpg` and `hero-mobile.png` to `src/DeChimex.Host/wwwroot/img/` (see AI_HANDOFF §89).
- **Quote→order repricing** from the current catalog: documented, intentional behaviour. Changing it is a business decision.
- **Remaining compiler nullable warnings** in `CheckoutController` and the repositories are false positives, e.g. `customer` is always set when `error` is null.

## Verification (one clean final run: `e2e/fixes/run_final.sh`)

- Tests: **847/847** (459 unit + 388 integration; 829 before this pass)
- Public `/health` Healthy. Dev-portal smoke 29/29.
- Regressions: checkout 60/60; design review 36/36; developer portal 107/107.
- Fixes journey: **39/39**, with 13 ordered screenshots in `e2e/fixes/evidence-final-run/` (moved there when the brand-images run took over `evidence/`).
- Secret-leak sweep clean. The previous (developer-portal) evidence is preserved in `e2e/dev-portal/evidence-final-run/`.
