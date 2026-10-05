"""Design-proof lifecycle E2E: chat media stays chat, proofs stay formal.

Journey (TAG-unique customer + catalog):
  staff: category -> options -> customizable product
  customer: register -> verify (Brevo stub token) -> order request
  staff: confirm -> upload proof v1
  customer: request changes (feedback) -> chat text+photo+video (no promotion)
  staff: bell notification -> upload v2
  customer: approve v2 (live toast on staff side, best-effort)
  security: second customer cannot view the order
  staff: finalize -> mark paid -> production -> ready -> completed
  both: full version/decision/media history intact after completion + reloads
"""
import json
import os
import re
import sys
import time

from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:5119"
WIP = "/home/user/e2e-proof-wip"
SHOTS = os.path.join(WIP, "final-shots")
CAPTURE = os.path.join(WIP, "brevo_captured.jsonl")
TAG = (sys.argv[1] if len(sys.argv) > 1 else "proof").lower().replace("_", "")[:10]
CHECKS = []



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


def shot(name):
    os.makedirs(SHOTS, exist_ok=True)
    pg = CURRENT[0]
    pg.screenshot(path=os.path.join(SHOTS, name), full_page=True)
    print("shot", name, flush=True)


CURRENT = [None]


def use(pg):
    CURRENT[0] = pg
    return pg


def goto(pg, path):
    pg.goto(BASE + path, wait_until="domcontentloaded")
    pg.wait_for_timeout(400)
    return pg


def expand_proofs(pg):
    """Open every proof <details> + the timeline: inner_text and screenshots
    only see rendered (open) nodes."""
    pg.eval_on_selector_all("#proofList details, #timelineCard",
                            "els => els.forEach(e => { e.open = true; })")
    pg.wait_for_timeout(300)


def submit_form(pg, form_sel, expect_url_part=None):
    form = pg.query_selector(form_sel)
    assert form, f"form missing: {form_sel} @ {pg.url}"
    btn = form.query_selector('button[type="submit"], input[type="submit"]')
    assert btn, f"submit missing in {form_sel} @ {pg.url}"
    btn.click()
    if expect_url_part:
        pg.wait_for_url(lambda u, p=expect_url_part: p in u, timeout=20000)
    else:
        pg.wait_for_timeout(2500)


def owner_login(pg):
    goto(pg, "/Account/Login")
    pg.fill('form[action*="Login"] input[name="Email"]', "owner@dechimexprinthub.com")
    # Reused sandbox app already had its password changed by an earlier run.
    for pw in ("OwnerE2E!2026xY", "ChangeMe123!"):
        pg.fill('form[action*="Login"] input[name="Password"]', pw)
        pg.click('form[action*="Login"] button[type="submit"]')
        pg.wait_for_timeout(2500)
        if "Login" not in pg.url:
            break
        if "Invalid email or password" not in pg.inner_text("body"):
            break
    else:
        raise AssertionError("owner login failed")
    if "password" in pg.url.lower():
        pg.fill('input[name="CurrentPassword"]', "ChangeMe123!")
        pg.fill('input[name="NewPassword"]', "OwnerE2E!2026xY")
        pg.fill('input[name="ConfirmPassword"]', "OwnerE2E!2026xY")
        pg.click('form[action*="password"] button[type="submit"]')
        pg.wait_for_timeout(3000)
    check("owner logged in", "/admin" in pg.url, pg.url)


def brevo_token(email, since=0.0):
    deadline = time.time() + 60
    while time.time() < deadline:
        if os.path.exists(CAPTURE):
            with open(CAPTURE) as f:
                lines = f.read().strip().split("\n")
            for line in lines:
                try:
                    m = json.loads(line)
                except Exception:
                    continue
                tos = json.dumps(m.get("to", []))
                if email in tos and "verif" in m.get("subject", "").lower():
                    html = m.get("htmlContent", "")
                    mt = verification_code(html)
                    if mt:
                        return mt.group(1)
        time.sleep(2)
    raise AssertionError(f"no verification token captured for {email}")


