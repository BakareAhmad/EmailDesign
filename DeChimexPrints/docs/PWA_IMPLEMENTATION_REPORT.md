# DeChimexPrints PWA Implementation Report

**Date:** 2026-10-04 · **Status:** Audit complete → recommendations below → implementation follows this plan.

---

## A. Current state (audit findings)

| Area | Finding |
|---|---|
| PWA-capable today? | **Partially** (baseline laid by audit fix AUD-029, 2026-10-04). Installable in Chromium; no push, no install UX, minimal offline. |
| Manifest | `wwwroot/site.webmanifest`: `id`, name/short_name, `start_url "/"`, `scope "/"`, `display standalone`, `portrait-primary`, icons 192/512 (+512 maskable), theme/background white. Linked from `_BrandHead.cshtml` (used by every layout). |
| Service worker | `wwwroot/service-worker.js` v1: conservative. Never touches `/api/*`, `/hubs/*`, `/uploads/*`, non-GET, cross-origin. Navigations network-first with cached `/offline.html` fallback. Static `/css|js|img|lib` stale-while-revalidate. `skipWaiting()` on install, old-cache purge on activate. |
| Offline support | `wwwroot/offline.html` styled fallback page only. No in-app offline/reconnect indicators. |
| HTTPS | Enforced: `UseHttpsRedirection` outside Development; cookies `Secure=Always` outside Development (AUD-023). Production requirement satisfied at the app level; the reverse proxy must forward HTTPS (KnownProxies configured, AUD-017). |
| Icons | `/img/icon-192.png`, `/img/icon-512.png` (official brand icon, existing), `apple-touch-icon.png`, `favicon.ico`, `favicon-32.png`. |
| Install UX | None. No `beforeinstallprompt` handling, no iOS guidance, no install help. |
| Push notifications | None. Notification stack is in-app only: `NotificationService` (Infrastructure) persists to `Notifications` table and pushes realtime via `INotificationPusher` (SignalR `NotificationHub`, auto-joined `user:{id}` group). Emails via Brevo outbox for major events. No browser permissions, no VAPID, no subscriptions. |
| Browser notifications | None beyond the site's own toast system. |
| Caching | Static files: `/uploads` media 7 days must-revalidate; other wwwroot 1 day. **`/service-worker.js` inherits the 1-day cache, which delays SW updates: must be `no-cache`.** |
| Mobile responsiveness | Strong. Dedicated customer shell (`customer.css`), fixed bottom navigation (`.cs-bottom`, 5 targets) **already with `env(safe-area-inset-bottom)`** and `viewport-fit=cover` in the viewport meta. Admin/dev are separate desktop-first shells (deliberate). |
| SignalR implications | Clients (`portal-notifications.js`, `order-workspace.js`, `business-activity.js`) manage their own connections with resync-on-reconnect. SW never touches `/hubs/*`. Pages using SignalR are authenticated navigations: always network-fresh. No conflict. |
| Auth/session concerns | Cookie auth + `secstamp` freshness (AUD-006). SW must never cache authenticated HTML (it doesn't: navigations are network-only). Account switching is safe as long as the SW keeps avoiding cached HTML and private uploads (`/uploads/*` excluded). |
| Paystack concerns | Payment pages (`/checkout/*`) are server-rendered navigations (network-first, never served from cache); Paystack checkout is cross-origin (SW ignores). Paystack JS comes from `https://js.paystack.co` (cross-origin, SW ignores). Risk is only the offline fallback serving `offline.html` for a payment navigation when the network drops, which is correct behaviour (no fake success). Add `/checkout` to the hard-exclusion list for defence in depth. |
| Uploads/design attachments | Private uploads flow through the authorized middleware (AUD-021) and are excluded from SW. Chat/upload failure UX already toasts errors and keeps drafts. |
| Share functionality | Product detail has "Copy link" + WhatsApp share. Web Share API can progressively enhance the copy button. |
| Existing tests | `brand_e2e.py` fetches `/site.webmanifest` (asserts it parses). Integration suite (428) has no PWA tests. `hero_e2e.py` depends on hero DOM only. |

## B. Gap analysis

| Area | Current | Required | Gap | Recommendation | Priority |
|---|---|---|---|---|---|
| Web App Manifest | Present, standalone | Valid manifest tuned to brand | Missing `display_override`, `categories`, `shortcuts` | Extend existing file | High |
| Icons | 192/512/maskable/apple-touch | All sizes present | None | Keep | — |
| Splash/loading | OS-generated from manifest | Present | None (Chrome builds it from manifest) | Keep | — |
| Standalone display | `display: standalone` | Works | Installed-vs-browser not detected in JS | Add display-mode detection | High |
| Theme/status bar | `theme-color #ffffff`, viewport-fit=cover | Fine | None | Keep | — |
| Service worker | v1 conservative | Safe versioned SW with update flow | No update UX; immediate `skipWaiting` | v2 with waiting-worker banner | High |
| Caching | Static SWR only | Documented, intentional | SW file itself cached 1 day | `no-cache` for SW/manifest/offline | High |
| Offline behaviour | offline.html on failed navigation | Offline/reconnect indicators; no false success | No online/offline UX | Add banners + keep fallback | High |
| Install prompt | None | `beforeinstallprompt` card | Missing | Implement (Chromium) | High |
| Android install guidance | None | In-app instructions | Missing | `/install-help` + card | High |
| iOS install guidance | None | Share → Add to Home Screen steps | Missing | iOS card variant + page section | High |
| Push notifications | None (in-app + email only) | Web Push for customer-relevant events | Everything | Implement: VAPID (seeded), `PushSubscription` entity + migration, subscribe/unsubscribe endpoints, SW handlers, dispatch hook in `NotificationService` | High |
| Notification permissions | None | Contextual opt-in, states surfaced | Missing | Button in notifications dropdown; never auto-prompt | High |
| Notification click routing | n/a | Focus-or-open deep link | Missing | SW `notificationclick` handler | High |
| Deep links | Normal routes | SW routes to notification `Link` | Missing | Part of click handler | High |
| App shortcuts | None | Launcher shortcuts | Missing | Public routes only (Shop, Track order, Custom printing) | Medium |
| Badge support | In-app bell badge only | Badging API progressive | Missing | `setAppBadge/clearAppBadge` when supported | Medium |
| Share | Copy link + WhatsApp | Web Share where supported | Missing | Enhance copy button with `navigator.share` | Medium |
| Background behaviour | None | Not required beyond push | n/a | No background sync (see D) | — |
| Update strategy | Instant skipWaiting | Safe, user-controlled refresh | Missing | Waiting worker + "New version available" banner | High |
| Security | Strong (cookie auth, upload guard, CSP) | No private caching; logout safety | SW file caching | Exclusions kept; audit documented | High |
| Accessibility | Good baseline (skip links, aria) | New UI accessible | New components must be | role=status, focus-safe banners | High |
| Mobile UX | Bottom nav + safe areas | Keep, don't duplicate | None major | Install card must clear bottom nav | High |
| Performance | Assets modest, SWR on statics | Fast repeat loads | None major | Measure, don't over-cache | Medium |
| Monitoring | Dev portal health/metrics | SW/push failures logged | Push failures logged via ILogger | Add logs | Medium |
| Testing | No PWA tests | Manifest/SW/push E2E + regression | Missing | New integration tests + `pwa_e2e.py` stage | High |
| Deployment | Reverse proxy + HTTPS | SW scope `/`, correct headers | SW caching header | Header fix (above) | High |

## C. Recommended architecture

Reuse everything. No new frameworks, no client build step, no new database beyond one table.

- **Manifest:** extend `wwwroot/site.webmanifest` in place (served as `application/manifest+json` by static files with correct extension MIME on Linux? — verified in tests; if the MIME is missing, map it via `FileExtensionContentTypeProvider`).
- **Service worker:** evolve the existing `wwwroot/service-worker.js` (v2). Same conservative fetch policy, plus `push`, `notificationclick` and a message channel (`SKIP_WAITING`, test hook). Install no longer calls `skipWaiting` unconditionally: a new waiting worker raises an in-page "New version available" banner; refresh is user-initiated (never during a POST or payment; banner is passive and dismissible).
- **Update-safe headers:** `/service-worker.js`, `/site.webmanifest`, `/offline.html` served `Cache-Control: no-cache` from the existing `UseStaticFiles` hook.
- **Web Push:** `WebPush` NuGet package (server) + standard Push API (client). VAPID keys are generated once by `DatabaseSeeder` into `ApplicationSettings` (`push.vapid.*`) — the established settings store, owner-inspectable, never client-visible except the public key. New `PushSubscription` entity + EF migration. Subscribe/unsubscribe via authenticated MVC endpoints under `/portal/notifications/push/*` (cookie auth + antiforgery, same as the existing mark-read endpoint). Dispatch hooks into the existing `NotificationService.NotifyAsync`: **only direct recipient notifications** (`recipientUserId != null`) are pushed, best-effort, failures logged, invalid subscriptions (410/404) deleted. Staff/admin broadcasts are intentionally not pushed.
- **Client PWA logic:** extend `wwwroot/js/pwa-register.js` (SW registration, update banner, offline/reconnect toasts, install card, iOS card variant, standalone detection). No framework; plain ES5-style IIFE consistent with the codebase.
- **Install help:** new `/install-help` page on the customer shell (StorePagesController) with Android/iPhone/desktop/generic sections; JS highlights the detected platform. Linked from the install card and the footer.
- **Badging/share:** progressive enhancement inside existing files (`portal-notifications.js`, product detail copy button).

## D. Feature recommendations

| Capability | Verdict | Why |
|---|---|---|
| Manifest tuning, shortcuts (public routes) | **IMPLEMENT NOW** | Cheap, real launcher value, no auth exposure. |
| SW v2 + offline fallback + version/update banner + no-cache headers | **IMPLEMENT NOW** | Fixes the only real update-safety gap; preserves the conservative cache policy. |
| Offline/reconnect indicators | **IMPLEMENT NOW** | Honest degraded-network UX; no false success. |
| Install card (Chromium `beforeinstallprompt`) + iOS instructions + `/install-help` | **IMPLEMENT NOW** | Required; iOS gets instructions, not a fake prompt. |
| Installed-vs-browser detection | **IMPLEMENT NOW** | Suppress install UI inside the installed app. |
| Web Push for customer notifications (order/quote/design/payment/message events sent to a recipient user) | **IMPLEMENT NOW** | Real value: order status, quote ready, design ready, payment events already exist as direct notifications. |
| Contextual permission flow (button in notifications dropdown) | **IMPLEMENT NOW** | No auto-prompts; denied state respected. |
| Notification click focus-or-open deep link | **IMPLEMENT NOW** | Standard pattern; routes to the notification's `Link`. |
| Badging API | **IMPLEMENT NOW (progressive)** | Silent no-op where unsupported; in-app badge stays authoritative. |
| Web Share API on product share | **IMPLEMENT NOW (progressive)** | Enhances the existing Copy link button only. |
| Push for staff/admin broadcasts | **DEFER** | High volume, lower marginal value (staff are realtime in-app + email); avoids notification fatigue. Structure allows it later. |
| Offline order/message submission queue | **DO NOT IMPLEMENT** | Payments, orders, approvals and messages must be server-confirmed; a queue risks double-submission and false success. Offline actions show a clear "needs a connection" state instead. |
| Background Sync | **DO NOT IMPLEMENT** | Same correctness concerns as the queue; Safari/iOS absent. |
| Periodic background sync / offline catalog cache | **DEFER** | Catalog is server-priced; stale prices are a business risk for marginal gain. |
| APK/TWA packaging, in-app purchases | **DO NOT IMPLEMENT** | Out of scope; Play Store policy risks around payments. |
| Customer bottom navigation | **ALREADY EXISTS** | `.cs-bottom` (5 targets, safe-area aware). No duplicate navigation. |
| Admin install prompts | **DO NOT IMPLEMENT** | Staff/admin shells stay clean; staff can still reach `/install-help` if they want the app. |

## E. Implementation notes (constraints honoured)

- Start URL stays `/` (public storefront; customers land in the shop, staff can navigate in; authenticated areas remain server-protected).
- SW never caches: `/api`, `/hubs`, `/uploads`, `/checkout`, anything non-GET, anything cross-origin; navigations are never served from cache (offline fallback only on network failure).
- Payment pages can never be stale or faked; offline shows the offline page; Paystack redirects are cross-origin and untouched.
- Push payload carries only the in-app notification's title/body/link (no more data than the user's own notification centre already shows on their locked device).
- Unsubscribe happens client-side (button), server-side (failed endpoint → row deleted), and on logout of the subscribing device only if the user unsubscribes; multiple devices per user are separate rows.

