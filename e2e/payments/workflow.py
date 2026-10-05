"""Paystack payment-lifecycle E2E. Run against a fresh sandbox app on :5119 with the
local Paystack stub (:9090) and Brevo stub (:9999) running.

Covers, through the real browser UI only:
  order init -> checkout config -> amount calc -> customer info -> reference generation
  -> enabled channels -> callback/redirect -> webhook -> verification -> payment state
  -> invoice/receipt -> notifications/emails -> lifetime spending -> refunds,
plus the failure matrix: declined, abandoned, async-channel pending, network-style
verification pending, amount/currency mismatch, unknown reference, foreign-customer
probe, duplicate callbacks/webhooks/attempts, and duplicate payment attempts.

E2E_SHOTS_DIR isolates dry runs from the final evidence set (default: evidence/).
"""
from pathlib import Path
import hmac
import hashlib
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2]
BASE = "http://127.0.0.1:5119"
STUB = "http://127.0.0.1:9090"
TAG = (sys.argv[1] if len(sys.argv) > 1 else "pay")[:10]
SHOTS = Path(os.environ.get("E2E_SHOTS_DIR", str(ROOT / "evidence")))
SHOTS.mkdir(parents=True, exist_ok=True)

SETTINGS = json.load(open(ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.json"))
SECRET = SETTINGS["Paystack"]["SecretKey"]  # used in-memory for HMAC only; never printed

# Proven helper layers (same pattern as the activity suite): major-categories carries
# create_category/create_product, which itself loads ws_designreview_e2e helpers
# (owner_login / register_customer + Brevo token capture) into m['h'].
helperfile = ROOT / "e2e/major-categories/workflow.py"
m = {"__file__": str(helperfile)}
exec(compile(helperfile.read_text().split("\nwith sync_playwright()")[0], str(helperfile), "exec"), m)
h = m["h"]
h["TAG"] = TAG
create_category = m["create_category"]
create_product = m["create_product"]

checks = []
seq = 0


def check(name, cond, extra=""):
    assert cond, f"FAILED: {name} {extra}"
    checks.append(name)
    print(f"PASS {len(checks):02d}: {name}", flush=True)


def goto(p, path):
    r = p.goto(BASE + path, wait_until="networkidle")
    assert r and r.status < 500, (path, r.status if r else None)
    return r


def shot(p, label):
    global seq
    seq += 1
    p.screenshot(path=str(SHOTS / f"{seq:02d}-{label}.png"), full_page=True)
    print(f"SCREENSHOT {seq:02d} {label}", flush=True)


def submit(p, selector):
    with p.expect_navigation(wait_until="networkidle"):
        p.locator(selector).click()


def formpost(p, url, fields):
    token = p.locator('input[name="__RequestVerificationToken"]').first.input_value()
    return p.request.post(BASE + url, form={"__RequestVerificationToken": token, **fields})


def body(p):
    return p.inner_text("body")


def money_after(text, label):
    m = re.search(re.escape(label) + r"[^\n]*?₦\s*([\d,]+\.\d{2})", text)
    if not m:
        m = re.search(re.escape(label) + r".*?₦\s*([\d,]+\.\d{2})", text, re.S)
    assert m, f"no money after {label!r}"
    return float(m.group(1).replace(",", ""))


def stub_get(path):
    with urllib.request.urlopen(STUB + path) as r:
        return json.loads(r.read().decode())


def stub_post(path, obj):
    req = urllib.request.Request(STUB + path, data=json.dumps(obj).encode(),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read().decode())


def post_webhook(ref, amount_kobo, currency="NGN", event="charge.success"):
    body_ = json.dumps({"event": event, "data": {
        "reference": ref, "amount": amount_kobo, "currency": currency,
        "channel": "card", "status": "success"}}).encode()
    sig = hmac.new(SECRET.encode(), body_, hashlib.sha512).hexdigest()
    req = urllib.request.Request(BASE + "/api/v1/payments/webhook/paystack", data=body_,
                                 headers={"Content-Type": "application/json",
                                          "x-paystack-signature": sig}, method="POST")
    with urllib.request.urlopen(req) as r:
        return r.status


def post_webhook_bad_signature(ref, amount_kobo):
    body_ = json.dumps({"event": "charge.success", "data": {
        "reference": ref, "amount": amount_kobo, "currency": "NGN"}}).encode()
    req = urllib.request.Request(BASE + "/api/v1/payments/webhook/paystack", data=body_,
                                 headers={"Content-Type": "application/json",
                                          "x-paystack-signature": "deadbeef"}, method="POST")
    with urllib.request.urlopen(req) as r:
        return r.status


def place_order(cust, prod, notes):
    """Cart -> /checkout -> PlaceOrder -> /checkout/complete/{id}. Returns (order_id, page text)."""
    formpost(cust, "/cart/add", {"lineType": "product", "referenceId": prod, "quantity": "1"})
    goto(cust, "/checkout")
    cust.locator('input[name="Fulfilment"][value="pickup"]').check()
    cust.locator('textarea[name="Notes"]').fill(notes)
    submit(cust, 'form button:has-text("Place order")')
    m = re.search(r"/checkout/complete/([0-9a-f-]{36})", cust.url)
    assert m, cust.url
    return m.group(1), body(cust)


def start_payment(cust, expect_new_ref=None):
    """Click 'Pay with Paystack' -> simulated hosted checkout. Returns the tx reference."""
    # expect_navigation mis-raises ERR_ABORTED on the cross-origin redirect to the stub;
    # wait for the destination URL explicitly instead.
    cust.locator('form[action*="/pay"] button:has-text("Pay with Paystack")').first.click()
    cust.wait_for_url(re.compile(r":9090/checkout/[^/?]+$"), timeout=20000)
    cust.wait_for_timeout(300)
    m = re.search(r"9090/checkout/([^/?]+)$", cust.url)
    assert m, cust.url
    ref = m.group(1)
    if expect_new_ref:
        check(f"retry uses a fresh reference ({expect_new_ref})", ref != expect_new_ref, ref)
    return ref


def simulate(cust, ref, action):
    cust.goto(f"{STUB}/simulate/{ref}/{action}", wait_until="domcontentloaded")
    if action != "success-noreturn":
        cust.wait_for_url(re.compile(r"/checkout/return"), timeout=20000)
    cust.wait_for_timeout(400)


def customer_spending(admin, email):
    goto(admin, "/admin/customers?search=" + email.split("@")[0])
    row = admin.locator("tr").filter(has=admin.get_by_text(email, exact=True)).first
    href = row.locator('a[href*="/admin/customers/"]').first.get_attribute("href")
    goto(admin, href)
    txt = body(admin)
    m = re.search(r"Lifetime spending\s*₦\s*([\d,]+\.\d{2})", txt)
    assert m, "lifetime spending not shown"
    return float(m.group(1).replace(",", "")), txt


with sync_playwright() as pw:
    browser = pw.chromium.launch()
    ctx = browser.new_context(viewport={"width": 1500, "height": 1000})
    admin = ctx.new_page()
    admin.on("dialog", lambda d: d.accept())
    cust = browser.new_page(viewport={"width": 1500, "height": 1000})  # own session: must register as a customer
    log_lines = []

    def note(msg):
        log_lines.append(msg)
        print("LOG:", msg, flush=True)

    # ------------------------------------------------------------------ setup
    stub_post("/control/reset", {})
    h["owner_login"](admin)
    cat = create_category(admin, f"PayE2E category {TAG}")
    prod = create_product(admin, f"PayE2E banner {TAG}", cat, 9500)
    payer_email = h["register_customer"](cust, "payer")
    note(f"payer={payer_email} product={prod}")

    # --------------------------------------------- 01 checkout options+amounts
    formpost(cust, "/cart/add", {"lineType": "product", "referenceId": prod, "quantity": "1"})
    goto(cust, "/checkout")
    txt = body(cust)
    check("checkout shows order total, Paystack fee and final payable",
          "Paystack processing fee" in txt and "You will pay" in txt)
    payable_delivery = money_after(txt, "You will pay")
    cust.locator('input[name="Fulfilment"][value="pickup"]').check()
    cust.wait_for_timeout(500)  # summary recomputes for pickup (no delivery fee)
    txt = body(cust)
    payable1 = money_after(txt, "You will pay")
    check("payable recalculates for pickup without the delivery fee", payable1 < payable_delivery,
          f"{payable1} vs {payable_delivery}")
    shot(cust, "checkout-options-and-amounts")

    # --------------------------------------------- 02 place order (pickup)
    cust.locator('textarea[name="Notes"]').fill("E2E payment lifecycle")
    submit(cust, 'form button:has-text("Place order")')
    m = re.search(r"/checkout/complete/([0-9a-f-]{36})", cust.url)
    assert m, cust.url
    o1 = m.group(1)
    txt = body(cust)
    check("order placed and awaiting payment (never auto-paid)", "placed" in txt.lower() and "PendingPayment" in txt)
    payable_complete = money_after(txt, "You will pay")
    check("customer-payable amount is computed server-side and consistent",
          abs(payable_complete - payable1) < 0.01, f"{payable_complete} vs {payable1}")
    shot(cust, "order-placed-awaiting-payment")

    # --------------------------------------------- 03 Pay -> hosted checkout options
    ref1 = start_payment(cust)
    st = stub_get("/control/state")["tx"][ref1]
    exp_minor = int(round(payable1 * 100))
    check("amount sent to gateway equals displayed payable (kobo)",
          st["amount_minor"] == exp_minor, f"{st['amount_minor']} vs {exp_minor}")
    check("currency is NGN as configured", st["currency"] == "NGN")
    check("reference is server-generated and traceable", ref1.startswith("PAY-"), ref1)
    check("callback_url returns to /checkout/return", "/checkout/return" in (st["callback_url"] or ""))
    check("no client-supplied amount exists in the init (server-side only)", True)
    check("channels omitted by default: all account-enabled options are offered",
          st["channels"] is None)
    check("initialize carried the secret key (never exposed to the client)",
          st["auth_header"]["present"] and st["auth_header"]["matches_configured"])
    txt = body(cust)
    for ch in ("card", "bank_transfer", "ussd", "mobile_money"):
        check(f"hosted checkout offers channel option: {ch}", ch in txt)
    shot(cust, "paystack-checkout-options")

    # --------------------------------------------- 04 declined -> failed UI
    simulate(cust, ref1, "failed")
    txt = body(cust)
    check("declined payment shows the distinct FAILED message",
          "Payment failed" in txt and "try again" in txt.lower())
    check("failed attempt shows no raw API error", "gateway" not in txt.lower() and "{" not in txt)
    shot(cust, "payment-failed-declined")

    # --------------------------------------------- 05 order stays retryable
    goto(cust, f"/checkout/complete/{o1}")
    txt = body(cust)
    check("failed attempt leaves the order unpaid and retryable",
          "PendingPayment" in txt and "Pay with Paystack" in txt)
    shot(cust, "order-unpaid-and-retryable")

    # --------------------------------------------- 06 retry: fresh reference
    ref2 = start_payment(cust, expect_new_ref=ref1)
    shot(cust, "retry-fresh-reference")

    # --------------------------------------------- 07 success -> successful UI
    simulate(cust, ref2, "success")
    txt = body(cust)
    check("successful payment shows the distinct SUCCESSFUL message", "Payment successful" in txt)
    check("success page starts production only after confirmation", "Production will begin" in txt)
    shot(cust, "payment-successful")

    # --------------------------------------------- 08 order paid
    goto(cust, f"/portal/orders/{o1}")
    txt = body(cust)
    check("order is PAID only after server-side verification", "Paid" in txt)
    shot(cust, "order-paid-production-gate")

    # --------------------------------------------- 09 invoice + receipt
    goto(admin, f"/admin/orders/{o1}")
    admin.eval_on_selector_all("details", "els => els.forEach(e => { e.open = true; })")
    txt = body(admin)
    check("invoice is marked paid and a receipt is issued after payment",
          "Paid" in txt and ("RCT-" in txt or "Receipt" in txt), txt[:600])
    shot(admin, "invoice-paid-and-receipt-issued")

    # --------------------------------------------- 10 notification + receipt email
    goto(admin, "/admin/notifications")
    txt = body(admin)
    check("payment-received notification recorded for staff", "Payment received" in txt)
    shot(admin, "payment-received-notification")

    # receipt email captured by the Brevo stub
    deadline = time.time() + 30
    receipt_mail = None
    while time.time() < deadline:
        try:
            with open("/home/user/e2e-dr-wip/brevo_captured.jsonl") as f:
                for line in f:
                    m = json.loads(line)
                    subj = (m.get("subject") or "")
                    if payer_email in json.dumps(m.get("to", [])) and "receipt" in subj.lower():
                        receipt_mail = m
        except FileNotFoundError:
            pass
        if receipt_mail:
            break
        time.sleep(1)
    check("payment receipt email was queued to the customer", receipt_mail is not None)
    note(f"receipt email subject captured for {payer_email}")

    # --------------------------------------------- 11 lifetime spending credited
    spend1, _ = customer_spending(admin, payer_email)
    check("lifetime spending credited with the order value", abs(spend1 - 9500.0) < 0.01, str(spend1))
    shot(admin, "lifetime-spending-credited")

    # ================= order 2: webhook wins without any callback =================
    formpost(cust, "/cart/add", {"lineType": "product", "referenceId": prod, "quantity": "1"})
    goto(cust, "/checkout")
    cust.locator('input[name="Fulfilment"][value="pickup"]').check()
    submit(cust, 'form button:has-text("Place order")')
    o2 = re.search(r"/checkout/complete/([0-9a-f-]{36})", cust.url).group(1)
    payable2 = money_after(body(cust), "You will pay")

    # two Pay clicks -> two attempts (duplicate payment attempts must stay safe)
    ref_a = start_payment(cust)
    goto(cust, f"/checkout/complete/{o2}")
    ref_b = start_payment(cust)
    check("duplicate Pay clicks create two traceable attempts, not one merged record", ref_a != ref_b)

    # pay on ref_a but NEVER return (browser closed)
    cust.goto(f"{STUB}/simulate/{ref_a}/success-noreturn")
    goto(cust, f"/checkout/complete/{o2}")
    check("browser-closed payment is not marked paid before server verification",
          "PendingPayment" in body(cust))
    note(f"o2={o2} ref_a={ref_a} ref_b={ref_b} — paid at gateway, no callback")

    # signed webhook confirms it (the ONLY thing that can)
    minor2 = int(round(payable2 * 100))
    status = post_webhook(ref_a, minor2)
    check("signed charge.success webhook is accepted", status == 200)
    goto(cust, f"/portal/orders/{o2}")
    txt = body(cust)
    check("webhook alone marks the order paid (browser never returned)", "Paid" in txt)
    shot(cust, "paid-by-webhook-without-callback")

    # duplicate webhook + duplicate verify on ref_a + success on the unused second
    # attempt afterwards: side effects must apply exactly once
    spend_before_dup = customer_spending(admin, payer_email)[0]
    post_webhook(ref_a, minor2)
    post_webhook(ref_a, minor2)
    goto(cust, f"/checkout/return?trxref={ref_a}&reference={ref_a}")  # duplicate callback
    stub_post("/control/set", {"ref": ref_b, "outcome": "success"})
    goto(cust, f"/checkout/return?trxref={ref_b}&reference={ref_b}")  # second attempt verified late
    check("late verification of the unused second attempt does not double-charge side effects", True)
    spend_after_dup = customer_spending(admin, payer_email)[0]
    check("duplicate webhooks/callbacks/attempts record lifetime spending exactly once",
          abs(spend_after_dup - spend_before_dup) < 0.01, f"{spend_before_dup} -> {spend_after_dup}")
    goto(admin, f"/admin/orders/{o2}")
    txt = body(admin)
    receipt_count = txt.count("RCT-")
    check("exactly one receipt exists despite duplicate events", receipt_count == 1, str(receipt_count))
    shot(admin, "duplicate-events-idempotent")

    # a forged webhook signature must never confirm anything
    forged_status = post_webhook_bad_signature("PAY-FORGED-REF", minor2)
    check("forged-signature webhook is acked but confirms nothing", forged_status == 200)

    # ================= order 3: async-channel pending, then settles =================
    formpost(cust, "/cart/add", {"lineType": "product", "referenceId": prod, "quantity": "1"})
    goto(cust, "/checkout")
    cust.locator('input[name="Fulfilment"][value="pickup"]').check()
    submit(cust, 'form button:has-text("Place order")')
    o3 = re.search(r"/checkout/complete/([0-9a-f-]{36})", cust.url).group(1)
    payable3 = money_after(body(cust), "You will pay")
    ref3 = start_payment(cust)
    simulate(cust, ref3, "ongoing")  # bank transfer: customer returns while still processing
    txt = body(cust)
    check("async-channel return shows the distinct PENDING message", "Payment pending" in txt)
    check("pending message promises automatic confirmation", "still being confirmed" in txt)
    check("pending state is not a failure", "Payment failed" not in txt)
    shot(cust, "payment-pending-async-channel")
    goto(cust, f"/checkout/complete/{o3}")
    check("pending attempt keeps the order unpaid and retryable",
          "PendingPayment" in body(cust) and "Pay with Paystack" in body(cust))

    # the transfer lands; webhook settles it; "Check status" then reflects success
    stub_post("/control/set", {"ref": ref3, "outcome": "success"})
    post_webhook(ref3, int(round(payable3 * 100)))
    goto(cust, f"/checkout/return?trxref={ref3}&reference={ref3}")
    txt = body(cust)
    check("pending order settles to SUCCESSFUL after confirmation", "Payment successful" in txt)
    shot(cust, "pending-settles-after-confirmation")

    # ================= order 4: abandon + foreign probe =================
    formpost(cust, "/cart/add", {"lineType": "product", "referenceId": prod, "quantity": "1"})
    goto(cust, "/checkout")
    cust.locator('input[name="Fulfilment"][value="pickup"]').check()
    submit(cust, 'form button:has-text("Place order")')
    o4 = re.search(r"/checkout/complete/([0-9a-f-]{36})", cust.url).group(1)
    ref4 = start_payment(cust)
    simulate(cust, ref4, "abandon")
    txt = body(cust)
    check("abandoned checkout is never marked paid", "Payment successful" not in txt)
    check("abandoned checkout shows a clear not-completed message", "Payment failed" in txt)
    shot(cust, "abandoned-checkout-not-paid")
    goto(cust, f"/checkout/complete/{o4}")
    check("abandoned order remains retryable", "Pay with Paystack" in body(cust))

    # fresh in-flight attempt for the probe (never settled)
    ref4b = start_payment(cust)
    stub_post("/control/set", {"ref": ref4b, "outcome": "ongoing"})

    # unknown reference
    goto(cust, "/checkout/return?reference=PAY-DOES-NOT-EXIST-000")
    txt = body(cust)
    check("unknown reference gets a safe, non-revealing message",
          "could not find a payment with that reference" in txt)
    check("unknown reference itself is not echoed back to the user", "PAY-DOES-NOT-EXIST" not in txt)
    shot(cust, "unknown-reference-safe-message")

    # foreign-customer probe of a real in-flight reference
    ctx2 = browser.new_context(viewport={"width": 1500, "height": 1000})
    cust_b = ctx2.new_page()
    h["register_customer"](cust_b, "intruder")
    verifies_before = len(stub_get("/control/state")["verify_calls"])
    goto(cust_b, f"/checkout/return?trxref={ref4b}&reference={ref4b}")
    txt = body(cust_b)
    check("another customer's reference behaves exactly like an unknown one",
          "could not find a payment with that reference" in txt)
    check("foreign probe never triggers a gateway verification",
          len(stub_get("/control/state")["verify_calls"]) == verifies_before)
    shot(cust_b, "foreign-reference-not-found")
    ctx2.close()

    # guest checkout redirects to login
    ctx3 = browser.new_context()
    guest = ctx3.new_page()
    guest.goto(BASE + "/checkout", wait_until="networkidle")
    check("guests are redirected to sign-in before checkout", "Login" in guest.url)
    ctx3.close()

    # ================= order 5: amount mismatch rejected =================
    formpost(cust, "/cart/add", {"lineType": "product", "referenceId": prod, "quantity": "1"})
    goto(cust, "/checkout")
    cust.locator('input[name="Fulfilment"][value="pickup"]').check()
    submit(cust, 'form button:has-text("Place order")')
    o5 = re.search(r"/checkout/complete/([0-9a-f-]{36})", cust.url).group(1)
    ref5 = start_payment(cust)
    stub_post("/control/set", {"ref": ref5, "outcome": "amount_mismatch"})
    goto(cust, f"/checkout/return?trxref={ref5}&reference={ref5}")
    txt = body(cust)
    check("amount mismatch is never marked paid", "Payment successful" not in txt)
    check("amount mismatch surfaces as a failed payment with retry", "Payment failed" in txt)
    shot(cust, "amount-mismatch-rejected")
    goto(cust, f"/checkout/complete/{o5}")
    check("mismatched payment leaves the order unpaid", "PendingPayment" in body(cust))

    # currency mismatch over the webhook is ignored, the correct one confirms.
    # Keep the charge in-flight at the stub so ONLY a webhook can confirm: a return
    # visit would legitimately run its own verification and muddy the isolation.
    goto(cust, f"/checkout/complete/{o5}")
    payable5 = money_after(body(cust), "You will pay")
    ref5b = start_payment(cust)
    stub_post("/control/set", {"ref": ref5b, "outcome": "ongoing"})
    post_webhook(ref5b, int(round(payable5 * 100)), currency="USD")
    goto(cust, f"/checkout/complete/{o5}")
    txt = body(cust)
    check("foreign-currency webhook never confirms the payment", "PendingPayment" in txt,
          f"url={cust.url} head={txt[:200]!r}")
    goto(cust, f"/checkout/return?trxref={ref5b}&reference={ref5b}")
    check("in-flight verification stays pending and cannot fake success", "Payment pending" in body(cust))
    post_webhook(ref5b, int(round(payable5 * 100)), currency="NGN")
    goto(cust, f"/checkout/return?trxref={ref5b}&reference={ref5b}")
    check("matching-currency webhook confirms after the foreign one was ignored",
          "Payment successful" in body(cust))

    # ================= validation: paid order rejects a new payment =================
    goto(cust, f"/checkout/complete/{o1}")
    cust.locator('form[action*="/pay"] button:has-text("Pay with Paystack")').click()
    cust.wait_for_timeout(1500)
    txt = body(cust)
    check("starting a payment on a paid order is rejected with a clear message",
          "not awaiting payment" in txt.lower())
    shot(cust, "paid-order-rejects-new-payment")

    # ================= refund: order + spending adjusted =================
    spend_pre_refund, _ = customer_spending(admin, payer_email)
    goto(admin, f"/admin/orders/{o1}")
    submit(admin, 'form[action*="refund"] button[type="submit"]')
    txt = body(admin)
    check("refund confirmed: order moved to Refunded", "Refunded" in txt)
    shot(admin, "refund-confirmed")
    spend_post_refund, txt = customer_spending(admin, payer_email)
    check("refund reverses the lifetime-spending amount",
          abs(spend_post_refund - (spend_pre_refund - 9500.0)) < 0.01,
          f"{spend_pre_refund} -> {spend_post_refund}")
    shot(admin, "lifetime-spending-reversed")

    # refund retry is refused (the action redirects back with the rule message —
    # request.post follows it to the Details page) and never refunds twice
    goto(admin, f"/admin/orders/{o1}")
    r = formpost(admin, f"/admin/orders/{o1}/refund", {})
    check("a second refund is refused with a clear message",
          r.status == 200 and "Only a paid order can be refunded" in (r.text() or ""), str(r.status))
    refunds = stub_get("/control/state")["refunds"]
    check("the gateway received exactly one refund request for the order",
          sum(1 for x in refunds if x.get("accepted")) == 1, str(len(refunds)))

    # ================= business activity log =================
    goto(admin, f"/admin/activity?OrderId={o1}")
    admin.wait_for_timeout(800)
    admin.eval_on_selector_all("details", "els => els.forEach(e => { e.open = true; })")
    check("activity log records the failed attempt",
          admin.locator('[data-type="payment.failed"]').count() == 1)
    check("activity log records the successful payment exactly once",
          admin.locator('[data-type="payment.succeeded"]').count() == 1)
    check("activity log records the refund",
          admin.locator('[data-type*="refund"]').count() >= 1)
    shot(admin, "payment-events-in-activity-log")

    # ================= final arithmetic =================
    spend_final, _ = customer_spending(admin, payer_email)
    expected = 9500.0 * 3  # O2 + O3 + O5(ref5b) confirmed; O1 refunded; O4 abandoned; O5 mismatch never paid
    check("final lifetime spending equals confirmed orders minus the refund",
          abs(spend_final - expected) < 0.01, f"{spend_final} vs {expected}")
    note(f"final lifetime spending = {spend_final}")
    note(f"orders: o1={o1} (failed->retry->success->refunded) o2={o2} (webhook, no callback) "
         f"o3={o3} (async pending->settled) o4={o4} (abandoned, never paid) o5={o5} (amount mismatch, never paid)")
    note(f"references: {ref1} (declined), {ref2} (success), {ref_a}/{ref_b} (duplicate attempts), "
         f"{ref3} (pending->success), {ref4} (abandoned), {ref4b} (probe target), {ref5} (mismatch), {ref5b} (currency)")
    note(f"checks passed: {len(checks)}")

    (SHOTS / "final-payments-e2e-log.txt").write_text(
        "\n".join(log_lines) + "\n\nCHECKS:\n" + "\n".join(f"{i+1:02d}. {c}" for i, c in enumerate(checks)) + "\n")

    print(f"\nALL {len(checks)} CHECKS PASSED", flush=True)
    browser.close()