def register_customer(pg, tag_suffix):
    email = f"proof-{TAG}-{tag_suffix}@example.com"
    goto(pg, "/Account/Register")
    pg.fill('form[action*="Register"] input[name="FirstName"]', "Proof")
    pg.fill('form[action*="Register"] input[name="LastName"]', f"User{tag_suffix}")
    pg.fill('form[action*="Register"] input[name="Email"]', email)
    pg.fill('form[action*="Register"] input[name="Phone"]', "08030000001")
    pg.fill('form[action*="Register"] input[name="Password"]', "CustE2E!2026xY")
    conf = pg.query_selector('form[action*="Register"] input[name="ConfirmPassword"]')
    if conf:
        conf.fill("CustE2E!2026xY")
    pg.click('form[action*="Register"] button[type="submit"]')
    pg.wait_for_timeout(3000)
    check(f"register {tag_suffix} -> verify page", "erif" in pg.url or "portal" in pg.url.lower(), pg.url)
    if "portal" not in pg.url.lower():
        token = brevo_token(email)
        check(f"verification token captured for {tag_suffix}", bool(token))
        otp = pg.query_selector('input[name="Token"], input[name="token"], input[name="Code"]')
        assert otp, f"OTP input missing @ {pg.url}"
        otp.fill(token)
        pg.click('form button[type="submit"]')
        pg.wait_for_url(lambda u: "portal" in u.lower(), timeout=20000)
    check(f"customer {tag_suffix} signed in", "portal" in pg.url.lower(), pg.url)
    return email


