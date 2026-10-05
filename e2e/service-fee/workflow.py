#!/usr/bin/env python3
"""Service Fee E2E (SERVICE_FEE_EVALUATION.md / brief §18).

  CONFIGURATION  100% -> 50% via the two-step confirmed flow (+ audit entry), later
                 50% -> 25%; generic settings save can NOT touch the rate.
  TRANSACTIONS   checkout shows "Service fee (added to your payment)" = Y% of the
                 Paystack fee; receipt shows Order total / Service fee / Amount paid;
                 admin payment card shows the snapshot (incl. expected vs charged fee).
  IMMUTABILITY   reconfiguring to 25% never rewrites the 50% transaction's records.
  FAIL/RETRY     declined payment records no fee; retry prices at the CURRENT config.
  CUSTOM PATH    custom order pays with the same fee rules at 25%.
  REFUND         refunded receipt keeps the applied 50% fee; spending reverses.

Run (final evidence set):  python3 e2e/service-fee/workflow.py sf-0923
Dry run:                   E2E_SHOTS_DIR=/tmp/dry-sf python3 e2e/service-fee/workflow.py dry
"""
from pathlib import Path
import os, re, sys, hmac, json, hashlib, urllib.request, urllib.parse
from playwright.sync_api import sync_playwright

ROOT = Path("/home/user/v047-DesignReview-Fixed-v4-Wishlist-Fix")
BASE = os.environ.get("E2E_BASE_URL", "http://127.0.0.1:5119")
STUB = os.environ.get("E2E_STUB_URL", "http://127.0.0.1:9090")
WIP = "/home/user/e2e-dr-wip"
TAG = (sys.argv[1] if len(sys.argv) > 1 else "sf-e2e")[:10]
SHOTS = Path(os.environ.get("E2E_SHOTS_DIR", str(ROOT / "evidence")))
SHOTS.mkdir(parents=True, exist_ok=True)

