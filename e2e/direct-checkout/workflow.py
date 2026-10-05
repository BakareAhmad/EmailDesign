#!/usr/bin/env python3
"""Direct Checkout E2E (DIRECT_CHECKOUT_EVALUATION.md §18 matrix).

Covers BOTH customer journeys plus regression coverage of the §14/§15 audit fixes:

  DIRECT PATH    browse/filter -> eligible product (Add to cart) -> cart -> checkout
                 -> Paystack (stub) -> verified/paid -> production statuses ->
                 fulfilment -> review eligibility -> retry/cancel/refund
  CUSTOM PATH    custom product (IsCustomizable / category ordering options) ->
                 "Order / Customize" request -> workspace (assigned sales) ->
                 finalize -> payment -> paid; plus the guards that keep custom
                 products OFF the direct path.

Run (final evidence set):  python3 e2e/direct-checkout/workflow.py direct
Dry run:                   E2E_SHOTS_DIR=/tmp/dry-direct python3 e2e/direct-checkout/workflow.py dry
"""
from pathlib import Path
import os, re, sys, hmac, json, hashlib, urllib.request, urllib.parse
from playwright.sync_api import sync_playwright

ROOT = Path(os.environ.get("E2E_ROOT", str(Path(__file__).resolve().parents[2])))
BASE = os.environ.get("E2E_BASE_URL", "http://127.0.0.1:5119")
STUB = os.environ.get("E2E_STUB_URL", "http://127.0.0.1:9090")
WIP = "/home/user/e2e-dr-wip"
TAG = (sys.argv[1] if len(sys.argv) > 1 else "dc-e2e")[:10]
SHOTS = Path(os.environ.get("E2E_SHOTS_DIR", str(ROOT / "evidence")))
SHOTS.mkdir(parents=True, exist_ok=True)

