"""Placeholder & generated-string format verification (Placeholder Audit task, 2026-10-04).

Runs against the SAME live app the other final-run stages use. Verifies that every
user-facing placeholder corrected by the audit renders server-side with an example
that matches the REAL generated/accepted format, and that representative untouched
placeholders are intact:

  generated formats (traced):  order DCX-2026-000042 · payment PAY-20261004123456-XXXXXXXXXX
  policies:                    password 8+ with A-Z/a-z/0-9 · contact email example standardised
  search honesty:              admin orders searches the order number ONLY

PRIMARY=placeholders publishes 5 ordered evidence shots to the final-run evidence dir;
other primaries run it as a regression with shots in /tmp.
"""
import json
import os
import re
import sys
import time

from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:5119"
WIP = "/home/user/e2e-dr-wip"
CAPTURE = os.path.join(WIP, "brevo_captured.jsonl")
SHOTS = os.environ.get("E2E_SHOTS_DIR", "/tmp/ph-shots")
TAG = (sys.argv[1] if len(sys.argv) > 1 else "ph").lower()[:8]
CHECKS = []

CURRENT = [None]
DEV_EMAIL, DEV_PASS = "developer@dechimexprinthub.local", "DevPortal#2026!"



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
    CURRENT[0].screenshot(path=os.path.join(SHOTS, name), full_page=True)
    print("shot", name, flush=True)


def goto(pg, path):
    pg.goto(BASE + path, wait_until="domcontentloaded")
    pg.wait_for_timeout(350)
    return pg


def placeholder_of(pg, selector):
    el = pg.query_selector(selector)
    return el.get_attribute("placeholder") if el else None


def owner_login(pg):
    goto(pg, "/Account/Login")
    for pw in ("OwnerE2E!2026xY", "ChangeMe123!"):
        pg.fill('form[action*="Login"] input[name="Email"]', "owner@dechimexprinthub.com")
        pg.fill('form[action*="Login"] input[name="Password"]', pw)
        pg.click('form[action*="Login"] button[type="submit"]')
        pg.wait_for_timeout(2200)
        if "/Account/Login" not in pg.url:
            return
    raise AssertionError("owner login failed")


def register_customer(pg):
    email = f"ph-{TAG}-{int(time.time())}@example.com"
    goto(pg, "/Account/Register")
    pg.fill('form[action*="Register"] input[name="FirstName"]', "Audit")
    pg.fill('form[action*="Register"] input[name="LastName"]', "Placeholder")
    pg.fill('form[action*="Register"] input[name="Email"]', email)
    pg.fill('form[action*="Register"] input[name="Phone"]', "08030000021")
    pg.fill('form[action*="Register"] input[name="Password"]', "CustE2E!2026xY")
    conf = pg.query_selector('form[action*="Register"] input[name="ConfirmPassword"]')
    if conf:
        conf.fill("CustE2E!2026xY")
    pg.click('form[action*="Register"] button[type="submit"]')
    pg.wait_for_timeout(2500)
    if "portal" not in pg.url.lower():
        token = None
        deadline = time.time() + 120
        while time.time() < deadline and not token:
            try:
                for line in open(CAPTURE):
                    m = json.loads(line)
                    if email in json.dumps(m.get("to", "")):
                        mt = verification_code(m.get("htmlContent", ""))
                        token = mt.group(1) if mt else None
            except FileNotFoundError:
                pass
            if not token:
                time.sleep(2)
        assert token, f"no verification token captured for {email}"
        otp = pg.query_selector('input[name="Token"], input[name="token"], input[name="Code"]')
        otp.fill(token)
        pg.click('form button[type="submit"]')
        pg.wait_for_url(lambda u: "portal" in u.lower(), timeout=20000)
    check("customer registered and signed in", "portal" in pg.url.lower(), pg.url)
    return email


