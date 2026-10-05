"""Implementation-inconsistency fixes E2E (placeholder-audit follow-up, 2026-10-04).

Verifies through the real UI that the four reported inconsistencies are resolved:
  1. quotes run on their own QTE- sequence (documents + admin list), and converting a
     quote into an order renumbers the adopted document onto INV- (paid documents never
     read QTE-…);
  2. procurement links an order by its business number (DCX-…), shows the linked order
     on the procurement document, rejects unknown numbers with a friendly error;
  3. the business-activity free-text search finds activity by the linked order number;
  4. the unreferenced dev-prototype ui.js is no longer served.

PRIMARY=implfixes publishes 5 ordered evidence shots to the final-run evidence dir;
other primaries run it as a regression with shots in /tmp.
"""
import os
import re
import sys
import time

from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:5119"
SHOTS = os.environ.get("E2E_SHOTS_DIR", "/tmp/implfixes-shots")
TAG = (sys.argv[1] if len(sys.argv) > 1 else "fx").lower()[:8]
CHECKS = []

CURRENT = [None]


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
    pg.wait_for_timeout(400)
    return pg


def select_first_product(pg, select_selector):
    """Pick the first real product option of a catalog <select> and return its value."""
    value = pg.evaluate(
        """(sel) => {
            const s = document.querySelector(sel);
            if (!s) return null;
            const opt = [...s.options].find(o => o.value && o.textContent.trim() && !/select/i.test(o.textContent));
            return opt ? opt.value : null;
        }""",
        select_selector)
    assert value, f"no product option found in {select_selector}"
    pg.select_option(select_selector, value)
    return value


