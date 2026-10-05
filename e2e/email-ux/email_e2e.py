#!/usr/bin/env python3
"""Email design-system E2E — every template, live sends, and the app side by side.

Proves the email UI is the DeChimex Hub UI translated into email, per
EMAIL_DESIGN_STANDARD.md:

  A. RENDERED TEMPLATES (all 19, straight from EmailTemplateRenderer via the unit
     test that writes EMAIL_PREVIEW_DIR): every colour exists in the app stylesheets
     (app.css :root / customer.css .cs-*), the retired pre-redesign palette is gone,
     layout is email-safe (tables, single 600px container, media query, no flex or
     absolute positioning), the type stack is the app's Inter stack, the header is
     the app brand lockup with the real logo, the footer is the slim legal bar
     (branding, contact, policies, copyright only), copy carries no em/en dashes and
     no leftover {{placeholders}}, CTAs are bulletproof buttons with a link fallback,
     and text/background pairs meet WCAG AA contrast.
  B. LIVE SENDS captured by the Brevo stub during this run (checkout, custom order,
     payments, registration ...): every captured body passes the same design
     invariants, so the invariants hold for the real send path and not just the
     renderer in isolation.
  C. LIVE DRIVES for the templates the other journeys do not reach: forget password,
     contact message, staff contact reply, provider test email, workspace message
     notification and design proof. Each drive must produce a captured email that
     passes the invariants, and every CTA link must resolve on the app (2xx/3xx).
  D. SCREENSHOTS: desktop 1280, mobile 390, dark-scheme 390, narrow 320 for the
     longest templates, plus app reference shots (storefront, auth) so the
     final evidence shows email and app in the same frame of reference.

Verify:  python3 e2e/email-ux/email_e2e.py <tag>
Audit:   EMAIL_MODE=audit python3 e2e/email-ux/email_e2e.py <tag>   (records, never fails)
Env:     E2E_PREVIEW_DIR  rendered previews (default <root>/email-previews)
         E2E_SHOTS_DIR    screenshots    (default <root>/evidence)
"""
from html import unescape as html_unescape
from pathlib import Path
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

from playwright.sync_api import sync_playwright

ROOT = Path(os.environ.get("E2E_ROOT", str(Path(__file__).resolve().parents[2])))
BASE = os.environ.get("E2E_BASE_URL", "http://127.0.0.1:5119")
WIP = "/home/user/e2e-dr-wip"
CAPTURE = os.environ.get("E2E_BREVO_CAPTURE", os.path.join(WIP, "brevo_captured.jsonl"))
TAG = (sys.argv[1] if len(sys.argv) > 1 else "email")[:10]
MODE = os.environ.get("EMAIL_MODE", "verify").lower()
PREVIEWS = Path(os.environ.get("E2E_PREVIEW_DIR", str(ROOT / "email-previews")))
SHOTS = Path(os.environ.get("E2E_SHOTS_DIR", str(ROOT / "evidence")))
SHOTS.mkdir(parents=True, exist_ok=True)
SETTINGS = json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.json").read_text())
SETTINGS.update(json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.Development.json").read_text()))
# The emails link to whatever origin the running app is configured with (production uses
# https://dechimexprinthub.com, the sandbox uses the dev origin). Both are legitimate.
APP_ORIGINS = {"dechimexprinthub.com", "www.dechimexprinthub.com",
               urllib.parse.urlsplit(SETTINGS.get("App", {}).get("FrontendBaseUrl", "")).netloc}

# The design system is mirrored from the app's own stylesheets. Reading them here (not
# hard-coding a copy) means the harness fails the moment an email invents a colour.
CSS = ROOT / "DeChimexPrints/src/DeChimex.Host/wwwroot/css"
APP_PALETTE = {m.upper() for f in ("app.css", "customer.css")
               for m in re.findall(r"#[0-9A-Fa-f]{6}\b", (CSS / f).read_text())}
RETIRED = ["#7C3AED", "#F6F5FC", "#3B3752", "#1E1B2E", "#6B6880", "#ECEAF5",
           "#F3F0FF", "#EC4899", "#F97316"]        # pre-redesign email palette
SHELL_MARKERS = ["<!DOCTYPE html>", "role='presentation'", "max-width:600px",
                 "'Inter'", "name='color-scheme' content='light'", "Creativity with Ease.",
                 "<title>", "img/logo.png", ".dc-body h1"]

PASS = []
FAIL = []


def check(name, cond, extra=""):
    if cond:
        PASS.append(name)
        print(f"ok {len(PASS):02d} - {name}", flush=True)
    else:
        FAIL.append(name)
        print(f"FAIL - {name} {extra}", flush=True)
        if MODE != "audit":
            raise AssertionError(f"{name} {extra}")


def ratio(fg, bg):
    def lum(h):
        h = h.lstrip("#")
        c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
        c = [x / 12.92 if x <= 0.03928 else ((x + 0.055) / 1.055) ** 2.4 for x in c]
        return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]
    a, b = lum(fg), lum(bg)
    lo, hi = min(a, b), max(a, b)
    return round((hi + 0.05) / (lo + 0.05), 2)