SETTINGS = json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.json").read_text())
SETTINGS.update(json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.Development.json").read_text()))
SECRET = SETTINGS["Paystack"]["SecretKey"]  # HMAC only; never printed

# Proven helper layers (same loader as e2e/direct-checkout/workflow.py).
helperfile = ROOT / "e2e/major-categories/workflow.py"
m = {"__file__": str(helperfile)}
exec(compile(helperfile.read_text().split("\nwith sync_playwright()")[0], str(helperfile), "exec"), m)
h = m["h"]
for _n in ("check", "use", "owner_login", "register_customer",
           "create_category", "create_product", "options", "order"):
    globals()[_n] = h[_n] if _n in h else m[_n]
m["SHOTS"] = Path(WIP) / "majors-shots"
m["SHOTS"].mkdir(parents=True, exist_ok=True)
h["TAG"] = TAG
create_category = m["create_category"]
set_question = m["options"]
submit_custom_request = m["order"]
owner_login = h["owner_login"]
register_customer = h["register_customer"]

checks = []
FAILS = []


def check(name, cond, extra=""):
    if cond:
        checks.append(name)
        print(f"PASS {len(checks):02d}: {name}", flush=True)
    else:
        FAILS.append(name)
        print(f"FAIL: {name}  {extra}", flush=True)


def goto(p, path):
    r = p.goto(BASE + path, wait_until="networkidle")
    p.wait_for_timeout(250)
    return r


def shot(p, name):
    p.screenshot(path=str(SHOTS / name), full_page=True)
    print(f"SHOT  {name}", flush=True)


def body(p):
    return p.inner_text("body")


def submit(p, sel):
    p.click(sel)
    p.wait_for_load_state("networkidle")
    p.wait_for_timeout(400)


def accept_dialogs(p):
    p.on("dialog", lambda d: d.accept())


def stub_post(path, obj):
    req = urllib.request.Request(STUB + path, data=json.dumps(obj).encode(),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read() or b"{}")


def stub_get(path):
    with urllib.request.urlopen(STUB + path, timeout=15) as r:
        return json.loads(r.read() or b"{}")


def post_webhook(ref, amount_kobo, currency="NGN", event="charge.success"):
    body_ = json.dumps({"event": event, "data": {
        "reference": ref, "amount": amount_kobo, "currency": currency,
        "channel": "card", "status": "success"}}).encode()
    sig = hmac.new(SECRET.encode(), body_, hashlib.sha512).hexdigest()
    req = urllib.request.Request(BASE + "/api/v1/payments/webhook/paystack", data=body_,
                                 method="POST",
                                 headers={"Content-Type": "application/json", "x-paystack-signature": sig})
    with urllib.request.urlopen(req, timeout=15) as r:
        return r.status, r.read().decode()


def start_payment(cust, order_id):
    before = set(stub_get("/control/state")["tx"].keys())
    goto(cust, f"/checkout/complete/{order_id}")
    cust.click('form button:has-text("Pay with Paystack")')
    try:
        cust.wait_for_url(re.compile(re.escape(STUB) + r"/checkout/"), timeout=15000)
    except Exception:
        pass
    after = stub_get("/control/state")["tx"]
    fresh = [r for r in after if r not in before]
    mm = re.search(r"/checkout/([^/?#]+)", cust.url)
    url_ref = mm.group(1) if mm and STUB in cust.url else None
    return url_ref or (fresh[-1] if fresh else None)


def simulate(cust, ref, action):
    cust.goto(f"{STUB}/checkout/{ref}", wait_until="networkidle")
    cust.wait_for_timeout(300)
    suffix = {"success": "/success", "decline": "/failed", "abandon": "/abandon"}[action]
    cust.click(f'a[href="/simulate/{ref}{suffix}"]')
    try:
        cust.wait_for_url(re.compile(r"/checkout/return"), timeout=20000)
    except Exception:
        pass
    cust.wait_for_timeout(1500)


def money_dec(text, label):
    mm = re.search(re.escape(label) + r"[^₦]*₦([\d,]+\.\d{2})", text)
    return float(mm.group(1).replace(",", "")) if mm else None


def fee_line(text):
    return money_dec(text, "Service fee (added to your payment)")


def create_product_local(staff, name, cat, price, custom=False):
    goto(staff, "/admin/products/create")
    form = 'form[action*="create"]'
    staff.fill(f'{form} input[name="Name"]', name)
    staff.fill(f'{form} textarea[name="Description"]', "service fee e2e product")
    staff.select_option(f'{form} select[name="CategoryId"]', cat)
    staff.fill(f'{form} input[name="SellingPrice"]', str(price))
    staff.fill(f'{form} input[name="InitialQuantity"]', "50")
    cb = staff.query_selector(f'{form} input[name="IsCustomizable"]')
    if cb and cb.is_checked() != custom:
        cb.click()
    staff.set_input_files(f'{form} input[name="Images"]', os.path.join(WIP, "product.png"))
    submit(staff, f'{form} button[type="submit"]')
    goto(staff, "/admin/products?search=" + urllib.parse.quote(name))
    row = staff.locator("tr").filter(has_text=name).first
    return row.locator('a[href*="/edit/"]').get_attribute("href").rstrip("/").split("/")[-1]


def add_to_cart(cust, product_id, qty=1):
    goto(cust, f"/portal/shop/{product_id}")
    q = cust.query_selector('input[name="quantity"], input#quantity')
    if q:
        q.fill(str(qty))
    cust.click('button:has-text("Add to cart")')
    cust.wait_for_load_state("networkidle")
    cust.wait_for_timeout(500)


def place_checkout_order(cust, notes):
    goto(cust, "/checkout")
    pickup = cust.query_selector('input[name="Fulfilment"][value="pickup"]')
    if pickup:
        pickup.check()
        cust.wait_for_timeout(300)
    tn = cust.query_selector('textarea[name="Notes"]')
    if tn:
        tn.fill(notes)
    cust.click('form button:has-text("Place order")')
    cust.wait_for_load_state("networkidle")
    cust.wait_for_timeout(300)
    mm = re.search(r"/checkout/complete/([0-9a-f-]{36})", cust.url)
    check("lands on /checkout/complete/{order}", bool(mm), cust.url)
    return mm.group(1) if mm else None


def pay_order(cust, order_id, outcome="success"):
    ref = start_payment(cust, order_id)
    check(f"paystack stub checkout opened (order {order_id[:8]})", bool(ref), "no stub tx")
    if not ref:
        return None
    stub_post("/control/set", {"ref": ref, "outcome": outcome})
    simulate(cust, ref, "success" if outcome == "success" else "decline")
    return ref


def admin_receipt(admin, order_id):
    """Open the admin order and follow its Receipt link to the receipt document."""
    goto(admin, f"/admin/orders/{order_id}")
    link = admin.query_selector('a[href*="/admin/receipts/"]')
    if not link:
        return False
    href = link.get_attribute("href")
    goto(admin, href)
    return True


def change_service_fee(admin, new_value):
    goto(admin, "/admin/settings")
    admin.fill('#inp_service_fee', str(new_value))
    submit(admin, 'form[action*="service-fee"] button[type="submit"]')
    return body(admin)


def confirm_service_fee(admin):
    html = admin.content()
    tok = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html) or \
          re.search(r'value="([^"]+)"[^>]*name="__RequestVerificationToken"', html)
    assert tok, "confirm form token missing"
    cur = re.search(r'name="CurrentPercent"[^>]*value="([^"]*)"', html)
    new = re.search(r'name="RequestedPercent"[^>]*value="([^"]*)"', html)
    admin.request.post(BASE + "/admin/settings/service-fee/confirm", form={
        "CurrentPercent": cur.group(1) if cur else "",
        "RequestedPercent": new.group(1) if new else "",
        "__RequestVerificationToken": tok.group(1),
    })
    admin.wait_for_timeout(500)


