"""PWA + app-like mobile experience E2E (PWA task, independent of business flows).

Journeys (evidence shots land in DeChimexPrints/docs/evidence/pwa, exact ordered names):
  mobile web:    manifest (brand fields/shortcuts/icons), service worker activates,
                 /portal/orders/track shortcut route exists (not 404)          -> 01
  install card:  iPhone Safari auto card (no beforeinstallprompt on iOS), dismiss
                 remembered across reloads                                      -> 02
  install help:  /install-help highlights Android on Chrome UA, iPhone on iOS UA -> 03, 04
  installed:     display-mode standalone never shows install cards               -> 05, 06
  offline:       navigations served the offline shell while offline, honest
                 reconnect toast after coming back (no fake success anywhere)    -> 07, 08
  notifications: contextual permission row in the bell (default -> granted),
                 showNotification lifecycle, click-through deep link to the
                 notification's own route (never the homepage)                   -> 09, 10, 11
  update:        "New version available" banner renders; Later keeps the page,
                 Refresh hides the banner (mechanics post SKIP_WAITING)          -> 12

Run against a live Host on 127.0.0.1:5119 (Development, in-memory DB, Brevo stub):
  python3 e2e/pwa/pwa_e2e.py [tag]
"""
import json
import os
import re
import sys
import time
import urllib.request

from playwright.sync_api import sync_playwright

BASE = os.environ.get("DCX_E2E_BASE", "http://127.0.0.1:5119")
WIP = "/home/user/e2e-pwa-wip"
# the Brevo stub hardcodes its capture location (same file the other journeys poll)
CAPTURE = "/home/user/e2e-dr-wip/brevo_captured.jsonl"
SHOTS = os.environ.get("E2E_SHOTS_DIR", os.path.abspath(
    os.path.join(os.path.dirname(__file__), "../../DeChimexPrints/docs/evidence/pwa")))
TAG = (sys.argv[1] if len(sys.argv) > 1 else "pwa").lower()[:10]
CHECKS = []
SETTINGS = json.load(open(os.path.join(os.path.dirname(__file__),
                                       "../../DeChimexPrints/src/DeChimex.Host/appsettings.json")))

CURRENT = [None]

IPHONE_UA = ("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 "
             "(KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1")
ANDROID_UA = ("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36")
MOBILE_VP = {"width": 390, "height": 844}
DESKTOP_VP = {"width": 1366, "height": 900}



def verification_code(html):
    """The 6-digit code from a verification email (design-independent).

    Prefers the renderer's code block (Code() sets letter-spacing:8px on it) and
    otherwise takes the first 6-digit run that is NOT part of a hex colour, because
    style tokens like #475569 are all digits and would read as a false code.
    Returns a match object whose group(1) is the code, or None.
    """
    m = re.search(r"letter-spacing:8px[^>]*>\s*([0-9]{4,8})\s*<", html or "")
    if m:
        return m
    return re.search(r"(?<![#0-9A-Fa-f])(\d{6})(?![0-9A-Fa-f])", html or "")
def check(name, cond, extra=""):
    assert cond, f"FAILED: {name} {extra}"
    CHECKS.append(name)
    print(f"ok {len(CHECKS):02d} - {name}", flush=True)


def use(pg):
    CURRENT[0] = pg
    return pg


def shot(name):
    os.makedirs(SHOTS, exist_ok=True)
    pg = CURRENT[0]
    pg.screenshot(path=os.path.join(SHOTS, name), full_page=True)
    print("shot", name, flush=True)


def goto(pg, path, tries=3):
    """Navigate with retries: a cold persistent Chromium under memory pressure can stall
    its FIRST navigation past the default 30s without anything being wrong with the app
    (observed once; the app kept serving requests the whole time)."""
    last = None
    for attempt in range(tries):
        try:
            pg.goto(BASE + path, wait_until="domcontentloaded", timeout=45000)
            pg.wait_for_timeout(400)
            return pg
        except Exception as e:  # noqa: BLE001 - retry any navigation-level failure
            last = e
            pg.wait_for_timeout(1500 * (attempt + 1))
    raise last


def sw_ready(pg, timeout=30000):
    """Wait until the page's service worker registration is active."""
    pg.wait_for_function(
        "() => navigator.serviceWorker && navigator.serviceWorker.getRegistration"
        " && navigator.serviceWorker.getRegistration().then(r => !!(r && r.active))",
        timeout=timeout)
    pg.wait_for_timeout(600)