# ---------------------------------------------------------------------------
# A. rendered templates
# ---------------------------------------------------------------------------
def audit_html(name, html, expect_unsubscribe=False):
    """Design invariants for one rendered email body."""
    check(f"{name}: app palette only",
          all(c.upper() in APP_PALETTE for c in re.findall(r"#[0-9A-Fa-f]{6}\b", html)),
          str(sorted({c for c in re.findall(r'#[0-9A-Fa-f]{6}\b', html) if c.upper() not in APP_PALETTE})))
    check(f"{name}: pre-redesign palette gone", not any(d in html for d in RETIRED),
          str([d for d in RETIRED if d in html]))
    check(f"{name}: shell intact", all(m in html for m in SHELL_MARKERS))
    check(f"{name}: no unresolved placeholders", "{{" not in html and "}}" not in html)
    check(f"{name}: no em or en dash", "\u2014" not in html and "\u2013" not in html)
    check(f"{name}: email-safe layout",
          "display:flex" not in html and "position:absolute" not in html and "grid" not in html.split("<style>")[0])
    check(f"{name}: responsive rules + mobile CTA", "@media only screen and (max-width:620px)" in html
          and ".dc-btn" in html and "dc-pad" in html)
    check(f"{name}: brand lockup with real logo",
          re.search(r"src='https?://[^']+/img/logo\.png'", html) is not None and "alt=''" in html
          and "DE-CHIMEX" in html and "PRINT HUB" in html)
    footer = html.rsplit("background-color:#FCFBF9", 1)[-1] if "#FCFBF9" in html else ""
    check(f"{name}: slim footer only (brand, contact, policies, copyright)",
          footer.count("<p ") <= 4 and "Privacy" in footer and "Terms" in footer
          and "hello@dechimexprinthub.com" in footer and "All rights reserved" in footer
          and "Creativity with Ease." in footer,
          f"paragraphs={footer.count('<p ')}")
    check(f"{name}: footer is not an oversized corporate block",
          footer.count("<a ") <= 6 and len(footer) < 1800, f"links={footer.count('<a ')} len={len(footer)}")
    cta = re.search(r"<td align='center' bgcolor='(#[0-9A-F]{6})'", html)
    if cta:
        check(f"{name}: CTA uses --primary and keeps a link fallback",
              cta.group(1) == "#4338CA" and "Button not working? Copy this link:" in html)
    check(f"{name}: unsubscribe only when marketing", ("Unsubscribe" in html) == expect_unsubscribe)
    return html.split("class='dc-pad dc-body'")[1] if "class='dc-pad dc-body'" in html else html


def preview_text(name):
    txt = PREVIEWS / f"{Path(name).stem}.txt"
    return txt.read_text(encoding="utf-8-sig") if txt.exists() else None


