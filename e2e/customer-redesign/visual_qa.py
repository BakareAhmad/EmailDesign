"""Visual QA / evidence screenshots for the customer-facing redesign.

Usage:
  PLAYWRIGHT_BROWSERS_PATH=... python3 e2e/customer-redesign/visual_qa.py <tag> [out_dir] [widths]

Requires the app on :5119 with data created by e2e/direct-checkout/workflow.py <tag>
(products "DC Everyday Mug <tag>" / "DC Signature Tee <tag>", customer
dr-<tag>-buyer@example.com). Takes full-page screenshots of every redesigned
customer page at the requested widths, signed-out and signed-in, and also a
handful of admin pages to prove the admin shell is untouched.
"""
import os, re, sys, json
from pathlib import Path
from playwright.sync_api import sync_playwright

BASE = os.environ.get("E2E_BASE_URL", "http://127.0.0.1:5119")
TAG = (sys.argv[1] if len(sys.argv) > 1 else "redesign1")[:10]
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else f"/tmp/visual-{TAG}")
WIDTHS = [int(w) for w in (sys.argv[3].split(",") if len(sys.argv) > 3 else ["390", "1440"])]
OUT.mkdir(parents=True, exist_ok=True)
CUST_EMAIL = f"dr-{TAG}-buyer@example.com"
CUST_PASS = "CustE2E!2026xY"
OWNER_EMAIL = "owner@dechimexprinthub.com"
OWNER_PASS = os.environ.get("E2E_OWNER_PASSWORD")

n = 0
def shot(page, name, full=True):
    global n
    n += 1
    path = OUT / f"{n:02d}-{name}.png"
    page.screenshot(path=str(path), full_page=full)
    print("SHOT", path.name, flush=True)

def goto(page, path):
    page.goto(BASE + path, wait_until="networkidle")
    page.wait_for_timeout(250)

def login(page, email, pw):
    goto(page, "/Account/Login")
    page.fill('input[name="Email"]', email)
    page.fill('input[name="Password"]', pw)
    page.click('button[type="submit"]')
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(300)

def find_product_ids(page):
    goto(page, "/products?search=" + TAG)
    html = page.content()
    ids = re.findall(r'data-product-id="([0-9a-f-]{36})"', html)
    names = re.findall(r'class="pc-name"[^>]*>\s*<a[^>]*>([^<]+)<', html)
    direct = custom = None
    for pid, nm in zip(ids, names):
        if "Mug" in nm: direct = pid
        if "Tee" in nm: custom = pid
    return direct, custom