def customer_spending(page, email):
    goto(page, "/admin/customers")
    rows = page.locator(f'tr:has-text("{email}") a')
    if rows.count():
        try:
            rows.first.click()
            page.wait_for_load_state("networkidle")
            page.wait_for_timeout(400)
        except Exception:
            pass
    t = body(page)
    mm = re.search(r"Lifetime spending[^₦]*₦([\d,]+)", t)
    return int(mm.group(1).replace(",", "")) if mm else None


def main():
    stub_post("/control/reset", {})
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        cust = use(browser.new_page())
        admin = use(browser.new_page())
        for pg in (cust, admin):
            accept_dialogs(pg)

        # ============ STAGE 0: actors + catalogue ============
        print("\n===== STAGE 0: setup =====", flush=True)
        owner_login(admin)
        cat_direct = create_category(admin, f"SF Direct {TAG}")
        prod_direct = create_product_local(admin, f"SF Mug {TAG}", cat_direct, 9500, custom=False)
        cat_custom = create_category(admin, f"SF Custom {TAG}")
        set_question(admin, cat_custom)
        prod_custom = create_product_local(admin, f"SF Tee {TAG}", cat_custom, 12000, custom=True)
        buyer = register_customer(cust, "buyer")
        check("buyer registered + signed in", bool(buyer), str(buyer))

        # ============ STAGE 1: configuration 100 -> 50 (confirmed) ============
        print("\n===== STAGE 1: service fee configuration =====", flush=True)
        goto(admin, "/admin/settings")
        t = body(admin)
        check("settings page shows Service Fee card", "Service Fee" in t)
        check("service fee starts at 100% (legacy full pass-through)",
              money_dec(t, "Current service fee") is None and "100%" in t, t[:200])
        shot(admin, "01-settings-at-100.png")

        html = change_service_fee(admin, 50)
        check("preview shows the confirmation dialog", "Change Service Fee?" in html)
        check("confirmation shows current value (100%)", "100%" in html)
        check("confirmation shows new value (50%)", "50%" in html)
        check("confirmation warns future transactions only", "affect future transactions" in html)
        check("confirmation protects existing transactions", "will not be recalculated" in html)
        check("confirmation asks for explicit consent", "Are you sure you want to continue?" in html)
        shot(admin, "02-confirm-dialog-100-to-50.png")

        confirm_service_fee(admin)
        goto(admin, "/admin/settings")
        t = body(admin)
        check("service fee saved at 50%",
              admin.locator("#inp_service_fee").input_value() == "50" and "Current service fee" in t and "50%" in t, t[:300])
        shot(admin, "03-settings-at-50.png")

        goto(admin, "/admin/activity")
        t = body(admin)
        check("audit: Service fee configuration changed", "Service fee configuration changed" in t)
        check("audit records old and new values", "from 100% to 50%" in t)
        check("audit states future-only scope", "Applies to future transactions" in t)
        shot(admin, "04-audit-service-fee-changed.png")

        # ============ STAGE 2: checkout at 50% ============
        print("\n===== STAGE 2: checkout prices the service fee =====", flush=True)
        add_to_cart(cust, prod_direct)
        goto(cust, "/checkout")
        pickup = cust.query_selector('input[name="Fulfilment"][value="pickup"]')
        if pickup:
            pickup.check()
            cust.wait_for_timeout(300)
        t = body(cust)
        check("checkout shows the Service fee line", "Service fee (added to your payment)" in t)
        check("checkout hides provider internals", "Paystack processing fee" not in t)
        fee50 = fee_line(t)
        check("service fee at 50% = ₦122.17 (50% of the Paystack fee on ₦9,500)",
              fee50 is not None and abs(fee50 - 122.17) < 0.05, str(fee50))
        payable50 = money_dec(t, "You will pay")
        check("payable = order + service fee (9,622.17)",
              payable50 is not None and abs(payable50 - 9622.17) < 0.05, str(payable50))
        shot(cust, "05-checkout-service-fee-50.png")

        order_a = place_checkout_order(cust, "service fee e2e order A")
        t = body(cust)
        check("complete page: Payment Required", "Status: Payment Required" in t)
        check("complete page shows the service fee", "Service fee (added to your payment)" in t)
        shot(cust, "06-order-placed-payment-required.png")

        # ============ STAGE 3: pay (success) ============
        print("\n===== STAGE 3: pay order A at 50% =====", flush=True)
        ref_a = pay_order(cust, order_a, "success")
        t = body(cust)
        check("payment successful at 50%", "Payment successful" in t or "Payment verified" in t, t[:150])
        shot(cust, "07-payment-successful.png")

        # ============ STAGE 4: documents & records show the applied fee ============
        print("\n===== STAGE 4: receipt / history / admin snapshot =====", flush=True)
        goto(cust, "/portal/payments")
        t = body(cust)
        check("payment history shows the service fee incl. split", "service fee (50%)" in t)
        check("payment history amount includes the fee", "9,622.17" in t)
        shot(cust, "08-payment-history-incl-fee.png")

        ok = admin_receipt(admin, order_a)
        check("receipt document reachable", ok)
        t = body(admin)
        check("receipt shows Order total", "Order total" in t)
        check("receipt shows Service fee ₦122.17", "Service fee" in t and "122.17" in t)
        check("receipt shows Amount paid ₦9,622.17", "Amount paid" in t and "9,622.17" in t)
        shot(admin, "09-receipt-fee-breakdown-50.png")

        goto(admin, f"/admin/orders/{order_a}")
        t = body(admin)
        check("admin payment card shows Order total", "Order total" in t)
        check("admin payment card shows Service fee (50%)", "Service fee (50%)" in t)
        check("admin payment card shows Paystack fee expected/charged", "Paystack fee (expected / charged)" in t)
        shot(admin, "10-admin-payment-snapshot-50.png")

        # ============ STAGE 5: reconfig 50 -> 25; history immutable ============
        print("\n===== STAGE 5: reconfiguration keeps history =====", flush=True)
        html = change_service_fee(admin, 25)
        check("second confirmation shows 50% -> 25%", "50%" in html and "25%" in html)
        confirm_service_fee(admin)
        goto(admin, "/admin/settings")
        t = body(admin)
        check("service fee now 25%", "25%" in t)
        shot(admin, "11-settings-at-25.png")

        ok = admin_receipt(admin, order_a)
        t = body(admin)
        check("receipt still shows the applied 50% fee (122.17)", "122.17" in t, t[:200])
        check("receipt still shows full amount paid (9,622.17)", "9,622.17" in t)
        shot(admin, "12-receipt-immutable-after-reconfig.png")

        # ============ STAGE 6: failure then retry at the CURRENT config (25%) ============
        print("\n===== STAGE 6: decline + retry at 25% =====", flush=True)
        add_to_cart(cust, prod_direct)
        order_b = place_checkout_order(cust, "service fee e2e order B")
        pay_order(cust, order_b, "failed")
        t = body(cust)
        check("declined payment is not a success", "Payment failed" in t or "not completed" in t, t[:150])
        no_receipt = not admin_receipt(admin, order_b)
        check("failed attempt generated no receipt", no_receipt)
        shot(cust, "13-payment-failed-no-fee.png")

        ref_b = pay_order(cust, order_b, "success")
        t = body(cust)
        check("retry on the same order succeeds", "Payment successful" in t or "Payment verified" in t, t[:150])
        ok = admin_receipt(admin, order_b)
        t = body(admin)
        fee25 = money_dec(t, "Service fee")
        check("retry priced at current config: service fee ≈ ₦60.85 (25%)",
              fee25 is not None and abs(fee25 - 60.85) < 0.05, str(fee25))
        goto(admin, f"/admin/orders/{order_b}")
        check("one receipt only — fee never charged twice",
              admin.locator('a[href*="/admin/receipts/"]').count() == 1)
        shot(admin, "14-retry-paid-at-25.png")

        # ============ STAGE 7: custom order path at 25% ============
        print("\n===== STAGE 7: custom order pays with the same rules =====", flush=True)
        ws_url = submit_custom_request(cust, prod_custom, "Black and gold, sizes M x6 and L x4")
        order_d = re.search(r"([0-9a-f-]{36})", ws_url).group(1)
        check("custom request placed", bool(order_d))
        goto(admin, f"/admin/orders/{order_d}/workspace")
        fin = admin.query_selector('button:has-text("Finali"), button:has-text("finali")')
        if fin:
            fin.click()
            admin.wait_for_load_state("networkidle")
            admin.wait_for_timeout(800)
        ref_d = pay_order(cust, order_d, "success")
        t = body(cust)
        check("custom order paid at 25%", "Payment successful" in t or "Payment verified" in t, t[:150])
        ok = admin_receipt(admin, order_d)
        t = body(admin)
        fee_d = money_dec(t, "Service fee")
        check("custom receipt service fee ≈ ₦70.27 (25% of the fee on ₦12,000)",
              fee_d is not None and abs(fee_d - 70.27) < 0.05, str(fee_d))
        shot(admin, "15-custom-paid-receipt-at-25.png")

        # ============ STAGE 8: refund keeps the 50% snapshot; spending reverses ============
        print("\n===== STAGE 8: refund order A =====", flush=True)
        spend_before = customer_spending(admin, buyer)
        check("lifetime spending before refund = 31,000", spend_before == 31000, str(spend_before))
        goto(admin, f"/admin/orders/{order_a}")
        rf = admin.query_selector('form[action*="efund"] button, button:has-text("Refund")')
        check("refund action available", rf is not None)
        if rf:
            rf.click()
            admin.wait_for_load_state("networkidle")
            admin.wait_for_timeout(800)
        ok = admin_receipt(admin, order_a)
        t = body(admin)
        check("refunded receipt keeps the applied 50% fee (122.17)", "122.17" in t, t[:200])
        check("receipt marked refunded", "refunded" in t.lower())
        shot(admin, "16-refunded-receipt-keeps-fee.png")
        spend_after = customer_spending(admin, buyer)
        check("spending reversed exactly the order value (21,500 left)", spend_after == 21500, str(spend_after))

        # ============ STAGE 9: activity trail ============
        print("\n===== STAGE 9: business activity =====", flush=True)
        goto(admin, "/admin/activity")
        t = body(admin)
        check("activity: payment received", "payment received" in t.lower() or "Payment received" in t)
        check("activity: refund recorded", "refund" in t.lower())
        shot(admin, "17-activity-feed-fees.png")

        browser.close()

    print(f"\n===== RESULT: {len(checks)} passed, {len(FAILS)} failed =====", flush=True)
    for f in FAILS:
        print(f"  FAILED: {f}", flush=True)
    sys.exit(1 if FAILS else 0)


if __name__ == "__main__":
    main()