with sync_playwright() as p:
    browser = p.chromium.launch()

    # ---------- anonymous forms ----------
    anon = use(browser.new_context(viewport={"width": 1280, "height": 900}).new_page())

    goto(anon, "/portal/orders/track")
    check("track page is customer-only (redirects anonymous visitors to sign-in)",
          "/Account/Login" in anon.url, anon.url)

    goto(anon, "/Account/Register")
    check("register email placeholder", placeholder_of(anon, 'input[name="Email"]') == "you@example.com")
    ph = placeholder_of(anon, 'input[name="Password"]')
    check("register password placeholder states the FULL policy (8 chars, A-Z, a-z, 0-9)",
          ph == "At least 8 characters, incl. A-Z, a-z & 0-9", ph)
    check("register phone placeholder matches the accepted +… format",
          placeholder_of(anon, 'input[name="Phone"]') == "+234…")
    shot("02-register-password-policy.png")

    goto(anon, "/Account/Login")
    check("login email placeholder", placeholder_of(anon, 'input[name="Email"]') == "you@example.com")

    goto(anon, "/contact")
    check("contact email example standardised to you@example.com",
          placeholder_of(anon, "#c_email") == "you@example.com",
          placeholder_of(anon, "#c_email"))
    shot("03-contact-form.png")

    # ---------- staff/admin surfaces ----------
    admin_ctx = browser.new_context(viewport={"width": 1440, "height": 900})
    owner = use(admin_ctx.new_page())
    owner_login(owner)

    goto(owner, "/admin/orders")
    ph = placeholder_of(owner, 'input[name="search"]')
    check("admin orders search placeholder no longer promises a customer search "
          "(backend matches the order number only)", ph == "Search order number...", ph)
    shot("04-admin-orders-search.png")

    goto(owner, "/admin/invoices")
    check("admin invoices search placeholder lists everything it matches",
          placeholder_of(owner, 'input[name="search"]') == "Search invoice, order number or customer...",
          placeholder_of(owner, 'input[name="search"]'))

    goto(owner, "/admin/activity")
    ph = placeholder_of(owner, 'input[name="Order"]')
    check("activity order-number filter shows the real format", ph == "e.g. DCX-2026-000042", ph)
    check("activity actor filter unchanged (username or the literal System actor)",
          placeholder_of(owner, 'input[name="Actor"]') == "Username or System")
    check("activity type filter keeps the real slug example",
          placeholder_of(owner, 'input[name="Type"]') == "e.g. payment.succeeded")

    goto(owner, "/admin/audit/raw")
    ph = placeholder_of(owner, 'input[name="action"]')
    check("audit action filter examples match the stored lowercase values",
          ph == "e.g. create, update", ph)

    goto(owner, "/admin/procurement/create")
    ph = placeholder_of(owner, 'input[name="SourceOrderNumber"]')
    check("procurement linked-order field teaches the business order-number format "
          "(GUID input replaced by the fix journey)",
          ph == "e.g. DCX-2026-000042", ph)

    goto(owner, "/admin/users/create")
    ph = placeholder_of(owner, 'input[name="Password"]')
    check("staff-create password shows the same policy as customer registration",
          ph == "At least 8 characters, incl. A-Z, a-z & 0-9", ph)
    check("staff username example stays a valid lowercase username",
          placeholder_of(owner, 'input[name="Username"]') == "e.g. shakur (customers see this in chat)")

    # ---------- customer track page (signed-in view) ----------
    cust_ctx = browser.new_context(viewport={"width": 390, "height": 844})
    cust = use(cust_ctx.new_page())
    register_customer(cust)
    goto(cust, "/portal/orders/track")
    ph = placeholder_of(cust, "#orderNumber")
    check("signed-in track page shows the real order-number example", ph == "e.g. DCX-2026-000042", ph)
    shot("01-track-order-number.png")

    # ---------- developer maintenance reference ----------
    dev = use(browser.new_context(viewport={"width": 1440, "height": 900}).new_page())
    goto(dev, "/dev")
    if "/Account/Login" in dev.url:
        dev.fill('form[action*="Login"] input[name="Email"]', DEV_EMAIL)
        dev.fill('form[action*="Login"] input[name="Password"]', DEV_PASS)
        dev.click('form[action*="Login"] button[type="submit"]')
        dev.wait_for_timeout(2200)
    goto(dev, "/dev/maintenance")
    ph = placeholder_of(dev, 'input[name="reference"]')
    check("dev maintenance payment-reference placeholder uses the real PAY-… format",
          ph == "e.g. PAY-20261004123456-8F3A2C91D0", ph)
    shot("05-dev-maintenance-payment-reference.png")

    browser.close()


print(f"RESULT: {len(CHECKS)} passed, 0 failed")
