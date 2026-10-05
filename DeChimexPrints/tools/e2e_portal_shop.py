"""Live proof: portal Shop menu shows the product section; customer can shop from it.

Run anywhere the app is reachable (owner machine: E2E_BASE=http://localhost:7061).
Requires: pip install playwright && python -m playwright install chromium.
Owner password: set OWNER_PASSWORD if the bootstrap password was already changed;
otherwise the script performs the first-login forced change to OWNER_PASSWORD... default OwnerPay2026!x.
"""
import os
import json, re, time, urllib.request
from playwright.sync_api import sync_playwright

BASE = os.environ.get("E2E_BASE", "http://localhost:5119")
SHOTS = "/home/user/e2e/shots"
import os; os.makedirs(SHOTS, exist_ok=True)

def mt(url, data=None, token=None):
    req = urllib.request.Request(url, method="POST" if data else "GET")
    req.add_header("Content-Type", "application/json")
    if token: req.add_header("Authorization", "Bearer " + token)
    body = json.dumps(data).encode() if data else None
    return json.load(urllib.request.urlopen(req, body, timeout=20))

out = {}

# fresh mail.tm inbox
dom = mt("https://api.mail.tm/domains")["hydra:member"][0]["domain"]
ADDR = f"portalshop{int(time.time())}@{dom}"
mt("https://api.mail.tm/accounts", {"address": ADDR, "password": "Test123456!"})

with sync_playwright() as pw:
    b = pw.chromium.launch(headless=True, channel="chromium")
    pg = b.new_context(viewport={"width": 1400, "height": 900}).new_page()
    pg.set_default_timeout(30000)

    # --- owner: password change + product
    pg.goto(BASE + "/Account/Login", wait_until="domcontentloaded")
    pg.fill("#Email", "owner@dechimexprinthub.com"); pg.fill("#Password", os.environ.get("OWNER_BOOTSTRAP", "ChangeMe123!"))
    pg.click("form[action='/Account/Login'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")
    if "password" in pg.url:
        pg.fill("#CurrentPassword", os.environ.get("OWNER_BOOTSTRAP", "ChangeMe123!")); pg.fill("#NewPassword", os.environ.get("OWNER_PASSWORD", "OwnerPay2026!x")); pg.fill("#ConfirmPassword", os.environ.get("OWNER_PASSWORD", "OwnerPay2026!x"))
        pg.click("form[action='/admin/security/password'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")
    pg.goto(BASE + "/Account/Login", wait_until="domcontentloaded")
    if pg.locator("#Email").count():
        pg.fill("#Email", "owner@dechimexprinthub.com"); pg.fill("#Password", os.environ.get("OWNER_PASSWORD", "OwnerPay2026!x"))
        pg.click("form[action='/Account/Login'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")
    pg.goto(BASE + "/admin/categories/create", wait_until="domcontentloaded")
    if pg.locator("#Name").count():
        pg.fill("#Name", "Shop Menu Category")
        pg.click("form[action='/admin/categories/create'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")
    pg.goto(BASE + "/admin/products/create", wait_until="domcontentloaded")
    pg.wait_for_selector("#Name")
    pg.fill("#Name", "Portal Shop Mug")
    opts = pg.eval_on_selector_all("#CategoryId option", "els => els.map((e,i)=>({i,v:e.value}))")
    pg.select_option("#CategoryId", index=next(o for o in opts if o["v"])["i"])
    pg.fill("#SellingPrice", "500")
    if pg.locator("#InitialQuantity").count(): pg.fill("#InitialQuantity", "10")
    pg.click("form[action='/admin/products/create'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")
    out["product_created_url"] = pg.url

    # --- customer: register + verify via mail.tm + login (fresh context: owner stays signed in elsewhere)
    pg = b.new_context(viewport={"width": 1400, "height": 900}).new_page()
    pg.set_default_timeout(30000)
    pg.goto(BASE + "/Account/Register", wait_until="domcontentloaded")
    pg.fill("#FirstName", "Ada"); pg.fill("#LastName", "Okonkwo")
    pg.fill("#Email", ADDR); pg.fill("#Phone", "+2348012345678")
    pg.fill("#Password", "CustomerPay2026!"); pg.fill("#ConfirmPassword", "CustomerPay2026!")
    pg.click("form[action='/Account/Register'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")

    tok = mt("https://api.mail.tm/token", {"address": ADDR, "password": "Test123456!"})["token"]
    otp = None
    for _ in range(30):
        msgs = mt("https://api.mail.tm/messages", token=tok).get("hydra:member", [])
        if msgs:
            m = mt("https://api.mail.tm/messages/" + msgs[0]["id"], token=tok)
            html = m.get("html")
            if isinstance(html, list):
                html = " ".join(p.get("content", "") if isinstance(p, dict) else str(p) for p in html)
            found = re.findall(r"\b(\d{6})\b", (m.get("text") or "") + " " + re.sub("<[^>]+>", " ", html or ""))
            if found: otp = found[0]; break
        time.sleep(3)
    out["otp"] = otp
    pg.goto(BASE + "/Account/VerifyEmail?email=" + ADDR, wait_until="domcontentloaded")
    pg.fill("#Token", otp)
    pg.click("form[action='/Account/VerifyEmail'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")
    pg.goto(BASE + "/Account/Login", wait_until="domcontentloaded")
    pg.fill("#Email", ADDR); pg.fill("#Password", "CustomerPay2026!")
    pg.click("form[action='/Account/Login'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")
    out["customer_home"] = pg.url

    # --- portal dashboard: sidebar Shop menu present
    pg.goto(BASE + "/portal", wait_until="domcontentloaded")
    body = pg.inner_text("body")
    out["dashboard_has_shop_menu"] = "Shop products" in body
    pg.screenshot(path=SHOTS + "/11-portal-dashboard-shopmenu.png", full_page=True)

    # --- portal shop page
    pg.click("a[href='/portal/shop']")
    pg.wait_for_load_state("domcontentloaded")
    body = pg.inner_text("body")
    out["shop_url"] = pg.url
    out["shop_shows_product"] = "Portal Shop Mug" in body
    out["shop_shows_price"] = "₦500.00" in body
    out["shop_shows_category"] = "Shop Menu Category" in body
    pg.screenshot(path=SHOTS + "/12-portal-shop.png", full_page=True)

    # --- shop from it: open card, add to cart
    pg.click("a.pcard")
    pg.wait_for_load_state("domcontentloaded")
    out["detail_url"] = pg.url
    pg.click("form[action='/cart/add'] button[type=submit]"); pg.wait_for_load_state("domcontentloaded")
    out["cart_url"] = pg.url
    out["cart_has_product"] = "Portal Shop Mug" in pg.inner_text("body")
    pg.screenshot(path=SHOTS + "/13-cart-from-portal.png", full_page=True)

print(json.dumps(out, indent=1))