def section_a():
    pages = sorted(PREVIEWS.glob("*.html"))
    check("A1 all 19 rendered templates present", len(pages) == 19, f"found {len(pages)}: {[p.name for p in pages]}")
    for page in pages:
        html = page.read_text()
        audit_html(page.stem, html, expect_unsubscribe="Marketing" in page.stem)
        plain = preview_text(page.name)
        if plain is not None:
            # Template markup must be gone; a literal "<b>" typed by a user is data
            # and stays verbatim, exactly like the renderer test documents.
            check(f"{page.stem}: plain-text part is markup-free",
                  not re.search(r"</?(p|td|tr|table|div|span|a|h1|h2|h3|img)\b", plain)
                  and "{{" not in plain and "\u2014" not in plain and "\u2013" not in plain)
            check(f"{page.stem}: plain text is complete", len(plain.strip()) > 40)
            # Customer-facing templates must keep their actions reachable in text mode.
            # (Otp is a pure code: no link by design.)
            if any(k in page.stem for k in ("Welcome", "EmailVerification", "ForgotPassword",
                                            "OrderConfirmation", "OrderStatusUpdate",
                                            "PaymentReceipt", "PaymentFailed", "OrderRequestReceived",
                                            "OrderMessageNotification", "OrderQuoteReady",
                                            "DesignProofReady", "DesignProofApproved")):
                check(f"{page.stem}: plain text keeps the app link",
                      "dechimexprinthub.com" in plain)

    # Contrast (AA) over the token pairs the shell actually uses.
    pairs = {
        "body on card": ("#475569", "#FFFFFF", 4.5),
        "muted on card": ("#64748B", "#FFFFFF", 4.5),
        "heading on card": ("#17161C", "#FFFFFF", 4.5),
        "CTA label on --primary": ("#FFFFFF", "#4338CA", 4.5),
        "link on card": ("#4338CA", "#FFFFFF", 4.5),
        "info pill on --primary-50": ("#3730A3", "#EEF2FF", 4.5),
        "ok pill": ("#065F46", "#ECFDF5", 4.5),
        "warn pill": ("#92400E", "#FFFBEB", 4.5),
        "bad pill": ("#991B1B", "#FEF2F2", 4.5),
        "alert info text on #EFF6FF": ("#1E40AF", "#EFF6FF", 4.5),
        "amount on --primary-50": ("#312E81", "#EEF2FF", 4.5),
    }
    for label, (fg, bg, need) in pairs.items():
        r = ratio(fg, bg)
        check(f"A2 contrast {label} {r}:1 >= {need}", r >= need, f"{r}:1")


# ---------------------------------------------------------------------------
# B. live captured sends
# ---------------------------------------------------------------------------
def captures():
    if not os.path.exists(CAPTURE):
        return []
    out = []
    for line in open(CAPTURE):
        try:
            out.append(json.loads(line))
        except Exception:
            pass
    return out


def section_b(min_templates):
    rows = captures()
    check("B1 stub captured live sends", len(rows) >= min_templates, f"captured={len(rows)}")
    subjects = {}
    for row in rows:
        subject = row.get("subject", "?")
        subjects.setdefault(subject, 0)
        subjects[subject] += 1
        html = row.get("htmlContent", "")
        check(f"B2 live send is markup and shell ({subject[:38]})",
              all(m in html for m in SHELL_MARKERS) and "{{" not in html)
        check(f"B2 live send keeps app palette ({subject[:38]})",
              all(c.upper() in APP_PALETTE for c in re.findall(r"#[0-9A-Fa-f]{6}\b", html)))
        text = row.get("textContent", "")
        check(f"B2 live plain text is markup-free ({subject[:38]})",
              bool(text.strip()) and not re.search(r"<\w+[ >/]", text) and "{{" not in text)
        check(f"B2 live send has a recipient ({subject[:38]})",
              bool(row.get("to")) and "@" in json.dumps(row.get("to")))
    print(f"    live subjects: {sorted(subjects)}", flush=True)
    return set(subjects)