---

# Part 2 — Implementation results (2026-10-04, same day)

**Status: IMPLEMENTED, tested end-to-end.** Solution builds 0 errors; full suite **893/893 green**
(459 unit + 434 integration, 887 baseline + 6 new PWA tests); complete final run passed every stage
(tests, health, dev smoke 29/29, checkout 60, brand 282, design-review 36, fixes 39, dev-portal 108,
dev-abort 37, **PWA 27**) with 0 unhandled app-log exceptions and a clean secret-leak sweep.

## F. What shipped

| Area | Before | After |
|---|---|---|
| Manifest | Installable, no extras | Brand-tuned; `categories`; **launcher shortcuts** (All products, Track an order `/portal/orders/track` — verified routable —, Custom printing, Our work) |
| Service worker | v1, instant `skipWaiting` | **v2 `dcx-v2`**: versioned caches, NO install-time `skipWaiting`, waiting-worker **"New version available"** banner (Refresh / Later, never forced, 3s safety apply after consent), `push` + `notificationclick` + test hook |
| Update headers | SW cached 1 day (stale-trap) | `/service-worker.js`, `/site.webmanifest`, `/offline.html` → **`Cache-Control: no-cache`** (Program.cs `OnPrepareResponse`) |
| Offline UX | bare offline.html | Honest offline/reconnect toasts + styled offline shell ("Try again"; **never** fake success for orders/payments/messages) |
| Install UX | none | Chromium `beforeinstallprompt` card (4s settle, mobile-only, never standalone/desktop/checkout-time dismissal memory 14d `dcx-pwa-install-dismissed-at`); **iOS auto instructions card** (6s); `/install-help` page (Android ⋮ Install app / iPhone Share → Add to Home Screen / desktop / generic); footer "Install the app" link |
| Web Push | none | VAPID (seeded, idempotent), `PushSubscriptions` table, subscribe/unsubscribe endpoints (auth + antiforgery), contextual opt-in row in the notifications bell, dispatch on direct-recipient notifications, 404/410 cleanup, multi-device |
| Notification click | n/a | close → focus same-origin client (best-effort) → `dcx:navigate` postMessage → else `openWindow(url)`; routes to the notification's own URL, never the homepage |
| Badging | in-app bell only | `setAppBadge/clearAppBadge` progressive (`updateOsBadge`); in-app badge stays authoritative |
| Share | Copy link + WhatsApp | `navigator.share` first where supported, clipboard fallback (`customer-product.js`) |