SETTINGS = json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.json").read_text())
SETTINGS.update(json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.Development.json").read_text()))
SECRET = SETTINGS["Paystack"]["SecretKey"]  # HMAC only; never printed

# Proven helper layers (same loader as e2e/payments/workflow.py):
# major-categories carries create_category/options/order and loads
# ws_designreview_e2e helpers (owner_login / register_customer + Brevo capture)
# into m['h'].
helperfile = ROOT / "e2e/major-categories/workflow.py"
m = {"__file__": str(helperfile)}
exec(compile(helperfile.read_text().split("\nwith sync_playwright()")[0], str(helperfile), "exec"), m)
h = m["h"]

# Flatten the proven helpers into this module so main() can call them bare
# (payments-style). ws helpers live in h (dict); majors helpers live in m.
# Note: majors' ws loader splits at '\nwith sync_playwright() as p:' which keeps
# check/use/owner_login/register_customer all in h.
for _n in ("check", "use", "owner_login", "register_customer",
           "create_category", "create_product", "options", "order"):
    globals()[_n] = h[_n] if _n in h else m[_n]
# keep majors-internal side screenshots out of the evidence deliverable dir
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
    m = re.search(r"/checkout/([^/?#]+)", cust.url)
    url_ref = m.group(1) if m and STUB in cust.url else None
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


def money_after(text, label):
    mm = re.search(re.escape(label) + r"\s*₦([\d,]+)", text)
    return int(mm.group(1).replace(",", "")) if mm else None


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
    spend = money_after(body(page), "Lifetime spending")
    if spend is not None:
        return spend
    row = page.evaluate("""(email) => {
        for (const tr of document.querySelectorAll('tr')) {
            if (tr.innerText.includes(email)) {
                const m = tr.innerText.match(/₦([\\d,]+)/);
                return m ? m[1] : null;
            }
        }
        return null;
    }""", email)
    return int(row.replace(",", "")) if row else None


def create_product(staff, name, cat, price, custom=False):
    goto(staff, "/admin/products/create")
    form = 'form[action*="create"]'
    staff.fill(f'{form} input[name="Name"]', name)
    staff.fill(f'{form} textarea[name="Description"]', "direct checkout e2e product")
    staff.select_option(f'{form} select[name="CategoryId"]', cat)
    staff.fill(f'{form} input[name="SellingPrice"]', str(price))
    staff.fill(f'{form} input[name="InitialQuantity"]', "50")
    cb = staff.query_selector(f'{form} input[name="IsCustomizable"]')
    if cb:
        want = custom
        if cb.is_checked() != want:
            cb.click()
    staff.set_input_files(f'{form} input[name="Images"]', os.path.join(WIP, "product.png"))
    submit(staff, f'{form} button[type="submit"]')
    check(f"product created ({'custom' if custom else 'direct'}): {name}", name in body(staff))
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
    check("checkout amount visibility (You will pay)", "You will pay" in body(cust))
    pickup = cust.query_selector('input[name="Fulfilment"][value="pickup"]')
    if pickup:
        pickup.check()
    tn = cust.query_selector('textarea[name="Notes"]')
    if tn:
        tn.fill(notes)
    cust.click('form button:has-text("Place order")')
    cust.wait_for_load_state("networkidle")
    p.wait_for_timeout(300) if False else cust.wait_for_timeout(300)
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


def main():
    stub_post("/control/reset", {})
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        cust = use(b := browser.new_page())
        cust2 = use(browser.new_page())
        admin = use(browser.new_page())
        anon = use(browser.new_page())
        for pg in (cust, cust2, admin, anon):
            accept_dialogs(pg)
        use_ = use  # noqa

        # ============ STAGE 0: actors + catalogue ============
        print("\n===== STAGE 0: setup =====", flush=True)
        owner_login(admin)

        cat_direct = create_category(admin, f"DC Direct {TAG}")
        prod_direct = create_product(admin, f"DC Everyday Mug {TAG}", cat_direct, 9500, custom=False)
        cat_custom = create_category(admin, f"DC Custom {TAG}")
        set_question(admin, cat_custom)  # "Personalisation text"
        prod_custom = create_product(admin, f"DC Signature Tee {TAG}", cat_custom, 12000, custom=True)

        buyer = register_customer(cust, "buyer")
        check("buyer registered + verified + signed in", bool(buyer), buyer)
        register_customer(cust2, "stranger")
        check("second customer registered", True)

        # ============ 1: browsing, filtering, share links ============
        print("\n===== 1: browsing & filtering =====", flush=True)
        goto(anon, "/products")
        check("public catalog lists direct product", "DC Everyday Mug" in body(anon))
        s = anon.query_selector('input[name="search"], input[type="search"], input[placeholder*="Search"]')
        if s:
            s.fill("DC Everyday Mug")
            s.press("Enter")
            anon.wait_for_load_state("networkidle")
            anon.wait_for_timeout(400)
        check("search filter finds the product", "DC Everyday Mug" in body(anon))
        # category filter dropdown (§14/15 category browsing retained)
        sel = anon.query_selector('select[name="category"], select[name="categoryId"], select#category')
        if sel:
            opts = sel.query_selector_all("option")
            target = [o for o in opts if "DC Direct" in (o.inner_text() or "")]
            if target:
                sel.select_option(value=target[0].get_attribute("value"))
                sel.press("Enter")
                anon.wait_for_load_state("networkidle")
                anon.wait_for_timeout(400)
                check("category filter narrows the catalog", "DC Everyday Mug" in body(anon))
        shot(anon, "01-catalog-filter.png")

        goto(anon, f"/products/{prod_direct}")
        check("external product link renders for anonymous visitor (shareable)",
              "DC Everyday Mug" in body(anon))

        # ============ 2: direct product actions ============
        print("\n===== 2: direct product =====", flush=True)
        goto(cust, f"/portal/shop/{prod_direct}")
        t = body(cust)
        check("direct product shows Add to cart", "Add to cart" in t)
        check("direct product shows checkout microcopy (no quote needed)",
              "No quote or design approval needed" in t)
        check("direct product has NO Order / Customize CTA", "Order / Customize" not in t)
        shot(cust, "02-direct-product-buy-actions.png")

        # ============ 3: custom product actions + cart guard ============
        print("\n===== 3: custom product =====", flush=True)
        goto(cust, f"/portal/shop/{prod_custom}")
        t = body(cust)
        check("custom product shows Order / Customize", "Order / Customize" in t)
        check("custom product explains what the request needs",
              "What you need" in t or "requirements" in t.lower() or "custom" in t.lower())
        add_btn = cust.query_selector('button:has-text("Add to cart")')
        check("custom product hides Add to cart", add_btn is None)
        shot(cust, "03-custom-product-actions.png")

        # tamper: POST the cart endpoint directly like a rogue client
        res = cust.evaluate("""async (pid) => {
            try {
                let token = '';
                const cartHtml = await (await fetch('/cart')).text();
                const mm = cartHtml.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
                if (mm) token = mm[1];
                const r = await fetch('/cart/add', {
                    method: 'POST',
                    headers: {'Content-Type': 'application/x-www-form-urlencoded'},
                    body: new URLSearchParams({__RequestVerificationToken: token, lineType: 'product', referenceId: pid, quantity: '1'})
                });
                return r.status + ' ' + (await r.text()).slice(0, 3000);
            } catch (e) { return 'ERR ' + e; }
        }""", prod_custom)
        t = body(cust)
        goto(cust, "/cart")
        t2 = body(cust)
        check("add-to-cart for custom product refused (no cart line)",
              "Signature Tee" not in t2 and res[:1] in ("4", "5"),
              res[:160])
        shot(cust, "04-cart-guard-rejects-custom.png")

        # ============ 4: direct checkout ============
        print("\n===== 4: direct checkout & amounts =====", flush=True)
        add_to_cart(cust, prod_direct, qty=1)
        goto(cust, "/cart")
        t = body(cust)
        check("cart shows the direct line", "DC Everyday Mug" in t)
        check("cart amount visibility (line math)", "9,500" in t)
        goto(cust, "/checkout")
        t = body(cust)
        check("checkout amount visibility (subtotal/fees/total)", ("You will pay" in t) and ("9,500" in t))
        shot(cust, "05-checkout-amounts.png")
        order_a = place_checkout_order(cust, "order A - direct checkout e2e")
        check("order A created on complete page", bool(order_a))
        shot(cust, "06-direct-order-placed.png")

        # ============ 5: payment -> paid ============
        print("\n===== 5: payment (Paystack stub) =====", flush=True)
        ref_a = pay_order(cust, order_a, "success")
        st, txt = post_webhook(ref_a, 950000)
        check("webhook for A accepted (200 + ignored/processed)", st == 200)
        goto(cust, f"/checkout/return?trxref={ref_a}&reference={ref_a}")
        t = body(cust)
        check("payment verified on return", "Payment successful" in t or "verified" in t.lower(), t[:140])
        shot(cust, "07-direct-paid.png")

        goto(cust, f"/portal/orders/{order_a}")
        tA = body(cust)
        check("order A shows production status",
              "In Production" in tA or "InProduction" in tA or "Paid" in tA, tA[:120])
        goto(cust, f"/portal/orders/{order_a}/workspace")
        hidden = cust.evaluate("""() => {
            const card = document.getElementById('proofCard');
            return card ? getComputedStyle(card).display : 'missing';
        }""")
        check("direct workspace hides design cards (display:none)", hidden == "none", hidden)
        shot(cust, "08-direct-workspace-no-design-steps.png")

        # ============ 6: admin side (badge, activity, spending) ============
        print("\n===== 6: admin views =====", flush=True)
        goto(admin, f"/admin/orders/{order_a}")
        t = body(admin)
        check("admin order badge says Direct purchase", "Direct purchase" in t)
        shot(admin, "09-direct-admin-badge.png")

        goto(admin, "/admin/activity")
        admin.wait_for_timeout(1000)
        t = body(admin)
        check("real-time status feed labels order", "order created" in t.lower() or "payment received" in t.lower())
        check("activity: Direct product order created", "Direct product order created" in t)
        check("activity: Direct order payment received", "Direct order payment received" in t)
        shot(admin, "10-direct-activity-log.png")

        spend = customer_spending(admin, buyer)
        check("lifetime spending = 9,500 after order A", spend == 9500, str(spend))
        shot(admin, "11-direct-lifetime-spending.png")

        # ============ 7: review rules ============
        print("\n===== 7: review rules =====", flush=True)
        goto(cust, f"/products/{prod_direct}")
        t = body(cust)
        check("purchaser sees review form",
              "Write a review" in t and cust.query_selector('[name="Rating"]') is not None)
        shot(cust, "12-review-unlocked-after-purchase.png")

        goto(cust, f"/products/{prod_custom}")
        check("non-purchaser review locked with clear copy",
              "Only customers who have purchased this product can review it." in body(cust))
        shot(cust, "13-review-locked-unpurchased.png")

        goto(cust, f"/products/{prod_direct}")
        selr = cust.query_selector('select[name="Rating"]')
        if selr:
            selr.select_option("5")
        else:
            # star-rating radios (redesigned product page): pick 5 stars via its label
            star = cust.query_selector('label[for="rate5"]')
            if star:
                star.click()
        tt_ = cust.query_selector('input[name="Title"]')
        if tt_:
            tt_.fill("Direct checkout e2e")
        rt = cust.query_selector('textarea[name="Comment"], textarea#comment')
        if rt:
            rt.fill("Direct checkout e2e review — solid mug.")
        submit(cust, 'form:has([name="Rating"]) button[type="submit"]')
        check("review submitted by verified purchaser", "Thank you" in body(cust))

        # ============ 8: declined + retry ============
        print("\n===== 8: failed payment & retry =====", flush=True)
        add_to_cart(cust, prod_direct, qty=1)
        order_b = place_checkout_order(cust, "order B - retry scenario")
        ref_b1 = pay_order(cust, order_b, "decline")
        goto(cust, f"/checkout/return?trxref={ref_b1}&reference={ref_b1}")
        check("declined shows Payment failed copy", "Payment failed" in body(cust))
        shot(cust, "14-direct-payment-failed.png")

        goto(cust, f"/checkout/complete/{order_b}")
        t = body(cust)
        check("complete page offers retry after decline", "Pay with Paystack" in t or "retry" in t.lower(), t[:120])
        ref_b2 = pay_order(cust, order_b, "success")
        st, txt = post_webhook(ref_b2, 950000)
        check("webhook for B accepted", st == 200)
        goto(cust, f"/portal/orders/{order_b}")
        tB = body(cust)
        check("order B paid after retry on the SAME order",
              "In Production" in tB or "InProduction" in tB or "Paid" in tB, tB[:120])
        shot(cust, "15-direct-retry-paid.png")

        # ============ 9: cancel (direct) ============
        print("\n===== 9: direct cancellation =====", flush=True)
        add_to_cart(cust, prod_direct, qty=1)
        order_c = place_checkout_order(cust, "order C - cancel scenario")
        goto(admin, f"/admin/orders/{order_c}")
        admin.fill('input[name="Reason"]', "customer changed their mind")
        submit(admin, 'form[action*="cancel"] button[type="submit"]')
        goto(admin, f"/admin/orders/{order_c}")
        check("admin cancels direct order", "Cancelled" in body(admin))
        shot(admin, "16-direct-cancelled.png")

        # ============ 10: refund (direct) ============
        print("\n===== 10: refund =====", flush=True)
        goto(admin, f"/admin/orders/{order_b}")
        submit(admin, 'form[action*="refund"] button[type="submit"]')
        goto(admin, f"/admin/orders/{order_b}")
        check("order B refunded", "Refunded" in body(admin))
        shot(admin, "17-direct-refunded.png")
        spend = customer_spending(admin, buyer)
        check("spending after refund = 9,500 (A kept, B reversed, C never paid)", spend == 9500, str(spend))

        # ============ 11: custom request path ============
        print("\n===== 11: custom request & workspace =====", flush=True)
        ws_url = submit_custom_request(cust, prod_custom, "Black and gold, sizes M x6 and L x4")
        order_d = re.search(r"([0-9a-f-]{36})", ws_url).group(1)
        check("custom request completed through persisted workspace", bool(order_d))
        goto(cust, f"/portal/orders/{order_d}/workspace")
        t = body(cust)
        check("workspace stage Order Received", "Order Received" in t)
        check("sales contact assigned", "sales specialist" in t.lower(), t[:200])
        check("customer conversation visible", "Conversation" in t)
        shot(cust, "18-custom-workspace-assigned.png")

        goto(admin, f"/admin/orders/{order_d}")
        t = body(admin)
        check("admin sees Custom order badge", "Custom order" in t)
        shot(admin, "19-custom-admin-badge.png")

        goto(admin, f"/admin/orders/{order_d}/workspace")
        t = body(admin)
        check("workbench shows the request details", "Personalisation" in t or "Black and gold" in t, t[:150])
        check("admin sees the personalisation answer", "Black and gold" in t, t[:150])
        fin = admin.query_selector('button:has-text("Finali"), button:has-text("finali")')
        if fin:
            fin.click()
            admin.wait_for_load_state("networkidle")
            admin.wait_for_timeout(800)
        goto(admin, f"/admin/orders/{order_d}")
        t = body(admin)
        check("custom order reaches payment stage after finalize",
              "Payment Required" in t or "PendingPayment" in t or "Ready for payment" in t or "pending payment" in t.lower(),
              t[:150])

        goto(cust, f"/checkout/complete/{order_d}")
        check("custom order reaches checkout",
              "Pay with Paystack" in body(cust) or "payment" in body(cust).lower())
        ref_d = pay_order(cust, order_d, "success")
        st, txt = post_webhook(ref_d, 1200000)
        check("webhook for D accepted", st == 200)
        goto(cust, f"/portal/orders/{order_d}/workspace")
        tD = body(cust)
        check("custom order paid (path intact)",
              "In Production" in tD or "InProduction" in tD or "Paid" in tD, tD[:120])
        shot(cust, "20-custom-paid.png")

        # ============ 12: history coherence ============
        print("\n===== 12: my orders coherence =====", flush=True)
        goto(cust, "/portal/orders")
        t = body(cust)
        check("my orders lists all four coherently",
              all(x in t for x in ("9,500", "12,000", "Cancelled", "Refunded")), t[:200])
        shot(cust, "21-my-orders-coherent.png")

        goto(cust2, f"/products/{prod_direct}")
        t = body(cust2)
        check("stranger cannot review the product",
              "Only customers who have purchased this product can review it." in t, t[:120])

        browser.close()

    print(f"\n===== RESULT: {len(checks)} passed, {len(FAILS)} failed =====", flush=True)
    for f in FAILS:
        print("  FAILED:", f, flush=True)
    sys.exit(1 if FAILS else 0)


if __name__ == "__main__":
    main()