with sync_playwright() as p:
    browser = p.chromium.launch()

    # ---------- STAGE A: staff builds catalog ----------
    staff = use(browser.new_page())
    owner_login(staff)

    cat_name = f"E2E Banners {TAG}"
    goto(staff, "/admin/categories/create")
    staff.fill('form[action*="create"] input[name="Name"]', cat_name)
    staff.fill('form[action*="create"] textarea[name="Description"]', "proof E2E category")
    staff.click('form[action*="create"] button[type="submit"]')
    staff.wait_for_url(lambda u: u.rstrip("/") == BASE + "/admin/categories", timeout=20000)
    check("category created", cat_name in staff.inner_text("body"))
    opt_link = staff.query_selector('a[href*="/options"]')
    # The list may hold rows from earlier runs: pick the options link on our row.
    hrefs = staff.eval_on_selector_all(
        "a[href*=\"/options\"]", "as => as.map(a => a.href)")
    cat_id = None
    rows = staff.query_selector_all("tr, li, .card")
    for href in hrefs:
        m = re.search(r"categories/([0-9a-f-]{36})/options", href)
        if m:
            cat_id = m.group(1)  # refined below by name match
    # Robust: find our category row by name, then its options link.
    nm = staff.eval_on_selector_all(
        "a", "as => as.map(a => [a.innerText.trim().slice(0,60), a.href])")
    for text, href in nm:
        m = re.search(r"categories/([0-9a-f-]{36})/options", href)
        if m:
            # verify this category is ours by visiting edit page title
            goto(staff, f"/admin/categories/edit/{m.group(1)}")
            if cat_name in staff.inner_text("body"):
                cat_id = m.group(1)
                break
    check("our category located", bool(cat_id))

    goto(staff, f"/admin/categories/{cat_id}/options")
    staff.wait_for_selector(".opt-row", timeout=10000)
    staff.fill(".opt-row .opt-question", "Slogan or design?")
    staff.select_option(".opt-row .opt-type", "Text")
    req = staff.query_selector(".opt-row .opt-required")
    if req and not req.is_checked():
        req.check()
    staff.click('#saveForm button[type="submit"]')
    staff.wait_for_timeout(2500)
    goto(staff, f"/admin/categories/{cat_id}/options")
    staff.wait_for_selector(".opt-row", timeout=10000)
    qval = staff.eval_on_selector(".opt-row .opt-question", "el => el.value")
    check("ordering question saved", qval == "Slogan or design?", repr(qval))

    prod_name = f"E2E Proof Banner {TAG}"
    goto(staff, "/admin/products/create")
    staff.fill('form[action*="create"] input[name="Name"]', prod_name)
    staff.fill('form[action*="create"] textarea[name="Description"]', "customizable proof E2E product")
    staff.select_option('form[action*="create"] select[name="CategoryId"]', cat_id)
    staff.fill('form[action*="create"] input[name="SellingPrice"]', "15000")
    staff.fill('form[action*="create"] input[name="InitialQuantity"]', "50")
    cust = staff.query_selector('form[action*="create"] input[name="IsCustomizable"]')
    if cust and not cust.is_checked():
        cust.check()
    staff.set_input_files('form[action*="create"] input[name="Images"]',
                          os.path.join(WIP, "product.png"))
    staff.click('form[action*="create"] button[type="submit"]')
    staff.wait_for_url(lambda u: "/admin/products" in u and "create" not in u, timeout=20000)
    check("product created", prod_name in staff.inner_text("body"))

    # ---------- STAGE B: customer registers + orders ----------
    cust = use(browser.new_page())
    register_customer(cust, "a")

    goto(cust, "/portal/shop")
    cust.wait_for_timeout(1500)  # let images/layout settle (avoid shift misclicks)
    link = cust.get_by_text(prod_name, exact=False).first
    link.wait_for(timeout=15000)
    link.scroll_into_view_if_needed()
    # The shop card links to the product page; the order form hangs under it.
    for _ in range(3):
        with cust.expect_navigation(wait_until="domcontentloaded", timeout=20000):
            link.click()
        if "/portal/shop/" in cust.url:
            break
        goto(cust, "/portal/shop")
        cust.wait_for_timeout(1500)
        link = cust.get_by_text(prod_name, exact=False).first
        link.scroll_into_view_if_needed()
    check("product page opens", "/portal/shop/" in cust.url, cust.url)
    if not re.search(r"/order(\?|$)", cust.url):
        order_link = cust.query_selector('a[href$="/order"], a[href*="/shop/"][href*="/order"]')
        assert order_link, f"no order link @ {cust.url}"
        with cust.expect_navigation(wait_until="domcontentloaded", timeout=20000):
            order_link.click()
    check("order form opens", re.search(r"/order(\?|$)", cust.url) is not None, cust.url)
    opt_input = cust.query_selector('input[name^="opt_"], textarea[name^="opt_"]')
    assert opt_input, f"ordering question missing @ {cust.url}"
    opt_input.fill("Logo on the back, sunset colours")
    qty = cust.query_selector('input[name="quantity"]')
    if qty:
        qty.fill("1")
    cust.click('form[action*="/order"] button[type="submit"]')
    cust.wait_for_url(lambda u: "/portal/orders" in u, timeout=20000)
    check("order request submitted", True)
    m = re.search(r"/portal/orders/([0-9a-f-]{36})/workspace", cust.url)
    if m:
        order_id = m.group(1)
    else:
        ws_link = cust.query_selector('a[href*="/workspace"]')
        assert ws_link, f"no workspace link @ {cust.url}"
        m = re.search(r"/portal/orders/([0-9a-f-]{36})/workspace", ws_link.get_attribute("href"))
        assert m, "order id not in workspace link"
        order_id = m.group(1)
        with cust.expect_navigation(wait_until="domcontentloaded", timeout=20000):
            ws_link.click()
    check("customer workspace opens", order_id in cust.url, cust.url)
    print("ORDER", order_id, flush=True)

    # ---------- STAGE C: staff confirms + uploads v1 ----------
    use(staff)
    goto(staff, f"/admin/orders/{order_id}/workspace")
    check("staff workspace shows request", "Logo on the back" in staff.inner_text("body"))
    confirm_btn = staff.query_selector(
        'form[action*="transition"] button[value="confirm"], form[action*="transition"] button:has-text("Confirm")')
    assert confirm_btn, "confirm transition button missing"
    confirm_btn.click()
    staff.wait_for_timeout(3000)
    goto(staff, f"/admin/orders/{order_id}/workspace")
    sbody = staff.inner_text("body")
    check("requirements confirmed",
          "RequirementsConfirmed" in sbody or "Requirements confirmed" in sbody, sbody[:200])

    staff.set_input_files('form[action*="roof"] input[type="file"]',
                          os.path.join(WIP, "proof_v1.png"))
    staff.fill('form[action*="roof"] input[name="staffNotes"]', "First draft — sunset palette")
    submit_form(staff, 'form[action*="roof"]')
    goto(staff, f"/admin/orders/{order_id}/workspace")
    body = staff.inner_text("body")
    check("v1 uploaded, pending", "Version 1" in body and "PendingApproval" in body)
    check("v1 submitted timestamp shown", "Submitted" in body)

    # ---------- STAGE D: customer requests changes ----------
    use(cust)
    goto(cust, f"/portal/orders/{order_id}/workspace")
    body = cust.inner_text("body")
    check("customer sees v1 awaiting approval",
          "Version 1" in body and "First draft" in body and "Submitted" in body)
    shot("01-customer-v1-awaiting.png")
    changes_form = cust.query_selector('.proof-actions form[action*="changes"]')
    assert changes_form, "request-changes form missing"
    cust.fill('.proof-actions form[action*="changes"] input[name="feedback"]',
              "Make the logo bigger and darker")
    submit_form(cust, '.proof-actions form[action*="changes"]')
    goto(cust, f"/portal/orders/{order_id}/workspace")
    body = cust.inner_text("body")
    check("v1 changes-requested with reason kept",
          "ChangesRequested" in body and "Make the logo bigger and darker" in body)
    shot("02-customer-changes-requested.png")

    # ---------- STAGE E: staff notified + uploads v2 ----------
    use(staff)
    goto(staff, "/admin/notifications")
    body = staff.inner_text("body")
    check("staff bell page shows change request",
          "Proof changes requested" in body and "logo bigger" in body)
    shot("03-staff-changes-notification.png")
    goto(staff, f"/admin/orders/{order_id}/workspace")
    body = staff.inner_text("body")
    check("staff sees v1 feedback", "Make the logo bigger and darker" in body)
    staff.set_input_files('form[action*="roof"] input[type="file"]',
                          os.path.join(WIP, "proof_v2.png"))
    staff.fill('form[action*="roof"] input[name="staffNotes"]', "Revised — bigger, darker logo")
    submit_form(staff, 'form[action*="roof"]')
    goto(staff, f"/admin/orders/{order_id}/workspace")
    expand_proofs(staff)
    body = staff.inner_text("body")
    check("v2 pending, v1 history preserved",
          "Version 2" in body and "PendingApproval" in body
          and "ChangesRequested" in body and "Make the logo bigger and darker" in body)

    # ---------- STAGE F: chat media never becomes a proof ----------
    use(cust)
    goto(cust, f"/portal/orders/{order_id}/workspace")
    cust.fill("#chatInput", "Sharing reference shots for the revision")
    cust.set_input_files("#attachInput", [os.path.join(WIP, "chat_photo.png")])
    cust.wait_for_selector("#stageBar:not([hidden]), .stage-thumb, #chatLog", timeout=10000)
    cust.click("#chatSend")
    cust.wait_for_selector('#chatLog img.msg-img, #chatLog img', timeout=30000)
    check("chat photo sent + rendered", True)
    cust.fill("#chatInput", "And a clip of the shade I mean")
    cust.set_input_files("#attachInput", [os.path.join(WIP, "clip.mp4")])
    cust.click("#chatSend")
    cust.wait_for_selector("#chatLog video", timeout=60000)
    check("chat video sent + rendered", True)
    proofs = cust.eval_on_selector_all("#proofList .proof-details",
                                       "els => els.map(e => e.innerText.slice(0,80))")
    check("proof card still exactly v1+v2 (chat media not promoted)",
          len(proofs) == 2, str(proofs))
    shot("04-customer-chat-media-plus-proofs.png")

    # ---------- STAGE G: customer approves v2 (staff watches live) ----------
    goto(staff, f"/admin/orders/{order_id}/workspace")
    approve_form = cust.query_selector('.proof-actions form[action*="approve"]')
    assert approve_form, "approve form missing on v2"
    cust.click('.proof-actions form[action*="approve"] button[type="submit"]')
    cust.wait_for_timeout(3000)
    try:
        staff.get_by_text("Design approved", exact=False).first.wait_for(timeout=12000)
        check("staff sees live approval toast", True)
    except Exception:
        print("warn: live toast not observed; verifying via reload", flush=True)
    goto(cust, f"/portal/orders/{order_id}/workspace")
    expand_proofs(cust)
    body = cust.inner_text("body")
    check("v2 approved with decision time",
          "Approved" in body and re.search(r"Approved \d", body) is not None)
    check("v1 rejection history intact after approval",
          "ChangesRequested" in body and "Make the logo bigger and darker" in body)
    shot("05-customer-approved-history.png")
    goto(staff, f"/admin/orders/{order_id}/workspace")
    expand_proofs(staff)
    body = staff.inner_text("body")
    check("staff sees approval + full history",
          "Approved" in body and "Make the logo bigger and darker" in body)

    # ---------- STAGE H: second customer is locked out ----------
    cust2 = browser.new_page()
    register_customer(cust2, "b")
    resp = cust2.goto(f"{BASE}/portal/orders/{order_id}/workspace",
                      wait_until="domcontentloaded")
    t2 = cust2.inner_text("body")
    check("other customer cannot view order",
          (resp is not None and resp.status == 404) or "not found" in t2.lower()
          or "Version 1" not in t2, f"status={resp.status if resp else '?'}")
    use(cust2)
    shot("06-other-customer-denied.png")
    use(cust)

    # ---------- STAGE I: finalize -> paid -> completed ----------
    use(staff)
    goto(staff, f"/admin/orders/{order_id}/workspace")
    fin = staff.query_selector('form[action*="inalize"] button[type="submit"]')
    assert fin, "finalize submit missing (order may not be finalizable)"
    submit_form(staff, 'form[action*="inalize"]')
    goto(staff, f"/admin/orders/{order_id}/workspace")
    check("finalized to pending payment",
          "PendingPayment" in staff.inner_text("body") or "pending payment" in staff.inner_text("body").lower())
    goto(cust, f"/portal/orders/{order_id}/workspace")
    check("customer sees pay button", "Review & pay" in cust.inner_text("body"))

    goto(staff, f"/admin/orders/{order_id}")
    mp = staff.query_selector('form[action*="mark-paid"]')
    assert mp, "mark-paid form missing"
    staff.select_option('form[action*="mark-paid"] select[name="Method"]', "BankTransfer")
    staff.fill('form[action*="mark-paid"] input[name="Note"]', f"E2E-{TAG}")
    submit_form(staff, 'form[action*="mark-paid"]')
    goto(staff, f"/admin/orders/{order_id}")
    check("marked paid", "Start production" in staff.inner_text("body"))
    for label, expect in [("Start production", "Mark ready"),
                          ("Mark ready", "Mark completed"),
                          ("Mark completed", "Completed")]:
        btn = staff.get_by_text(label, exact=True).first
        btn.wait_for(timeout=10000)
        btn.click()
        staff.wait_for_timeout(3000)
        goto(staff, f"/admin/orders/{order_id}")
        check(f"{label} -> {expect}", expect in staff.inner_text("body"))

    # ---------- STAGE J: completed-order history (both sides + reload) ----------
    use(cust)
    goto(cust, f"/portal/orders/{order_id}/workspace")
    expand_proofs(cust)
    body = cust.inner_text("body")
    check("completed: customer keeps v1 rejection + reason",
          "ChangesRequested" in body and "Make the logo bigger and darker" in body)
    check("completed: customer keeps v2 approval + time", "Approved" in body)
    check("completed: customer keeps chat media",
          cust.query_selector("#chatLog img") is not None
          and cust.query_selector("#chatLog video") is not None)
    check("completed: customer keeps timeline",
          "proof-changes-requested" in body.lower() or "requested changes" in body.lower()
          or "Timeline" in body)
    shot("07-customer-completed-history.png")
    goto(cust, f"/portal/orders/{order_id}/workspace")
    expand_proofs(cust)
    check("completed: customer history survives reload",
          "Make the logo bigger and darker" in cust.inner_text("body")
          and cust.query_selector("#chatLog video") is not None)

    use(staff)
    goto(staff, f"/admin/orders/{order_id}/workspace")
    expand_proofs(staff)
    body = staff.inner_text("body")
    check("completed: staff keeps full proof history",
          "Version 1" in body and "Version 2" in body
          and "Make the logo bigger and darker" in body and "Approved" in body)
    shot("08-staff-completed-history.png")

    browser.close()

print(f"\nE2E GREEN: {len(CHECKS)} checks, TAG={TAG}")