## G. Files changed

**Backend:** `Domain/Entities/PushSubscription.cs` (new, AggregateRoot), `Domain/Repositories/Repositories.cs` (+`IPushSubscriptionRepository`), `Persistence/Configurations/PushSubscriptionConfiguration.cs` (soft-delete filter + filtered unique index), `Persistence/Repositories/PushRepositories.cs` (new), `Application/Features/Notifications/PushFeature.cs` (validators/handlers), `Application/Common/Interfaces/Services.cs` (+`IWebPushDispatcher`), `Infrastructure/Services/PwaWebPushDispatcher.cs` (new), `Infrastructure/Services/NotificationService.cs` (dispatch hook), `Infrastructure/DependencyInjection.cs` (scoped registration), `Persistence/DatabaseSeeder.cs` (VAPID gen, idempotent), `Host/Controllers/Mvc/Portal/PortalNotificationsController.cs` (3 push endpoints), `Host/Program.cs` (no-cache headers), migration `20261004110951_PwaPushSubscriptions`.
**Frontend:** `wwwroot/service-worker.js` v2, `wwwroot/js/pwa-register.js` v2 (+`DcxPwa` QA hooks), `wwwroot/site.webmanifest`, `Views/StorePages/InstallHelp.cshtml` + controller action (`/install-help`), `wwwroot/css/customer.css` (install card/update banner styles), `wwwroot/js/portal-notifications.js` (push row + badging), `wwwroot/js/customer-product.js` (Web Share), `Views/Shared/_CustomerLayout.cshtml` (`#portalNotifPush` slot, `dcx-toast.js` now loaded on ALL storefront pages — offline/reconnect toasts work outside the portal too, footer install link).
**Tests:** `tests/DeChimex.IntegrationTests/Controllers/PwaSurfaceTests.cs` (6 tests), `e2e/pwa/pwa_e2e.py` (27 checks) + `e2e/fixes/run_final.sh` stage 8d (`PRIMARY=pwa`).