def section_b_links():
    """Every CTA/link in the live sends points at a real route on the app."""
    rows = captures()
    checked = 0
    for row in rows[-12:]:
        html = row.get("htmlContent", "")
        for href in re.findall(r"href='([^']+)'", html):
            href = html_unescape(href)
            if not href.startswith("http") or "img/logo" in href:
                continue
            path = urllib.parse.urlsplit(href)
            if path.netloc not in APP_ORIGINS:
                continue
            url = BASE + (path.path or "/") + (("?" + path.query) if path.query else "")
            req = urllib.request.Request(url, headers={"User-Agent": "email-ux-e2e"})
            try:
                code = urllib.request.urlopen(req, timeout=15).status
            except urllib.error.HTTPError as e:
                code = e.code
            except Exception:
                code = 0
            checked += 1
            check(f"B3 email link resolves on the app ({path.path[:44]}) -> {code}", code in (200, 302, 303))
    check("B3 link sweep actually looked at links", checked >= 5, f"checked={checked}")


# ---------------------------------------------------------------------------
# C. live drives for the remaining templates
# ---------------------------------------------------------------------------
def wait_capture(predicate, needle, timeout=60, since=None):
    """Wait for a captured message (new sends only) matching subject/body needle."""
    import time
    if since is None:
        since = len(captures())
    deadline = time.time() + timeout
    while time.time() < deadline:
        for row in captures()[since:]:
            if predicate(row) and needle in json.dumps(row):
                return row
        time.sleep(1.5)
    return None


def form_post(page, path, fields):
    """Post an MVC form with its antiforgery token (page must show the form)."""
    token = page.locator('input[name="__RequestVerificationToken"]').first.input_value()
    return page.request.post(BASE + path, form={"__RequestVerificationToken": token, **fields})