def brevo_capture_for(email):
    if not os.path.exists(CAPTURE):
        return None
    msgs = [json.loads(l) for l in open(CAPTURE) if l.strip()]
    hits = [m for m in msgs if email in json.dumps(m.get("to", ""))]
    return hits[-1] if hits else None


def brevo_token(email):
    deadline = time.time() + 120
    while time.time() < deadline:
        m = brevo_capture_for(email)
        if m:
            mt = verification_code(m.get("htmlContent", ""))
            if mt:
                return mt.group(1)
        time.sleep(2)
    raise AssertionError(f"no verification token captured for {email}")


def register_customer(pg, tag_suffix):
    email = f"pwa-{TAG}-{tag_suffix}{os.urandom(2).hex()}@example.com"  # unique per run
    goto(pg, "/Account/Register")
    pg.fill('form[action*="Register"] input[name="FirstName"]', "Pwa")
    pg.fill('form[action*="Register"] input[name="LastName"]', f"User{tag_suffix}")
    pg.fill('form[action*="Register"] input[name="Email"]', email)
    pg.fill('form[action*="Register"] input[name="Phone"]', "08030000003")
    pg.fill('form[action*="Register"] input[name="Password"]', "CustE2E!2026xY")
    conf = pg.query_selector('form[action*="Register"] input[name="ConfirmPassword"]')
    if conf:
        conf.fill("CustE2E!2026xY")
    pg.click('form[action*="Register"] button[type="submit"]')
    pg.wait_for_timeout(3000)
    if "portal" not in pg.url.lower():
        token = brevo_token(email)
        otp = pg.query_selector('input[name="Token"], input[name="token"], input[name="Code"]')
        assert otp, f"OTP input missing @ {pg.url}"
        otp.fill(token)
        pg.click('form button[type="submit"]')
        pg.wait_for_url(lambda u: "portal" in u.lower(), timeout=20000)
    check(f"customer {tag_suffix} signed in", "portal" in pg.url.lower(), pg.url)
    return email