## H. Notification architecture (as built)

```
NotifyAsync(direct recipient) ──> IWebPushDispatcher ──> WebPush VAPID send (5-min cached keys)
        │ (broadcasts: never pushed)                     ├─ 200 → MarkSuccess (persisted once/batch)
        └─ INotificationPusher (SignalR, unchanged)      └─ 404/410 → subscription row deleted
Browser: bell row "Turn on push notifications" → pushManager.subscribe(VAPID key from
GET /portal/notifications/push/vapid-public-key) → POST push/subscribe (antiforgery)
SW push event → showNotification {title, body, icon/badge, data.url} → click → focus-or-open data.url
```
Permission states surfaced: supported / not-requested ("Turn on…") / granted ("Turn off…") / denied
("turned off in your browser settings") / unavailable (row hidden) / expired (auto-cleaned). The
native prompt is never triggered on page load and never re-asked after denial.

## I. Browser compatibility matrix

| Platform | Install | Offline shell | Push | Notes |
|---|---|---|---|---|
| Android Chrome / Samsung Internet | ✅ card + menu | ✅ | ✅ | full experience |
| iPhone Safari (iOS 16.4+) | ✅ Add to Home Screen (instructions, honest) | ✅ | ✅ after install | pre-16.4: everything except push |
| iPhone Chrome/Edge | ⚠️ in-app browser limits; guidance to Safari/Share | ✅ | follows iOS support | documented on `/install-help` |
| Desktop Chrome/Edge | ✅ address-bar install (no mobile card) | ✅ | ✅ | card suppressed by design (<1024px only) |
| Desktop Safari | ⚠️ Add to Dock (macOS Sonoma+) | ✅ | ⚠️ | never a failure: features degrade silently |
| Any unsupported browser | site unchanged | per-navigation fallback | row hidden | **unsupported ≠ broken** |