with sync_playwright() as p:
    browser = p.chromium.launch()

    ctx = browser.new_context(viewport={"width": 1440, "height": 900})
    owner = use(ctx.new_page())

    # ---------- owner sign-in (handles the forced first-login password rotation) ----------
    goto(owner, "/Account/Login")
    for pw in ("OwnerE2E!2026xY", "ChangeMe123!"):
        owner.fill('form[action*="Login"] input[name="Email"]', "owner@dechimexprinthub.com")
        owner.fill('form[action*="Login"] input[name="Password"]', pw)
        owner.click('form[action*="Login"] button[type="submit"]')
        owner.wait_for_timeout(2200)
        if "/Account/Login" not in owner.url:
            break
    if "password" in owner.url.lower():
        owner.fill('input[name="CurrentPassword"]', "ChangeMe123!")
        owner.fill('input[name="NewPassword"]', "OwnerE2E!2026xY")
        owner.fill('input[name="ConfirmPassword"]', "OwnerE2E!2026xY")
        owner.click('form[action*="password"] button[type="submit"]')
        owner.wait_for_timeout(2500)
    check("owner signed in", "/admin" in owner.url, owner.url)

    # ---------- 1. quote runs on the QTE- sequence ----------
    goto(owner, "/admin/invoices/new")
    owner.check('input[name="CustomerMode"][value="new"]')  # reveals the new-customer panel
    owner.wait_for_timeout(300)
    first_product = select_first_product(owner, 'select[name="Lines[0].ProductId"]')
    owner.fill('input[name="NewFirstName"]', f"Impl{TAG}")
    owner.fill('input[name="NewLastName"]', "Quote")
    owner.fill('input[name="NewPhone"]', "08030000031")
    owner.fill('input[name="Lines[0].Quantity"]', "2")
    owner.click('form[action*="invoices"] button[type="submit"], form[action*="invoices"] input[type="submit"]')
    owner.wait_for_timeout(1500)
    doc_html = owner.content()
    qte = re.search(r"QTE-\d{4}-\d{6}", doc_html)
    check("quote document carries a QTE- reference", bool(qte), doc_html[:200])
    quote_url = owner.url
    check("quote document still offers one-click conversion",
          owner.query_selector('a[href*="/admin/orders/new?invoice="]') is not None)
    shot("01-quote-qte-document.png")

    goto(owner, "/admin/invoices")
    check("invoices list labels the quote with its QTE- reference",
          qte.group(0) in owner.content() and "Quote" in owner.content())
    shot("02-quote-invoices-list-qte.png")

    # ---------- 2. conversion renumbers the adopted document onto INV- ----------
    quote_id = re.search(r"/admin/invoices/([0-9a-fA-F-]{36})", quote_url).group(1)
    goto(owner, f"/admin/orders/new?invoice={quote_id}")
    check("conversion prefill carries the quote banner",
          "quote" in owner.inner_text("body").lower())
    # customer: the quote created one — resolve via the picker and force existing mode
    picker = owner.evaluate(
        "fetch('/admin/customers/picker?q=Impl" + TAG + "', {credentials:'same-origin'})"
        ".then(r => r.json())")
    check("quote customer resolvable in the picker", len(picker) >= 1)
    customer_id = picker[0]["id"]
    owner.check('input[name="CustomerMode"][value="existing"]')
    owner.evaluate(
        f"const i = document.querySelector('input[name=\"CustomerId\"]');"
        f"if (i) i.value = '{customer_id}';")
    select_first_product(owner, 'select[name="Products[0].ProductId"]')
    owner.fill('input[name="Products[0].Quantity"]', "2")
    owner.check('input[name="Fulfilment"][value="pickup"]')
    owner.click('form[action*="orders"] button[type="submit"], form[action*="orders"] input[type="submit"]')
    owner.wait_for_timeout(1800)
    order_html = owner.content()
    order_number = re.search(r"DCX-\d{4}-\d{6}", order_html)
    check("order created from the quote", bool(order_number), owner.url[:160])
    check("conversion routes to the order workspace", "/admin/orders/" in owner.url, owner.url[:160])

    invoice_doc = str(ctx.request.get(quote_url).body(), "utf-8", "ignore")
    inv = re.search(r"INV-\d{4}-\d{6}", invoice_doc)
    check("adopted document renumbered onto the INV- sequence", bool(inv))
    check("paid-document path no longer shows the QTE- reference",
          qte.group(0) not in invoice_doc)
    check("adopted invoice links its order", order_number.group(0) in invoice_doc)
    shot("03-order-from-quote-adopted-inv.png")

    # ---------- 3. procurement links by business number ----------
    goto(owner, "/admin/procurement/create")
    proc_product = select_first_product(owner, 'select[name="Items[0].ProductId"]')
    owner.fill('input[name="SourceOrderNumber"]', "NOPE-123")
    owner.fill('input[name="Items[0].Quantity"]', "5")
    owner.fill('input[name="Items[0].UnitCost"]', "300")
    owner.click('form[action*="procurement"] button[type="submit"], form[action*="procurement"] input[type="submit"]')
    owner.wait_for_timeout(1200)
    check("unknown order number gets a friendly validation error",
          "No order with the number NOPE-123 was found" in owner.inner_text("body"))
    shot("04-procurement-unknown-number-error.png")

    check("form placeholder teaches the real order-number format",
          owner.get_attribute('input[name="SourceOrderNumber"]', "placeholder") == "e.g. DCX-2026-000042")
    owner.fill('input[name="SourceOrderNumber"]', order_number.group(0))
    owner.fill('input[name="Items[0].Quantity"]', "5")
    owner.fill('input[name="Items[0].UnitCost"]', "300")
    owner.click('form[action*="procurement"] button[type="submit"], form[action*="procurement"] input[type="submit"]')
    owner.wait_for_timeout(1500)
    proc_html = owner.content()
    check("procurement created and linked by order number",
          "PRC-" in proc_html and order_number.group(0) in proc_html, owner.url[:160])
    check("procurement document links the order page",
          f"/admin/orders/" in proc_html)
    shot("05-procurement-linked-order.png")

    # ---------- 4. activity search finds the order by number ----------
    goto(owner, f"/admin/activity?Search={order_number.group(0)}")
    body = owner.inner_text("body")
    check("business-activity free-text search matches the linked order number",
          order_number.group(0) in body, body[:160])
    shot("06-activity-search-by-order-number.png")

    # ---------- 5. prototype ui.js removed ----------
    code = ctx.request.get(BASE + "/js/ui.js").status
    check("unreferenced prototype ui.js is no longer served", code == 404, code)

    browser.close()

print(f"RESULT: {len(CHECKS)} passed, 0 failed")
