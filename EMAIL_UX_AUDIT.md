# DeChimex Outgoing Email — UI/UX Audit (pre-change inventory)

Date: 2026-09-24 · Scope: every message the system sends over email (Brevo SMTP API).
This inventory was produced BEFORE any template was modified (brief §1).

> **Superseded in part on 2026-10-05.** The inventory below (templates, triggers,
> recipients, links, dynamic data) is still the accurate call-site map and is kept as the
> audit trail. The **design layer** it evaluated (violet shell, gradient strip, lavender
> canvas) was replaced by the app's own design system: see **`EMAIL_DESIGN_STANDARD.md`**.
> Two findings are now resolved by the app rather than the email: the logo asset exists at
> `/img/logo.png` and is used in the email header; emails are no longer outside the brand
> shell for contact intake/reply.

## Infrastructure (unchanged by this task)

| Piece | Location | Role |
|---|---|---|
| `IEmailService` / `EmailMessage` | `Application/Common/Interfaces/Services.cs` | Send contract |
| `BrevoEmailService` | `Infrastructure/Email/BrevoEmailService.cs` | Brevo `/smtp/email` (SenderName "DE-CHIMEX PRINT HUB", no-reply@) |
| `EmailQueue` / `IEmailQueue` | `Infrastructure/Email/EmailQueue.cs` | Fire-and-forget dispatch queue |
| `IEmailTemplateRenderer` + `EmailTemplate` enum | `Services.cs` + `Infrastructure/Email/EmailTemplateRenderer.cs` | `{{Placeholder}}` templates + brand shell |
| `IAppUrlProvider` | link builders (order tracking `/portal/orders/track?orderNumber=`, workspace, verify/reset, marketing unsubscribe) | All email links |
| `BrevoEmailHealthCheck` | admin "Send test email" diagnostics | provider probe |
| `BrevoOptions.DevOutboxPath` | dev-only file outbox for local capture | E2E aid |
| Existing live test | `EmailWorkflowE2ETests` (gated on `E2E_EMAIL_TARGET`) | real Brevo delivery + events-API verification |

## Outgoing email inventory

| # | Email | Subject (before) | Trigger | To | Dynamic values | Built where |
|---|---|---|---|---|---|---|
| 1 | Email verification | `Verify your DE-CHIMEX email` | register / resend (`UserEmailVerificationRequestedEvent`) | Customer | FirstName, Token (6-digit), VerificationLink | `UserEventHandlers` ×2 |
| 2 | Welcome | `Welcome to DE-CHIMEX PRINT HUB 🎉` | `UserEmailVerifiedEvent` | Customer | FirstName | `UserEventHandlers` |
| 3 | Forgot password | `Reset your DE-CHIMEX password` | `UserPasswordResetRequestedEvent` | Customer | FirstName, ResetLink | `UserEventHandlers` |
| 4 | Password changed | `Your DE-CHIMEX password was changed` | change-own-password command | Customer | FirstName | `PasswordManagement` |
| 5 | Order confirmed (paid) | `Your order {{OrderNumber}} is confirmed ✅` | `OrderPaidEvent` | Customer | FirstName, OrderNumber, TrackingLink | `OrderEventHandlers` |
| 6 | Order status update | `Update on your order {{OrderNumber}}` | `OrderStatusChangedEvent` | Customer | FirstName, OrderNumber, **Status = raw enum name**, TrackingLink | `OrderEventHandlers` |
| 7 | Payment receipt | `Payment receipt for order {{OrderNumber}}` | payment verified (`PaymentConfirmationService`) | Customer | FirstName, OrderNumber, Amount (`N2`, no currency sign) | `PaymentConfirmationService` |
| 8 | Payment failed | `Payment failed for {{OrderNumber}}` | `PaymentFailedEvent` | Customer | FirstName, OrderNumber, Amount (`N2`), TrackingLink | `OrderEventHandlers` |
| 9 | Custom request received | `We received your order request {{OrderNumber}} 🎨` | `OrderRequestSubmittedEvent` | Customer | FirstName, OrderNumber, WorkspaceLink | `OrderEventHandlers` |
| 10 | New order message | `New message on your order {{OrderNumber}} 💬` | `OrderMessageAddedEvent` (staff → customer) | Customer | FirstName, OrderNumber, **MessagePreview (raw user text, unencoded)**, WorkspaceLink | `OrderEventHandlers` |
| 11 | Quote ready | `Your quote for {{OrderNumber}} is ready 💰` | quote finalized | Customer | FirstName, OrderNumber, WorkspaceLink | `OrderEventHandlers` |
| 12 | Design proof ready | `A design proof for {{OrderNumber}} is ready for approval 🖼️` | proof uploaded | Customer | FirstName, OrderNumber, WorkspaceLink | `OrderEventHandlers` |
| 13 | Design proof approved | `Design approved for {{OrderNumber}} ✅` | **ORPHAN — never rendered** (handler sends in-app notification only) | — | FirstName, OrderNumber, WorkspaceLink | renderer only |
| 14 | OTP | `Your DE-CHIMEX verification code` | **ORPHAN — never rendered** | — | FirstName, Otp | renderer only |
| 15 | Generic fallback | `DE-CHIMEX PRINT HUB` | **ORPHAN** (default arm for undefined enum values) | — | Message | renderer only |
| 16 | Contact intake (staff) | `New contact form message from {Name}` | public contact form POST | Staff (`business.email`) | Name, Email, Phone?, Service, Details (**raw HTML, outside brand shell**) | `StorePagesController` |
| 17 | Contact reply (customer) | staff-entered subject | staff reply command | Customer | Body paragraphs, ActorName (**raw HTML, outside brand shell**) | `ContactMessageCommands` |
| 18 | Marketing broadcast | admin-composed | `MarketingBroadcastProcessor` (armed worker) | subscribed customers | FirstName, UnsubscribeLink + admin HTML | `RenderMarketing` (brand shell + mandatory unsubscribe) |
| 19 | Provider test | health-check text | admin "Send test email to my address" | Admin | — | `BrevoEmailHealthCheck` |