## J. Security & what is NOT cached

Never cached/touched by the SW: `/api/*`, `/hubs/*` (SignalR stays client-managed), `/uploads/*`
(authorized private media), `/checkout/*` + non-GET requests + cross-origin requests; navigations are
never served from cache. Push payloads carry only the user's own notification title/body/link.
Endpoints are cookie-auth + antiforgery; VAPID private key lives server-side in `ApplicationSettings`;
subscriptions are per (user, endpoint) and owner-scoped. No offline queues/Background Sync for
payments/orders/approvals/messages — connection is required and the UI says so. No admin/staff install
prompts (shells don't load the PWA script).

## K. Known limitations & future improvements

- Push permission on iOS must be granted from the installed app (OS rule); Safari desktop push needs macOS Sonoma+.
- Staff/broadcast push intentionally deferred (structure allows it later).
- No offline catalog (server-priced products — stale prices are a business risk), no TWA/APK.
- Update banner applies the new worker only on user refresh; ultra-stale clients self-heal within 24h (browser SW cap).
- Future: push preferences per category, localized install art, iOS splash `<link rel="apple-touch-startup-image">` set.

## L. Evidence

`docs/evidence/pwa/` — 12 ordered shots from the final clean run:
01-mobile-web, 02-install-card, 03-android-install-help, 04-ios-install-help, 05-installed-pwa,
06-standalone-mode, 07-offline-state, 08-reconnected-state, 09-notification-permission,
10-push-notification, 11-notification-deep-link, 12-update-available.
Supporting logs of the same run: repo-root `evidence/final-*-log.txt` (incl. `final-pwa-e2e-log.txt`).