def section_c(browser):
    # Separate sessions on purpose: the customer is signed in on one context while the
    # admin/staff screens need their own clean session (a logged-in customer visiting
    # /Account/Login is redirected, so the login form only exists in a fresh context).
    cust_ctx = browser.new_context(viewport={"width": 1440, "height": 1000})
    page = cust_ctx.new_page()
    # Use the same proven helper layers as the other harnesses.
    helper = ROOT / "e2e/design-review/ws_designreview_e2e.py"
    h = {"__file__": str(helper)}
    exec(compile(helper.read_text().split("\nwith sync_playwright()")[0], str(helper), "exec"), h)
    h["TAG"] = TAG
    goto = h["goto"]
    owner_login = h["owner_login"]
    register_customer = h["register_customer"]

    # --- customer (register + verify: Welcome/EmailVerification already captured) ---
    cust = page
    email = register_customer(cust, "mail")

    # --- Forgot password (public form) ---
    since = len(captures())
    goto(cust, "/Account/ForgotPassword")
    form_post(cust, "/Account/ForgotPassword", {"Email": email})
    row = wait_capture(lambda r: "reset" in r.get("subject", "").lower(),
                       "/Account/ResetPassword", since=since)
    check("C1 forgot-password email rendered + captured", row is not None)
    if row:
        audit_html("live-ForgotPassword", row["htmlContent"])

    # --- Contact form (public) -> staff inbox ---
    since = len(captures())
    goto(cust, "/contact")
    form_post(cust, "/contact", {
        "name": f"Mail Tester {TAG}", "email": email, "phone": "08031234567",
        "service": "Corporate branding",
        "details": f"Email design E2E {TAG}: please confirm the new email look.",
    })
    row = wait_capture(lambda r: "contact form message" in r.get("subject", "").lower(),
                       f"Mail Tester {TAG}", since=since)
    check("C2 contact enquiry email rendered + captured", row is not None)
    if row:
        audit_html("live-ContactMessageReceived", row["htmlContent"])

    # --- Provider test email from admin settings ---
    staff_ctx = browser.new_context(viewport={"width": 1440, "height": 1000})
    staff = staff_ctx.new_page()
    owner_login(staff)
    since = len(captures())
    goto(staff, "/admin/settings")
    clicked = False
    for label in ("Send test email", "Test email", "Send a test email"):
        btn = staff.locator(f"button:has-text('{label}'), a:has-text('{label}')").first
        if btn.count():
            btn.click()
            staff.wait_for_timeout(2500)
            clicked = True
            break
    check("C3 admin settings offers the provider test send", clicked)
    row = wait_capture(lambda r: "test email" in r.get("subject", "").lower(),
                       "Test email", timeout=45, since=since)
    check("C3 provider test email rendered + captured", row is not None)
    if row:
        audit_html("live-ProviderTestEmail", row["htmlContent"])

    # --- Staff reply to the contact message -> ContactReply ---
    since = len(captures())
    goto(staff, "/admin/messages")
    row_link = staff.locator("tr").filter(has_text=f"Mail Tester {TAG}").first
    check("C4 contact enquiry listed for staff", row_link.count() > 0)
    if row_link.count():
        row_link.locator("a").first.click()
        staff.wait_for_load_state("networkidle")
        # Subject is required by the reply form and is not pre-filled for a fresh enquiry.
        staff.fill('input[name="Subject"]', f"Re: your enquiry {TAG}")
        staff.fill('textarea[name="Body"]', f"Thanks for the note {TAG}. Your flasks quote follows shortly.")
        with staff.expect_navigation(wait_until="networkidle"):
            staff.click('form[action*="reply"] button[type="submit"]')
        staff.wait_for_timeout(2000)
    row = wait_capture(lambda r: r.get("subject", "").startswith("Re:"),
                       "flasks quote follows shortly", since=since)
    check("C4 staff contact reply rendered + captured", row is not None)
    if row:
        audit_html("live-ContactReply", row["htmlContent"])

    # --- Direct checkout + payment -> OrderConfirmation + PaymentReceipt ---
    m_helper = ROOT / "e2e/major-categories/workflow.py"
    m = {"__file__": str(m_helper)}
    exec(compile(m_helper.read_text().split("\nwith sync_playwright()")[0], str(m_helper), "exec"), m)
    dc_helper = ROOT / "e2e/direct-checkout/workflow.py"
    dc = {"__file__": str(dc_helper)}
    exec(compile(dc_helper.read_text().split("\nwith sync_playwright()")[0], str(dc_helper), "exec"), dc)
    dc["TAG"] = TAG
    dc["STUB"] = os.environ.get("E2E_STUB_URL", "http://127.0.0.1:9090")
    create_category = m["create_category"]
    create_product = m["create_product"]
    submit_request = m["order"]

    since = len(captures())
    shop_cat = create_category(staff, f"E2E Mail Shop {TAG}")
    shop_prod = dc["create_product"](staff, f"E2E Mail Mug {TAG}", shop_cat, 9500, False)
    dc["add_to_cart"](cust, shop_prod)
    order = dc["place_checkout_order"](cust, f"Email E2E {TAG} checkout")
    dc["pay_order"](cust, order, "success")
    # The email carries the order NUMBER (DCX-...), not the GUID, so match on the
    # subject within this drive's recency window.
    row = wait_capture(lambda r: "is confirmed" in r.get("subject", ""), "", since=since)
    check("C0 order confirmation email rendered + captured", row is not None)
    if row:
        audit_html("live-OrderConfirmation", row["htmlContent"])
    row = wait_capture(lambda r: "payment receipt" in r.get("subject", "").lower(),
                       "", since=since)
    check("C0 payment receipt email rendered + captured", row is not None)
    if row:
        audit_html("live-PaymentReceipt", row["htmlContent"])

    # --- Custom order: staff workspace message -> OrderMessageNotification ---
    cat = create_category(staff, f"E2E Mail {TAG}")
    m["options"](staff, cat)  # ordering question the custom-request form requires
    prod = create_product(staff, f"E2E Mail Tee {TAG}", cat, 12000)
    ws_url = submit_request(cust, prod, f"Email E2E {TAG} personalisation")
    order_id = re.search(r"/portal/orders/([0-9a-fA-F-]{36})", ws_url).group(1)

    since = len(captures())
    staff_ws = staff_ctx.new_page()
    goto(staff_ws, f"/admin/orders/{order_id}/workspace")
    staff_ws.fill('#chatInput', f"Hello from the studio {TAG}: your artwork is queued.")
    staff_ws.click('#chatSend')
    staff_ws.wait_for_timeout(3000)
    row = wait_capture(lambda r: "new message on your order" in r.get("subject", "").lower(),
                       "your artwork is queued", since=since)
    check("C5 workspace message notification rendered + captured", row is not None)
    if row:
        audit_html("live-OrderMessageNotification", row["htmlContent"])

    # --- Design proof upload -> DesignProofReady ---
    since = len(captures())
    proof = ROOT / "DeChimexPrints/src/DeChimex.Host/wwwroot/img/icon-192.png"
    staff_ws.set_input_files('form[action$="/proof"] input[name="file"]', str(proof))
    staff_ws.fill('form[action$="/proof"] input[name="staffNotes"]', f"Proof v1 for {TAG}")
    staff_ws.click('form[action$="/proof"] button[type="submit"]')
    staff_ws.wait_for_timeout(3500)
    # The proof email carries no staff notes, so match the recipient instead.
    row = wait_capture(lambda r: "design proof" in r.get("subject", "").lower(),
                       email, since=since)
    check("C6 design-proof email rendered + captured", row is not None)
    if row:
        audit_html("live-DesignProofReady", row["htmlContent"])

    # --- Unsubscribe landing page matches the email brand lockup ---
    anon_ctx = browser.new_context(viewport={"width": 1200, "height": 900})
    unsub = anon_ctx.new_page()
    goto(unsub, "/unsubscribe/00000000-0000-0000-0000-000000000000")
    body_html = unsub.content()
    check("C7 unsubscribe page uses the app brand lockup",
          "brand-grad" in body_html and "PRINT HUB" in body_html and "#7C3AED" not in body_html)
    unsub.close()
    anon_ctx.close()
    staff.close()
    staff_ws.close()
    staff_ctx.close()
    cust_ctx.close()