In-app bell notifications (`INotificationService`) are a separate channel and are out of scope.

## Findings (issues to fix in the redesign)

1. **Two different visual languages** — renderer emails (gradient-header card) vs raw-HTML contact emails (bare paragraphs, no shell) vs marketing (shell + unsubscribe). They do not look like one product (brief §15).
2. **Unencoded dynamic values** — `Substitute()` inserts model values raw. `MessagePreview` is user-authored chat text; `FirstName`/`OrderNumber` are user/admin data. A `<`, `&` or long token can break layout or inject markup (brief §11). (Contact emails encode manually today.)
3. **Layout is not email-safe** — div/gradient/box-shadow/border-radius soup: Outlook desktop drops gradients and shadows; no `<meta viewport>`, no presentation tables, no responsive rules (brief §7–8).
4. **Buttons** are gradient `<a>` pills without a bulletproof structure; "Or copy this link:" is repeated for every CTA in tiny gray text (brief §6).
5. **Currency inconsistency** — payment emails show `9,500.00` (no sign); the failed-payment template hardcodes `NGN {{Amount}}`; the app UI convention is `₦9,500.00` (brief §5).
6. **Status is an enum name** — `InProduction` lands in the customer's inbox (brief §5 human-readable labels). No visual status treatment anywhere.
7. **Subject inconsistency** — some subjects carry emoji (✅🎨💬💰🖼️🎉), some don't; tone varies (brief §15).
8. **Order/payment emails are one-liners** — no scannable summary of order number / amount / status / next action (brief §5).
9. **Orphans** — `DesignProofApproved` and `Otp` templates exist but are never sent (kept, redesigned; NOT wired to new triggers per brief §16).
10. **Accessibility** — no `lang`, small 11px tagline, information via bold/color only, no preheader text (brief §10).
11. **Plain text** is a regex tag-strip with collapsed whitespace; link URLs absent from the text part (brief: readable fallback).
12. **Logo asset missing** — `/img/logo.png` 404s in-app; emails use a text wordmark. Decision: keep a text wordmark in email (image-blocked clients still show the brand) — no new image dependencies (brief §9).

## Locked constraints (from tests + policy)

- Subject fragments asserted by tests (must keep): `Verify your DE-CHIMEX email`, `Welcome to DE-CHIMEX PRINT HUB`, `New contact form message from {Name}` (reply subject = staff-entered text, exact).
- All links continue to come from `IAppUrlProvider` (origin tests exist).
- Brevo transport, queue, triggers, recipients, notification logic unchanged.
- Marketing keeps its server-appended unsubscribe footer (cannot be omitted by authors).
- EmailVerification keeps a prominent 6-digit token (E2E scrapes `(\d{6})`).
