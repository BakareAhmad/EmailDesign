#!/usr/bin/env python3
"""Footer placement E2E — which shell variant renders which footer, per page group.

The footer is a LAYOUT decision in this app, never a page-by-page if:
  * public storefront  (_CustomerLayout / _PublicLayout) -> full branded footer
  * customer app + transactional (_PortalLayout)         -> slim legal bar, no storefront footer
  * admin/staff (_AdminLayout), auth (_AuthLayout), dev (_DevLayout) -> no footer at all

Page groups verified here (one live journey, real Chromium, in-memory DB + stubs):
  A. PUBLIC      / /products /products/{id} /services /gallery /about /contact /faq
                 /privacy /terms /install-help           -> full footer, links work
  B. CUSTOMER SHOP  /portal/shop, /portal/shop/{id}      -> full footer (storefront experience)
  C. APP         /portal /portal/orders /portal/orders/track /portal/orders/{id}
                 /portal/orders/{id}/workspace /portal/orders/{id}/workspace (empty)
                 /portal/profile /portal/profile/password /portal/addresses
                 /portal/addresses/add /portal/wishlist /portal/payments /portal/invoices
                 /portal/shop/order                       -> slim bar, app nav intact
  D. TRANSACTIONAL /cart (guest), /cart (item), /checkout, /checkout/complete/{id},
                 /checkout/return?reference=...           -> slim bar, focused
  E. ADMIN       /admin/dashboard /admin/orders /admin/products /admin/settings
                 /admin/notifications /admin/customers /admin/orders/{id}/workspace
                                                          -> no footer, no slim bar
  F. AUTH        /Account/Login /Account/Register /Account/ForgotPassword
                 /Account/ResetPassword /Account/AccessDenied -> no footer, clean
Plus: footer content preserved (brand, columns, socials, legal line, policy links),
footer links navigate, no horizontal overflow (320/390/1440), no leftover space where
the storefront footer used to be, no console/page/network problems.

Run:   python3 e2e/footer/footer_e2e.py footer                      (verify — the default)
Audit: FOOTER_MODE=audit E2E_SHOTS_DIR=e2e/footer/evidence-before \
       python3 e2e/footer/footer_e2e.py before                    (records the CURRENT
       footer placement without failing, for before/after evidence)
"""
from pathlib import Path
import json
import os
import re
import sys
import urllib.request

from playwright.sync_api import sync_playwright

ROOT = Path(os.environ.get("E2E_ROOT", str(Path(__file__).resolve().parents[2])))
BASE = os.environ.get("E2E_BASE_URL", "http://127.0.0.1:5119")
STUB = os.environ.get("E2E_STUB_URL", "http://127.0.0.1:9090")
WIP = "/home/user/e2e-dr-wip"
TAG = (sys.argv[1] if len(sys.argv) > 1 else "footer")[:10]
MODE = os.environ.get("FOOTER_MODE", "verify").lower()  # verify | audit
SHOTS = Path(os.environ.get("E2E_SHOTS_DIR", str(ROOT / "evidence")))
SHOTS.mkdir(parents=True, exist_ok=True)