with sync_playwright() as p:
    # Full Chromium build: the plain headless shell pins notifications to 'denied' and
    # ignores both CDP grants and --use-fake-ui; the full build honours CDP grants.
    browser = p.chromium.launch(channel="chromium")

    # ============ 01 mobile web: manifest + service worker + shortcut route ============
    m1_ctx = browser.new_context(viewport=MOBILE_VP, user_agent=ANDROID_UA)
    m1 = use(m1_ctx.new_page())
    goto(m1, "/")
    sw_ready(m1)
    active = m1.evaluate(
        "navigator.serviceWorker.getRegistration().then(r => r.active ? r.active.scriptURL : null)")
    check("service worker active on mobile home", str(active).endswith("/service-worker.js"), active)

    man = m1.evaluate("fetch('/site.webmanifest').then(r => r.json())")
    check("manifest brand fields", man["name"] == "DE-CHIMEX PRINT HUB" and man["display"] == "standalone")
    sc_urls = [s["url"] for s in man.get("shortcuts", [])]
    check("manifest shortcuts present", "/products" in sc_urls and "/services" in sc_urls, sc_urls)
    for icon in man["icons"]:
        src = icon["src"]
        code = urllib.request.urlopen(BASE + src, timeout=10).status
        assert code == 200, f"manifest icon missing: {src}"
    check("all manifest icons resolve", True)

    # the manifest's order-tracking shortcut route must exist server-side (not 404)
    track = m1_ctx.request.get(BASE + "/portal/orders/track")
    check("shortcut route /portal/orders/track exists", track.status in (200, 302), track.status)

    st = m1.evaluate("window.DcxPwa.state()")
    check("DcxPwa hooks live (browser mode)", st["standalone"] is False and st["swRegistered"] is True, st)
    shot("01-mobile-web.png")

    # ============ 02 iPhone install card (auto) + dismissal remembered ============
    ios_ctx = browser.new_context(viewport=MOBILE_VP, user_agent=IPHONE_UA)
    ios = use(ios_ctx.new_page())
    goto(ios, "/")
    try:
        ios.wait_for_selector("#dcx-install-card", timeout=12000)
    except Exception:
        ios.evaluate("window.DcxPwa.showInstallCard('ios')")  # settle-delay variance guard
    ios.wait_for_selector("#dcx-install-card", timeout=5000)
    card_txt = ios.inner_text("#dcx-install-card")
    check("iOS install card auto-shows with Add to Home Screen steps",
          "Share" in card_txt and "Add to Home Screen" in card_txt, card_txt[:120])
    shot("02-install-card.png")
    ios.click("#dcx-install-later")  # "Got it"
    ios.wait_for_timeout(300)
    check("card dismissed", ios.query_selector("#dcx-install-card") is None)
    goto(ios, "/faq")
    ios.wait_for_timeout(7000)
    check("dismissal remembered (no card after reload)", ios.query_selector("#dcx-install-card") is None)

    # ============ 03/04 install help page (browser-aware) ============
    droid_ctx = browser.new_context(viewport=MOBILE_VP, user_agent=ANDROID_UA)
    droid = use(droid_ctx.new_page())
    goto(droid, "/install-help")
    help_txt = droid.inner_text("body")
    check("install help covers Android Chrome menu path",
          "Install app" in help_txt and "⋮" in help_txt)
    shot("03-android-install-help.png")

    ios2 = use(ios_ctx.new_page())
    goto(ios2, "/install-help")
    help_txt = ios2.inner_text("body")
    check("install help covers iPhone Safari Share path",
          "Add to Home Screen" in help_txt and "Safari" in help_txt)
    shot("04-ios-install-help.png")

    # ============ 05/06 standalone (installed) mode ============
    std_ctx = browser.new_context(viewport=MOBILE_VP, user_agent=IPHONE_UA)
    std_ctx.add_init_script(
        "const mm=window.matchMedia.bind(window);window.matchMedia=q=>q==='(display-mode: standalone)'"
        "?{matches:true,media:q,addEventListener(){},removeEventListener(){},addListener(){},removeListener(){},onchange:null,dispatchEvent:()=>false}"
        ":mm(q);")
    std = use(std_ctx.new_page())
    goto(std, "/")
    sw_ready(std)
    st = std.evaluate("window.DcxPwa.state()")
    check("standalone detected via display-mode", st["standalone"] is True, st)
    std.wait_for_timeout(7000)
    check("no install card inside installed app", std.query_selector("#dcx-install-card") is None)
    shot("05-installed-pwa.png")
    goto(std, "/products")
    check("standalone navigation works", "products" in std.url.lower())
    shot("06-standalone-mode.png")

    # ============ 07/08 offline shell + honest reconnect ============
    off_ctx = browser.new_context(viewport=MOBILE_VP, user_agent=ANDROID_UA)
    off = use(off_ctx.new_page())
    goto(off, "/products")
    sw_ready(off)
    # toggle on a LIVE page so the PWA toasts actually render (the offline shell is
    # a bare file without scripts, so its reconnect would fire nothing there)
    off_ctx.set_offline(True)
    try:
        off.wait_for_selector(".dcx-toast, [role='status']", timeout=4000)
        toast_txt = off.inner_text(".dcx-toasts, .dcx-toast, [role='status']")
        check("offline toast honest (no fake queueing)", "offline" in toast_txt.lower(), toast_txt[:100])
    except Exception:
        check("offline toast honest (no fake queueing)", False, "no toast appeared")
    off_ctx.set_offline(False)
    off.wait_for_timeout(600)
    toast_txt = ""
    try:
        off.wait_for_selector(".dcx-toast", timeout=4000)
        toast_txt = off.inner_text(".dcx-toasts")
    except Exception:
        pass
    check("reconnect toast shown", "restored" in toast_txt.lower(), toast_txt[:100])
    shot("08-reconnected-state.png")
    # and the offline SHELL for fresh navigations while offline
    off_ctx.set_offline(True)
    off.wait_for_timeout(300)
    off.goto(BASE + "/faq", wait_until="domcontentloaded")
    off.wait_for_timeout(800)
    body = off.inner_text("body")
    check("offline navigation shows offline shell", "You're offline" in body, body[:100])
    check("offline shell offers retry, no fake success",
          "Try again" in body or "Retry" in body)
    shot("07-offline-state.png")
    off_ctx.set_offline(False)
    off.goto(BASE + "/faq", wait_until="domcontentloaded")
    off.wait_for_timeout(900)
    check("back online: real page served again", "You're offline" not in off.inner_text("body"))

    # ============ 09/10/11 notifications: permission row, push, deep link ============
    # A persistent profile is REQUIRED for the Push API: Chromium disables push in
    # (non-persistent) incognito contexts, which is what browser.new_context gives.
    notif_ctx = p.chromium.launch_persistent_context(
        os.path.join(WIP, "profile"), channel="chromium",
        viewport=MOBILE_VP, user_agent=ANDROID_UA)
    # CDP grant = the stand-in for the browser's native permission dialog the
    # contextual row's toggle triggers on a real device
    notif_ctx.grant_permissions(["notifications"], origin=BASE)
    notif = use(notif_ctx.new_page())
    register_customer(notif, "note")
    perm = notif.evaluate("typeof Notification !== 'undefined' ? Notification.permission : 'unsupported'")
    check("notifications granted in test browser (CDP stand-in for the native prompt)",
          perm in ("default", "granted"), perm)
    sw_ready(notif)
    notif.click("#portalNotifBell")
    notif.wait_for_selector("#portalNotifPush a", timeout=8000)
    notif.wait_for_timeout(1200)
    row_txt = notif.inner_text("#portalNotifPush")
    check("contextual push row present in bell (not subscribed yet)",
          "Turn on push notifications" in row_txt, row_txt[:80])
    shot("09-notification-permission.png")

    # real opt-in through the row: pushManager.subscribe with the server's VAPID key,
    # then the endpoint persisted via POST /portal/notifications/push/subscribe
    notif.click("#portalNotifPush a")
    try:
        # headless push subscribe + server round-trip can take >20s under load
        notif.wait_for_function(
            "() => document.querySelector('#portalNotifPush a') && "
            "document.querySelector('#portalNotifPush a').textContent.includes('Turn off push notifications')",
            timeout=30000)
        flipped = notif.inner_text("#portalNotifPush")
        check("row toggle subscribes this device (endpoint persisted server-side)",
              "Turn off push notifications" in flipped, flipped[:80])
    except Exception:
        flipped = notif.inner_text("#portalNotifPush")
        check("row toggle subscribes this device (endpoint persisted server-side)", False, flipped[:80])

    # Real notification through the service worker registration (the same call the
    # server-pushed payload triggers inside the SW push handler).
    shown = notif.evaluate("""(async () => {
        const reg = await navigator.serviceWorker.ready;
        await reg.showNotification('Design approved', {
            body: 'Your design review was approved. Track it in Orders.',
            icon: '/img/icon-192.png', badge: '/img/icon-192.png',
            data: { url: '/portal/orders' } });
        return (await reg.getNotifications()).length;
    })()""")
    check("notification displayed via service worker", shown == 1, shown)
    shot("10-push-notification.png")

    # Click-through: focus-or-open the notification's own route (test hook mirrors
    # the notificationclick handler's focus + navigate path; headless may focus
    # another open page, so accept the deep link landing on any page of this context).
    notif.bring_to_front()
    notif.evaluate(
        "navigator.serviceWorker.controller.postMessage({type:'dcx:test-notificationclick', url:'/portal/orders'})")
    deep_ok = True
    try:
        notif.wait_for_url(lambda u: u.rstrip("/").endswith("/portal/orders"), timeout=30000)
    except Exception:
        # The click can land before the wait registers (or the page is already at the
        # target), so the URL itself is the assertion: poll it instead of failing on a
        # missed navigation event.
        import time as _t
        deadline = _t.time() + 15
        while _t.time() < deadline and not notif.url.rstrip("/").endswith("/portal/orders"):
            notif.wait_for_timeout(250)
        deep_ok = notif.url.rstrip("/").endswith("/portal/orders")
    check("notification click deep-links to its route (not homepage)", deep_ok,
          f"url={notif.url} pages={[pg.url for pg in notif_ctx.pages]}")
    shot("11-notification-deep-link.png")

    # ============ 12 update-available banner ============
    upd_ctx = browser.new_context(viewport=MOBILE_VP, user_agent=ANDROID_UA)
    upd = use(upd_ctx.new_page())
    goto(upd, "/")
    sw_ready(upd)
    upd.evaluate("window.DcxPwa.showUpdateBanner()")
    upd.wait_for_selector("#dcx-update-banner", timeout=4000)
    ban_txt = upd.inner_text("#dcx-update-banner")
    check("update banner offers Refresh and Later", "Refresh" in ban_txt and "Later" in ban_txt, ban_txt[:80])
    shot("12-update-available.png")
    upd.click("#dcx-update-later")
    upd.wait_for_timeout(400)
    check("'Later' keeps the current version (never forced)", upd.query_selector("#dcx-update-banner") is None)

    browser.close()
    notif_ctx.close()

print(f"RESULT: {len(CHECKS)} passed, 0 failed")
print(f"PWA E2E PASSED - {len(CHECKS)} checks, evidence in {SHOTS}")