# ---------------------------------------------------------------------------
# D. screenshots — email designs next to the real app surfaces
# ---------------------------------------------------------------------------
def section_d(browser):
    pages = sorted(PREVIEWS.glob("*.html"))
    for width, label in ((1280, "desktop"), (390, "mobile")):
        ctx = browser.new_context(viewport={"width": width, "height": 800}, device_scale_factor=2)
        pg = ctx.new_page()
        for html in pages:
            pg.goto(html.resolve().as_uri())
            pg.screenshot(path=str(SHOTS / f"{html.stem}-{label}.png"), full_page=True)
        ctx.close()
    # dark-scheme behaviour (Gmail/Apple Mail dark mode) for the two most-used emails
    ctx = browser.new_context(viewport={"width": 390, "height": 800}, device_scale_factor=2,
                              color_scheme="dark")
    pg = ctx.new_page()
    for stem in ("06-OrderConfirmation", "02-EmailVerification"):
        src = PREVIEWS / f"{stem}.html"
        if src.exists():
            pg.goto(src.resolve().as_uri())
            pg.screenshot(path=str(SHOTS / f"{stem}-dark.png"), full_page=True)
    ctx.close()
    # narrow 320 pass on the longest bodies
    ctx = browser.new_context(viewport={"width": 320, "height": 720}, device_scale_factor=2)
    pg = ctx.new_page()
    for stem in ("06-OrderConfirmation", "16-ContactReply"):
        src = PREVIEWS / f"{stem}.html"
        if src.exists():
            pg.goto(src.resolve().as_uri())
            pg.screenshot(path=str(SHOTS / f"{stem}-320px.png"), full_page=True)
    ctx.close()
    # app reference shots: the same visual language, live
    ctx = browser.new_context(viewport={"width": 1280, "height": 900}, device_scale_factor=2)
    pg = ctx.new_page()
    for name, path in (("app-reference-home", "/"), ("app-reference-auth", "/Account/Login")):
        pg.goto(BASE + path, wait_until="networkidle")
        pg.screenshot(path=str(SHOTS / f"{name}.png"))
    ctx.close()


def main():
    print(f"email design E2E (tag={TAG}, mode={MODE})", flush=True)
    section_a()
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        section_c(browser)         # live drives first: they fill the capture file
        browser.close()
    section_b(min_templates=10)  # then every captured send is judged
    section_b_links()
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        section_d(browser)
        browser.close()
    covered = sorted({Path(p.stem.split("-", 1)[1]).name for p in PREVIEWS.glob("*.html")})
    print(f"    rendered templates: {covered}", flush=True)
    print(f"RESULT: {len(PASS)} passed, {len(FAIL)} failed (TAG={TAG})")
    if FAIL and MODE != "audit":
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