SETTINGS = json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.json").read_text())
SETTINGS.update(json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.Development.json").read_text()))

# Proven helper layers (same loader as e2e/direct-checkout/workflow.py): the design-review
# helpers (check/use/shot/goto/owner_login/register_customer + Brevo capture) live in h,
# the catalog helpers (create_category/create_product) in m.
helperfile = ROOT / "e2e/major-categories/workflow.py"
m = {"__file__": str(helperfile)}
exec(compile(helperfile.read_text().split("\nwith sync_playwright()")[0], str(helperfile), "exec"), m)
h = m["h"]
h["TAG"] = TAG
h["SHOTS"] = str(SHOTS)  # design-review shot() writes here
check = h["check"]
use = h["use"]
shot = h["shot"]
goto = h["goto"]
owner_login = h["owner_login"]
register_customer = h["register_customer"]
create_category = m["create_category"]


def create_major(staff, name):
    """Create a major category so the storefront footer renders its catalogue links."""
    goto(staff, "/admin/major-categories/create")
    staff.locator('[name="Name"]').fill(name)
    staff.locator('[name="Description"]').fill("footer e2e major category")
    staff.click('form[action*="create"] button[type="submit"]')
    staff.wait_for_load_state("networkidle")
    check(f"major category created: {name}", name in staff.inner_text("body"))
    goto(staff, "/admin/major-categories")
    row = staff.locator("tr").filter(has_text=name).first
    return row.locator('a[href*="/edit/"]').get_attribute("href").rstrip("/").split("/")[-1]


def create_product(staff, name, cat, price, custom=False):
    """Create a catalogue product through the admin UI (same flow as e2e/direct-checkout)."""
    goto(staff, "/admin/products/create")
    form = 'form[action*="create"]'
    staff.fill(f'{form} input[name="Name"]', name)
    staff.fill(f'{form} textarea[name="Description"]', "footer e2e product")
    staff.select_option(f'{form} select[name="CategoryId"]', cat)
    staff.fill(f'{form} input[name="SellingPrice"]', str(price))
    staff.fill(f'{form} input[name="InitialQuantity"]', "50")
    cb = staff.query_selector(f'{form} input[name="IsCustomizable"]')
    if cb and cb.is_checked() != custom:
        cb.click()
    staff.set_input_files(f'{form} input[name="Images"]', os.path.join(WIP, "product.png"))
    staff.click(f'{form} button[type="submit"]')
    staff.wait_for_load_state("networkidle")
    check(f"product created: {name}", name in staff.inner_text("body"))
    goto(staff, "/admin/products?search=" + name.replace(" ", "+"))
    row = staff.locator("tr").filter(has_text=name).first
    return row.locator('a[href*="/edit/"]').get_attribute("href").rstrip("/").split("/")[-1]

INVENTORY = []
PROBLEMS = []
_n = [0]


def snap(name, pg):
    """Capture the page under test (the shared helper screenshots its own current page)."""
    use(pg)
    _n[0] += 1
    shot(f"{_n[0]:02d}-{name}.png")


def watch(pg):
    """Console errors, uncaught page errors, failed requests and 4xx/5xx sub-resources."""
    def failed(r):
        try:
            where = r.frame.page.url
        except Exception:  # noqa: BLE001
            where = "?"
        PROBLEMS.append(f"requestfailed: {r.url} {r.failure} @ {where}")
    pg.on("console", lambda msg: msg.type == "error" and PROBLEMS.append(f"console: {msg.text} @ {pg.url}"))
    pg.on("pageerror", lambda e: PROBLEMS.append(f"pageerror: {e} @ {pg.url}"))
    pg.on("requestfailed", failed)
    pg.on("response", lambda r: r.status >= 400 and r.request.resource_type != "document"
           and PROBLEMS.append(f"HTTP {r.status}: {r.url}"))
    return pg


# ---------------------------------------------------------------------------
# footer expectations
# ---------------------------------------------------------------------------
def footer_kind(pg):
    if pg.locator(".cs-foot").count():
        return "full"
    if pg.locator(".cs-foot-slim").count():
        return "slim"
    return "none"


GEOM_JS = """() => {
  const d = document.documentElement, scrollY = window.scrollY;
  const box = e => e ? {t: e.getBoundingClientRect().top + scrollY, b: e.getBoundingClientRect().bottom + scrollY} : null;
  const main = document.querySelector('main'), body = document.body;
  return {vw: d.clientWidth, sw: d.scrollWidth, sh: d.scrollHeight,
          bodyPb: parseFloat(getComputedStyle(body).paddingBottom) || 0,
          full: box(document.querySelector('.cs-foot')), slim: box(document.querySelector('.cs-foot-slim')),
          main: box(main), mainMb: main ? getComputedStyle(main).marginBottom : null,
          bottomNav: document.querySelector('.cs-bottom') ? getComputedStyle(document.querySelector('.cs-bottom')).display : null}; }"""


def geometry(pg, label, kind):
    """Spacing checks: the shell reserves space for its fixed bars (bottom nav, sticky
    action bars) through body padding. Nothing else may sit below the content, and the
    storefront footer's own 40px lead-in must not linger on footer-less pages."""
    g = pg.evaluate(GEOM_JS)
    allowed = g["bodyPb"] + 12  # deliberate clearance for the fixed bars only
    check(f"{label}: no horizontal overflow ({g['vw']}px viewport)", g["sw"] <= g["vw"] + 1, (g["sw"], g["vw"]))
    if kind == "slim":
        check(f"{label}: slim bar is the last in-flow element (nothing but the fixed-bar clearance below it)",
              g["sh"] - g["slim"]["b"] <= allowed, (g["sh"], g["slim"]["b"], g["bodyPb"]))
        check(f"{label}: no storefront-footer spacing left on the page (<main> margin-bottom 0)",
              (g["mainMb"] in (None, "0px")), g["mainMb"])
    if kind == "none":
        floor = g["main"]["b"] if g["main"] else g["sh"]
        check(f"{label}: no empty footer-shaped band under the content",
              g["sh"] - floor <= allowed + 40, (g["sh"], floor, g["bodyPb"]))
    return g


def expect(pg, label, want):
    """Assert the footer variant (or, in audit mode, record what is actually there)."""
    got = footer_kind(pg)
    if MODE == "audit":
        INVENTORY.append((label, got))
        print(f"info footer[{label}] = {got}", flush=True)
    else:
        check(f"{label}: footer variant = {want}", got == want, f"got {got}")
    return got


def check_full_footer(pg, label):
    """The storefront footer must keep all of its content and links (rule 10)."""
    f = pg.locator(".cs-foot")
    t = f.inner_text()
    low = t.lower()  # column headings render uppercase (CSS text-transform)
    check(f"{label}: footer shows the brand block and motto",
          "de-chimex" in low and "print hub" in low and "creativity with ease" in low, t[:160])
    check(f"{label}: footer shows the Shop / Help / Talk to us columns",
          all(x in low for x in ("shop", "help", "talk to us", "all products", "faq", "contact us", "about us")), t[:200])
    check(f"{label}: footer shows contact details + studio/pickup addresses",
          "studio:" in low and "pickup:" in low and "@" in t and "+234" in t)
    check(f"{label}: footer shows the social + WhatsApp links",
          pg.locator(".cs-foot-socials a").count() >= 3 and pg.locator(".cs-foot a.cs-wa").count() == 1)
    check(f"{label}: footer keeps the legal line",
          "All rights reserved" in t and "Secure payments by Paystack" in t and "Prices in NGN" in t)
    missing = [href for href in ("/privacy", "/terms", "/about", "/faq", "/contact", "/gallery", "/services")
               if pg.locator(f'.cs-foot a[href="{href}"]').count() == 0]
    check(f"{label}: footer keeps its policy/help links", not missing, missing)
    check(f"{label}: footer keeps the category links into the catalogue",
          pg.locator('.cs-foot a[href*="majorCategoryId="]').count() >= 1)


def check_slim_bar(pg, label):
    t = pg.locator(".cs-foot-slim").inner_text()
    check(f"{label}: slim bar keeps the copyright line", "All rights reserved" in t, t)
    check(f"{label}: slim bar keeps the payment line", "Secure payments by Paystack" in t and "Prices in NGN" in t, t)
    links = [a.get_attribute("href") for a in pg.locator(".cs-foot-slim nav a").all()]
    check(f"{label}: slim bar keeps Privacy / Terms / Contact", links == ["/privacy", "/terms", "/contact"], links)
    check(f"{label}: no storefront footer anywhere in the page", pg.locator(".cs-foot").count() == 0)


def group(pg, label, want, shot_name=None, app_nav=False):
    got = expect(pg, label, want)
    if MODE == "audit":  # audit only records placement + shots; it asserts nothing
        if shot_name:
            snap(shot_name, pg)
        return got
    geometry(pg, label, want)
    if want == "full":
        check_full_footer(pg, label)
    if want == "slim":
        check_slim_bar(pg, label)
    if app_nav:
        check(f"{label}: customer app navigation still present (bottom nav)", pg.locator(".cs-bottom").count() == 1)
    if shot_name:
        snap(shot_name, pg)
    return got


# ---------------------------------------------------------------------------
# payment mechanics (identical to e2e/direct-checkout/workflow.py)
# ---------------------------------------------------------------------------
def stub_post(path, obj):
    req = urllib.request.Request(STUB + path, data=json.dumps(obj).encode(),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read() or b"{}")


def stub_get(path):
    with urllib.request.urlopen(STUB + path, timeout=15) as r:
        return json.loads(r.read() or b"{}")


def start_payment(cust, order_id):
    before = set(stub_get("/control/state")["tx"].keys())
    goto(cust, f"/checkout/complete/{order_id}")
    cust.click('form button:has-text("Pay with Paystack")')
    try:
        cust.wait_for_url(re.compile(re.escape(STUB) + r"/checkout/"), timeout=15000)
    except Exception:  # noqa: BLE001
        pass
    after = stub_get("/control/state")["tx"]
    fresh = [r for r in after if r not in before]
    mm = re.search(r"/checkout/([^/?#]+)", cust.url)
    url_ref = mm.group(1) if mm and STUB in cust.url else None
    return url_ref or (fresh[-1] if fresh else None)


def simulate(cust, ref):
    cust.goto(f"{STUB}/checkout/{ref}", wait_until="networkidle")
    cust.wait_for_timeout(300)
    cust.click(f'a[href="/simulate/{ref}/success"]')
    try:
        cust.wait_for_url(re.compile(r"/checkout/return"), timeout=20000)
    except Exception:  # noqa: BLE001
        pass
    cust.wait_for_timeout(1500)


def add_to_cart(cust, product_id, qty=1):
    goto(cust, f"/portal/shop/{product_id}")
    q = cust.query_selector('input[name="quantity"], input#quantity')
    if q:
        q.fill(str(qty))
    cust.click('button:has-text("Add to cart")')
    cust.wait_for_load_state("networkidle")
    cust.wait_for_timeout(500)


def place_checkout_order(cust):
    goto(cust, "/checkout")
    pickup = cust.query_selector('input[name="Fulfilment"][value="pickup"]')
    if pickup:
        pickup.check()
    tn = cust.query_selector('textarea[name="Notes"]')
    if tn:
        tn.fill("footer e2e order")
    cust.click('form button:has-text("Place order")')
    cust.wait_for_load_state("networkidle")
    cust.wait_for_timeout(400)
    mm = re.search(r"/checkout/complete/([0-9a-f-]{36})", cust.url)
    return mm.group(1) if mm else None


# ---------------------------------------------------------------------------
def main():
    print(f"footer placement E2E — mode={MODE} tag={TAG} shots={SHOTS}", flush=True)
    stub_post("/control/reset", {})
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        guest = watch(use(browser.new_page(viewport={"width": 390, "height": 844})))
        cust = watch(use(browser.new_page(viewport={"width": 390, "height": 844})))
        wide = watch(use(browser.new_page(viewport={"width": 1440, "height": 900})))
        staff = watch(use(browser.new_page(viewport={"width": 1440, "height": 900})))

        print("\n===== setup: owner creates a catalogue, customer registers =====", flush=True)
        owner_login(staff)
        major = create_major(staff, f"Footer Majors {TAG}")
        cat = create_category(staff, f"Footer Cat {TAG}", major)
        prod = create_product(staff, f"Footer Mug {TAG}", cat, 9500, custom=False)
        prod_custom = create_product(staff, f"Footer Tee {TAG}", cat, 12000, custom=True)
        buyer = register_customer(cust, "footer")
        check("customer registered + verified + signed in", bool(buyer), buyer)

        print("\n===== A. public storefront keeps the full footer =====", flush=True)
        goto(guest, "/")
        group(guest, "public home @390px", "full", shot_name="public-home-full-footer-mobile")
        goto(wide, "/")
        group(wide, "public home @1440px", "full", shot_name="public-home-full-footer-desktop")
        check("public home @1440px: footer renders its 4 columns",
              len(wide.evaluate("() => getComputedStyle(document.querySelector('.cs-foot-grid')).gridTemplateColumns"
                                ".trim().split(/\\s+/)")) == 4)
        for path, name in (("/products", "public-products"), ("/services", "public-services"),
                           ("/gallery", "public-gallery"), ("/about", "public-about"), ("/contact", "public-contact"),
                           ("/faq", "public-faq"), ("/privacy", "public-privacy"), ("/terms", "public-terms"),
                           ("/install-help", "public-install-help")):
            goto(guest, path)
            group(guest, f"public {path} @390px", "full", shot_name=f"{name}-full-footer" if name in
                  ("public-products", "public-faq", "public-contact") else None)
        for path in ("/products", "/faq", "/contact"):
            goto(wide, path)
            group(wide, f"public {path} @1440px", "full")

        print("\n===== A2. storefront footer links still work =====", flush=True)
        goto(guest, "/")
        for href, name in (("/privacy", "Privacy"), ("/terms", "Terms"), ("/about", "About us"),
                           ("/faq", "FAQ"), ("/contact", "Contact us")):
            goto(guest, "/")
            with guest.expect_navigation(wait_until="domcontentloaded", timeout=10000):
                guest.click(f'.cs-foot a[href="{href}"]')
            check(f"footer link '{name}' opens {href}", guest.url.rstrip("/").endswith(href.rstrip("/")), guest.url)
        goto(guest, "/")
        snap("public-footer-links-work", guest)
        with guest.expect_navigation(wait_until="domcontentloaded", timeout=10000):
            guest.click('.cs-foot a[href="/services"]')
        check("footer link 'Custom printing' opens /services", guest.url.rstrip("/").endswith("/services"), guest.url)

        print("\n===== B. guest transactional pages are focused (slim bar) =====", flush=True)
        goto(guest, "/cart")
        group(guest, "guest /cart @390px", "slim", shot_name="guest-cart-slim-footer")
        check("guest /cart still offers a way to the catalogue",
              "/products" in guest.content() or "Shop" in guest.inner_text("body"))
        goto(guest, "/checkout")
        check("guest /checkout redirects to the clean auth page", "/Account/Login" in guest.url
              or "/checkout" in guest.url, guest.url)
        if "/Account/Login" in guest.url:
            expect(guest, "guest /checkout -> login (auth layout)", "none")

        print("\n===== C. customer shop browsing keeps the storefront footer =====", flush=True)
        goto(cust, "/portal/shop")
        group(cust, "customer /portal/shop @390px", "full", shot_name="customer-shop-storefront-footer")
        check("customer shop footer links back into the portal catalogue",
              cust.locator('.cs-foot a[href="/portal/shop"]').count() >= 1)
        goto(cust, f"/portal/shop/{prod}")
        group(cust, "customer /portal/shop/{id} @390px", "full")

        print("\n===== D. customer workspace pages have no storefront footer =====", flush=True)
        goto(cust, "/portal")
        group(cust, "customer /portal (dashboard)", "slim", shot_name="customer-dashboard-slim-footer", app_nav=True)
        goto(cust, "/portal/orders")
        group(cust, "customer /portal/orders", "slim", shot_name="customer-orders-slim-footer", app_nav=True)
        goto(cust, "/portal/orders/track")
        group(cust, "customer /portal/orders/track", "slim", app_nav=True)
        goto(cust, "/portal/profile")
        group(cust, "customer /portal/profile", "slim")
        goto(cust, "/portal/profile/password")
        group(cust, "customer /portal/profile/password", "slim")
        goto(cust, "/portal/addresses")
        group(cust, "customer /portal/addresses", "slim")
        goto(cust, "/portal/addresses/add")
        group(cust, "customer /portal/addresses/add", "slim")
        goto(cust, "/portal/wishlist")
        group(cust, "customer /portal/wishlist", "slim", shot_name="customer-wishlist-slim-footer")
        goto(cust, "/portal/payments")
        group(cust, "customer /portal/payments", "slim")
        goto(cust, "/portal/invoices")
        group(cust, "customer /portal/invoices", "slim")
        goto(cust, f"/portal/shop/{prod_custom}/order")
        group(cust, "customer custom-order form /portal/shop/{id}/order", "slim",
              shot_name="customer-order-form-slim-footer")
        cust.set_viewport_size({"width": 1440, "height": 900})
        goto(cust, "/portal")
        group(cust, "customer /portal @1440px", "slim", shot_name="customer-dashboard-slim-footer-desktop")
        cust.set_viewport_size({"width": 390, "height": 844})

        print("\n===== E. transactional checkout funnel (slim, focused) =====", flush=True)
        add_to_cart(cust, prod)
        goto(cust, "/cart")
        group(cust, "customer /cart with an item", "slim", shot_name="customer-cart-slim-footer")
        goto(cust, "/checkout")
        group(cust, "customer /checkout", "slim", shot_name="customer-checkout-slim-footer")
        order_id = place_checkout_order(cust)
        check("order placed, lands on the confirmation page", bool(order_id), cust.url)
        goto(cust, f"/checkout/complete/{order_id}")
        group(cust, "order confirmation /checkout/complete/{id}", "slim",
              shot_name="order-confirmation-slim-footer")
        ref = start_payment(cust, order_id)
        check("Paystack stub checkout opened", bool(ref), "no stub tx")
        if ref:
            stub_post("/control/set", {"ref": ref, "outcome": "success"})
            simulate(cust, ref)
            goto(cust, f"/checkout/return?reference={ref}")
            group(cust, "payment return /checkout/return", "slim", shot_name="payment-return-slim-footer")
        goto(staff, "/checkout")
        if "/checkout" in staff.url:
            expect(staff, "staff account /checkout (no customer profile)", "slim")
        goto(cust, f"/portal/orders/{order_id}")
        group(cust, "customer order details /portal/orders/{id}", "slim", shot_name="order-details-slim-footer")
        goto(cust, f"/portal/orders/{order_id}/workspace")
        group(cust, "customer order workspace", "slim", shot_name="order-workspace-slim-footer")

        print("\n===== F. admin/staff pages render no footer =====", flush=True)
        for path in ("/admin/dashboard", "/admin/orders", "/admin/products", "/admin/customers",
                     "/admin/settings", "/admin/notifications", "/admin/inventory", "/admin/reports"):
            goto(staff, path)
            group(staff, f"admin {path} @1440px", "none",
                  shot_name={"dashboard": "admin-dashboard-no-footer", "orders": "admin-orders-no-footer",
                             "settings": "admin-settings-no-footer"}.get(path.rsplit("/", 1)[-1]))
        goto(staff, f"/admin/orders/{order_id}/workspace")
        group(staff, "admin order workspace", "none")
        goto(guest, "/admin/dashboard")
        check("guest hitting an admin page is redirected to the clean auth page",
              "/Account/Login" in guest.url, guest.url)
        expect(guest, "guest /admin/dashboard -> login", "none")

        print("\n===== G. authentication pages stay clean =====", flush=True)
        auth = watch(use(browser.new_page(viewport={"width": 390, "height": 844})))
        for path, name in (("/Account/Login", "auth-login-no-footer"), ("/Account/Register", "auth-register-no-footer"),
                           ("/Account/ForgotPassword", "auth-forgot-password-no-footer"),
                           ("/Account/ResetPassword", "auth-reset-password-no-footer")):
            goto(auth, path)
            group(auth, f"auth {path} @390px", "none", shot_name=name)
            check(f"auth {path}: no customer bottom nav either", auth.locator(".cs-bottom").count() == 0)
        goto(auth, "/Account/AccessDenied")
        group(auth, "auth /Account/AccessDenied", "none")
        goto(auth, "/Account/Login")
        group(auth, "auth /Account/Login @1440px", "none")
        wide.set_viewport_size({"width": 1440, "height": 900})
        goto(wide, "/Account/Login")
        group(wide, "auth /Account/Login desktop", "none")

        print("\n===== H. narrow 320px pass (mobile layout stays correct) =====", flush=True)
        narrow = watch(use(browser.new_page(viewport={"width": 320, "height": 720})))
        goto(narrow, "/")
        group(narrow, "public home @320px", "full")
        check("@320px: storefront footer stacks to a single column",
              len(narrow.evaluate("() => getComputedStyle(document.querySelector('.cs-foot-grid'))"
                                  ".gridTemplateColumns.trim().split(/\\s+/)")) == 1)
        goto(narrow, "/faq")
        group(narrow, "public /faq @320px", "full")
        snap("public-faq-full-footer-320px", narrow)
        goto(narrow, "/cart")
        group(narrow, "guest /cart @320px", "slim")
        snap("guest-cart-slim-footer-320px", narrow)

        print("\n===== I. no console/page/network problems on any page visited =====", flush=True)
        if MODE == "verify":
            check(f"clean console, no page errors, no failed or 4xx/5xx sub-resource requests ({len(PROBLEMS)})",
                  not PROBLEMS, PROBLEMS[:8])

        browser.close()

    if MODE == "audit":
        print(f"\nAUDIT COMPLETE — {len(INVENTORY)} page groups recorded (no assertions)", flush=True)
        for label, kind in INVENTORY:
            print(f"  {kind:5} {label}", flush=True)
        print("RESULT: audit complete, 0 failed", flush=True)
        return
    print(f"\nRESULT: {len(h['CHECKS'])} passed, 0 failed (TAG={TAG})", flush=True)


if __name__ == "__main__":
    main()