def main():
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        for width in WIDTHS:
            mobile = width < 768
            ctx = browser.new_context(viewport={"width": width, "height": 844 if mobile else 900},
                                      device_scale_factor=2 if mobile else 1, is_mobile=mobile, has_touch=mobile)
            page = ctx.new_page()
            prefix = f"w{width}"
            direct, custom = find_product_ids(page)
            print(f"[{width}] direct={direct} custom={custom}", flush=True)

            # ---- public / signed-out ----
            goto(page, "/");                      shot(page, f"{prefix}-home")
            goto(page, "/products");              shot(page, f"{prefix}-shop")
            goto(page, "/products?search=mug");   shot(page, f"{prefix}-shop-search")
            if mobile:
                goto(page, "/products")
                page.click('[data-sheet-open="shFilterSheet"]')
                page.wait_for_timeout(350)
                shot(page, f"{prefix}-shop-filter-sheet", full=False)
                page.keyboard.press("Escape")
                page.click('#csMenuBtn') if page.query_selector('#csMenuBtn') else None
                page.wait_for_timeout(350)
                shot(page, f"{prefix}-menu-sheet", full=False)
            if direct:
                goto(page, f"/products/{direct}"); shot(page, f"{prefix}-product-direct-anon")
            if custom:
                goto(page, f"/products/{custom}"); shot(page, f"{prefix}-product-custom-anon")
            goto(page, "/cart");                  shot(page, f"{prefix}-cart-empty-anon")
            goto(page, "/services");              shot(page, f"{prefix}-services")
            goto(page, "/gallery");               shot(page, f"{prefix}-gallery")
            goto(page, "/about");                 shot(page, f"{prefix}-about")
            goto(page, "/contact");               shot(page, f"{prefix}-contact")
            goto(page, "/faq");                   shot(page, f"{prefix}-faq")
            goto(page, "/Account/Login");         shot(page, f"{prefix}-login")
            goto(page, "/Account/Register");      shot(page, f"{prefix}-register")
            goto(page, "/Account/ForgotPassword");shot(page, f"{prefix}-forgot-password")

            # ---- signed-in customer ----
            login(page, CUST_EMAIL, CUST_PASS)
            goto(page, "/portal");                shot(page, f"{prefix}-account-overview")
            goto(page, "/portal/shop");           shot(page, f"{prefix}-portal-shop")
            if direct:
                goto(page, f"/portal/shop/{direct}"); shot(page, f"{prefix}-product-direct")
                # add to cart then view cart + checkout
                page.click('button:has-text("Add to cart")')
                page.wait_for_load_state("networkidle"); page.wait_for_timeout(300)
                goto(page, "/cart");              shot(page, f"{prefix}-cart")
                goto(page, "/checkout");          shot(page, f"{prefix}-checkout")
                page.check('input[name="Fulfilment"][value="pickup"]'); page.wait_for_timeout(200)
                shot(page, f"{prefix}-checkout-pickup")
                goto(page, "/cart")
                rm = page.query_selector('.ck-remove')
                if rm:
                    page.once("dialog", lambda d: d.accept())
                    rm.click(); page.wait_for_load_state("networkidle")
            if custom:
                goto(page, f"/portal/shop/{custom}"); shot(page, f"{prefix}-product-custom")
                goto(page, f"/portal/shop/{custom}/order"); shot(page, f"{prefix}-custom-order-form")
            goto(page, "/portal/orders");         shot(page, f"{prefix}-orders")
            html = page.content()
            order_ids = re.findall(r'href="/portal/orders/([0-9a-f-]{36})"', html)
            ws_ids = re.findall(r'href="/portal/orders/([0-9a-f-]{36})/workspace"', html)
            pay_ids = re.findall(r'href="/checkout/complete/([0-9a-f-]{36})"', html)
            if order_ids:
                goto(page, f"/portal/orders/{order_ids[0]}"); shot(page, f"{prefix}-order-details")
            if ws_ids:
                goto(page, f"/portal/orders/{ws_ids[0]}/workspace"); shot(page, f"{prefix}-workspace")
            else:
                # any order has a workspace route; show a direct one
                if order_ids:
                    goto(page, f"/portal/orders/{order_ids[-1]}/workspace"); shot(page, f"{prefix}-workspace")
            if pay_ids:
                goto(page, f"/checkout/complete/{pay_ids[0]}"); shot(page, f"{prefix}-payment-step")
            goto(page, "/portal/orders/track?orderNumber=NOPE-1"); shot(page, f"{prefix}-track-notfound")
            goto(page, "/portal/wishlist");       shot(page, f"{prefix}-wishlist")
            goto(page, "/portal/addresses");      shot(page, f"{prefix}-addresses")
            goto(page, "/portal/addresses/add");  shot(page, f"{prefix}-address-add")
            goto(page, "/portal/payments");       shot(page, f"{prefix}-payments")
            goto(page, "/portal/invoices");       shot(page, f"{prefix}-invoices")
            goto(page, "/portal/profile");        shot(page, f"{prefix}-profile")
            if mobile:
                page.click('#csMenuBtn') if page.query_selector('#csMenuBtn') else None
                page.wait_for_timeout(350)
                shot(page, f"{prefix}-account-menu", full=False)
            ctx.close()

        # ---- admin unaffected (desktop only) ----
        if OWNER_PASS:
            ctx = browser.new_context(viewport={"width": 1440, "height": 900})
            page = ctx.new_page()
            login(page, OWNER_EMAIL, OWNER_PASS)
            for path, name in (("/admin/dashboard", "admin-dashboard"), ("/admin/orders", "admin-orders"), ("/admin/products", "admin-products")):
                goto(page, path); shot(page, f"w1440-{name}")
            ctx.close()
        browser.close()
    print("DONE", n, "screenshots in", OUT)

if __name__ == "__main__":
    main()
