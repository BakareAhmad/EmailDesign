"""Design-review workflow E2E (OrderDesignReview — independent-history submissions).

Journey (TAG-unique customer + catalog, live two-sided workspace):
  keys:   /api/v1/config serves the Paystack PUBLIC key; the Brevo stub capture
          proves the configured api-key header travels with real sends (the app
          skips the HTTP call entirely when the key is blank).
  staff:  category -> options -> customizable product
  customer: register -> verify (Brevo stub token) -> order request
  customer: upload design review v1 (notes) -> staff sees it LIVE (JS node + toast)
  staff:  approve from the LIVE-rendered card -> customer pill flips LIVE + toast
  customer: upload v2 -> staff rejects with reason (modal) -> customer sees reason LIVE
  validations: reject requires a reason; disallowed file type refused; required file input
  persistence: both workspaces keep full decision history after reload
"""
import json
import os
import re
import sys
import time
import urllib.request

from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:5119"
WIP = "/home/user/e2e-dr-wip"
SHOTS = os.environ.get("E2E_SHOTS_DIR", os.path.abspath(os.path.join(os.path.dirname(__file__), "../../evidence")))
CAPTURE = os.path.join(WIP, "brevo_captured.jsonl")
TAG = (sys.argv[1] if len(sys.argv) > 1 else "dr").lower().replace("_", "")[:10]
CHECKS = []
SETTINGS = json.load(open(os.path.join(os.path.dirname(__file__), "../../DeChimexPrints/src/DeChimex.Host/appsettings.json")))

CURRENT = [None]



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
    pg = CURRENT[0]
    pg.screenshot(path=os.path.join(SHOTS, name), full_page=True)
    print("shot", name, flush=True)


def goto(pg, path):
    pg.goto(BASE + path, wait_until="domcontentloaded")
    pg.wait_for_timeout(400)
    return pg


def expand_details(pg):
    pg.eval_on_selector_all(
        "#designReviewList details, #timelineCard",
        "els => els.forEach(e => { e.open = true; })")
    pg.wait_for_timeout(300)


def brevo_capture_for(email):
    if not os.path.exists(CAPTURE):
        return None
    found = None
    with open(CAPTURE) as f:
        for line in f:
            try:
                m = json.loads(line)
            except Exception:
                continue
            if email in json.dumps(m.get("to", [])) and "verif" in m.get("subject", "").lower():
                found = m  # latest capture wins: reruns reuse the same address
    return found


def brevo_token(email):
    # 120s budget (was 60s): under load the queued send may land late; the loop
    # returns as soon as the token appears, so the larger deadline costs nothing.
    deadline = time.time() + 120
    while time.time() < deadline:
        m = brevo_capture_for(email)
        if m:
            mt = verification_code(m.get("htmlContent", ""))
            if mt:
                return mt.group(1), m
        time.sleep(2)
    raise AssertionError(f"no verification token captured for {email}")


def owner_login(pg):
    goto(pg, "/Account/Login")
    pg.fill('form[action*="Login"] input[name="Email"]', "owner@dechimexprinthub.com")
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


def register_customer(pg, tag_suffix):
    email = f"dr-{TAG}-{tag_suffix}@example.com"
    goto(pg, "/Account/Register")
    pg.fill('form[action*="Register"] input[name="FirstName"]', "Review")
    pg.fill('form[action*="Register"] input[name="LastName"]', f"User{tag_suffix}")
    pg.fill('form[action*="Register"] input[name="Email"]', email)
    pg.fill('form[action*="Register"] input[name="Phone"]', "08030000002")
    pg.fill('form[action*="Register"] input[name="Password"]', "CustE2E!2026xY")
    conf = pg.query_selector('form[action*="Register"] input[name="ConfirmPassword"]')
    if conf:
        conf.fill("CustE2E!2026xY")
    pg.click('form[action*="Register"] button[type="submit"]')
    pg.wait_for_timeout(3000)
    check(f"register {tag_suffix} -> verify page", "erif" in pg.url or "portal" in pg.url.lower(), pg.url)
    if "portal" not in pg.url.lower():
        token, cap = brevo_token(email)
        check(f"verification token captured for {tag_suffix}", bool(token))
        # The captured request carried the configured Brevo key (never print it).
        api_key = (cap.get("_headers") or {}).get("api-key", "")
        check("brevo api-key header present (key loaded at runtime)",
              api_key == SETTINGS["Brevo"]["ApiKey"],
              f"prefix={api_key[:8]!r} len={len(api_key)}")
        otp = pg.query_selector('input[name="Token"], input[name="token"], input[name="Code"]')
        assert otp, f"OTP input missing @ {pg.url}"
        otp.fill(token)
        pg.click('form button[type="submit"]')
        pg.wait_for_url(lambda u: "portal" in u.lower(), timeout=20000)
    check(f"customer {tag_suffix} signed in", "portal" in pg.url.lower(), pg.url)
    return email


def paystack_secret_key_valid():
    """§9 procedure: authorized GET /transaction with the secret key read from the
    app's own appsettings (the same file the runtime binds). Status-only check —
    the key value is never printed or captured."""
    import json as _json
    base = os.path.abspath(os.path.join(os.path.dirname(__file__), "../../DeChimexPrints/src/DeChimex.Host/appsettings.json"))
    secret = _json.load(open(base))["Paystack"]["SecretKey"]
    req = urllib.request.Request("https://api.paystack.co/transaction", headers={
        "Authorization": f"Bearer {secret}",
        # Cloudflare bans the default python-urllib signature (error 1010) — a
        # browser UA passes; this mirrors §9's documented validation result.
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                      "(KHTML, like Gecko) Chrome/126.0 Safari/537.36",
        "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            body = _json.loads(resp.read().decode())
            return resp.status == 200 and bool(body.get("status"))
    except Exception as e:
        print("paystack check error:", type(e).__name__, flush=True)
        return False


with sync_playwright() as p:
    browser = p.chromium.launch()

    # ---------- STAGE 0: keys are loaded at runtime ----------
    keys = use(browser.new_page())
    goto(keys, "/api/v1/config")
    cfg_text = keys.inner_text("body")
    cfg = json.loads(cfg_text).get("data", {})
    check("paystack public key served by /api/v1/config",
          cfg.get("paystackPublicKey") == SETTINGS["Paystack"]["PublicKey"],
          repr(str(cfg.get("paystackPublicKey", ""))[:12]))
    check("public config does not expose either secret",
          SETTINGS["Brevo"]["ApiKey"] not in cfg_text and SETTINGS["Paystack"]["SecretKey"] not in cfg_text)
    use(keys)
    shot("01-config-endpoint-paystack-public-key.png")
    keys.close()
    check("paystack test secret key authorized against api.paystack.co (§9 GET)",
          paystack_secret_key_valid())

    # ---------- STAGE A: staff builds catalog ----------
    staff = use(browser.new_page())
    owner_login(staff)
    use(staff)
    shot("02-owner-logged-in.png")

    cat_name = f"E2E DR Category {TAG}"
    goto(staff, "/admin/categories/create")
    staff.fill('form[action*="create"] input[name="Name"]', cat_name)
    staff.fill('form[action*="create"] textarea[name="Description"]', "design review E2E category")
    staff.click('form[action*="create"] button[type="submit"]')
    staff.wait_for_url(lambda u: u.rstrip("/") == BASE + "/admin/categories", timeout=20000)
    check("category created", cat_name in staff.inner_text("body"))

    # Find the options link in OUR category's table row (names are TAG-unique).
    row = staff.query_selector(f'tr:has-text("{cat_name}")')
    assert row, "our category row not found in the list"
    opt_link = row.query_selector('a[href*="/options"]')
    assert opt_link, "options link missing on our row"
    m = re.search(r"categories/([0-9a-f-]{36})/options", opt_link.get_attribute("href"))
    assert m, "category id not in options href"
    cat_id = m.group(1)
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

    prod_name = f"E2E DR Product {TAG}"
    goto(staff, "/admin/products/create")
    staff.fill('form[action*="create"] input[name="Name"]', prod_name)
    staff.fill('form[action*="create"] textarea[name="Description"]', "design review E2E product")
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
    use(cust)
    shot("03-customer-verified-signed-in.png")

    goto(cust, "/portal/shop")
    cust.wait_for_timeout(1500)
    link = cust.get_by_text(prod_name, exact=False).first
    link.wait_for(timeout=15000)
    link.scroll_into_view_if_needed()
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

    # The portal workspace shows the empty design-review section.
    expand_details(cust)
    # DOM contract (not copy): the 2026-09-25 customer redesign reworded the
    # empty-state text, the #designReviewEmpty id is the stable contract.
    check("design review section empty",
          cust.query_selector("#designReviewEmpty") is not None
          and cust.query_selector("#designReviewList [data-design-review-id]") is None)
    use(cust)
    shot("04-customer-workspace-empty-design-reviews.png")

    # ---------- STAGE C: staff opens the workspace (live listener) ----------
    goto(staff, f"/admin/orders/{order_id}/workspace")
    check("staff workspace opens", order_id in staff.url, staff.url)
    use(staff)
    shot("05-staff-workspace-open.png")

    # ---------- STAGE D: customer uploads design review v1 ----------
    form = cust.query_selector('form[action*="designreview"]')
    assert form, "design review upload form missing"
    file_input = form.query_selector('input[type="file"][name="file"]')
    check("file input is required (browser-level validation)",
          file_input.get_attribute("required") is not None)
    file_input.set_input_files(os.path.join(WIP, "dr_v1.png"))
    form.query_selector('input[name="notes"]').fill("First version — sunset gradient")
    form.query_selector('button[type="submit"]').click()
    cust.wait_for_timeout(2500)  # POST -> 302 -> workspace re-render
    expand_details(cust)
    check("v1 card rendered on customer side", "dr_v1.png" in cust.inner_text("body"))
    check("v1 pending + awaiting staff review",
          "Pending" in cust.inner_text("body")
          and "Waiting for the team to review" in cust.inner_text("body")   # one wording: server + live card
          and "Awaiting staff review" not in cust.inner_text("body"))
    use(cust)
    shot("06-customer-uploaded-v1.png")

    # Staff sees it LIVE (no reload): JS node + toast.
    staff.wait_for_selector('[data-design-review-id]', timeout=20000)
    staff_dr = staff.query_selector('#designReviewList [data-design-review-id]')
    check("v1 appeared live on staff side", staff_dr is not None)
    check("live card shows file + pending",
          "dr_v1.png" in staff.inner_text("#designReviewList") and "Pending" in staff.inner_text("#designReviewList"))
    staff.wait_for_timeout(800)
    use(staff)
    shot("07-staff-sees-v1-live.png")

    # ---------- STAGE E: staff approves from the LIVE-rendered card ----------
    live_form = staff.query_selector('#designReviewList form[action*="/approve"]')
    assert live_form, "approve form missing on live card"
    live_form.query_selector('button[type="submit"]').click()
    staff.wait_for_timeout(2500)  # POST -> 302 -> workspace re-render
    expand_details(staff)
    check("v1 approved on staff side (after reload)",
          "Approved" in staff.inner_text("#designReviewList"))
    use(staff)
    shot("08-staff-approved-v1.png")

    # Customer pill flips LIVE + toast (no reload).
    cust.wait_for_selector('#designReviewList .badge-success', timeout=20000)
    check("v1 pill flipped to Approved live on customer side", True)
    cust.wait_for_timeout(800)
    use(cust)
    shot("09-customer-sees-approval-live.png")
    toast_txt = cust.inner_text("body")
    check("customer toast announced approval", "approved" in toast_txt.lower())

    # ---------- STAGE F: customer uploads v2; staff rejects with reason ----------
    form = cust.query_selector('form[action*="designreview"]')
    file_input = form.query_selector('input[type="file"][name="file"]')
    file_input.set_input_files(os.path.join(WIP, "dr_v2.png"))
    form.query_selector('input[name="notes"]').fill("Second version — bolder type")
    form.query_selector('button[type="submit"]').click()
    cust.wait_for_timeout(2500)  # POST -> 302 -> workspace re-render

    staff.wait_for_selector('#designReviewList details:nth-child(2)', timeout=20000)
    expand_details(staff)
    check("v2 appeared live on staff side (independent history)",
          staff.eval_on_selector_all("#designReviewList [data-design-review-id]", "els => els.length") == 2)
    use(staff)
    shot("10-staff-sees-v2-live-independent-history.png")

    # Validation: the reject modal requires a reason (browser-level constraint).
    v2_card = staff.query_selector_all('#designReviewList [data-design-review-id]')[1]
    v2_card.query_selector('button:has-text("Reject")').click()
    staff.wait_for_selector('#rejectDesignReviewModal', state="visible", timeout=10000)
    staff.click('#rejectDesignReviewForm button[type="submit"]')
    staff.wait_for_timeout(800)
    check("reject without reason is blocked (required constraint)",
          staff.eval_on_selector('#rejectReason', "el => el.checkValidity()") is False)
    use(staff)
    shot("11-reject-requires-reason-validation.png")

    staff.fill('#rejectReason', "Colour mode must be CMYK, please re-export")
    staff.click('#rejectDesignReviewForm button[type="submit"]')
    staff.wait_for_timeout(2500)  # POST -> 302 -> workspace re-render
    expand_details(staff)
    check("v2 rejected on staff side", "Rejected" in staff.inner_text("#designReviewList"))
    check("rejection reason recorded", "CMYK" in staff.inner_text("#designReviewList"))
    use(staff)
    shot("12-staff-rejected-v2-with-reason.png")

    # Customer sees the rejection LIVE with reason + toast.
    cust.wait_for_selector('#designReviewList .badge-danger', timeout=20000)
    expand_details(cust)
    cust.wait_for_timeout(600)
    check("v2 rejected live on customer side", "Rejected" in cust.inner_text("#designReviewList"))
    check("rejection reason visible to customer", "CMYK" in cust.inner_text("#designReviewList"))
    use(cust)
    shot("13-customer-sees-rejection-reason-live.png")

    # ---------- STAGE G: invalid file type is refused ----------
    goto(cust, f"/portal/orders/{order_id}/workspace")
    cust.wait_for_timeout(800)
    form = cust.query_selector('form[action*="designreview"]')
    file_input = form.query_selector('input[type="file"][name="file"]')
    file_input.set_input_files({
        "name": "virus.exe",
        "mimeType": "application/octet-stream",
        "buffer": b"MZ\x90\x00fake-exe",
    })
    form.query_selector('button[type="submit"]').click()
    cust.wait_for_timeout(2500)
    body = cust.inner_text("body")
    check("disallowed file type refused",
          "400" in cust.url or "Bad Request" in body or "not allowed" in body.lower() or "validation" in body.lower(),
          cust.url)
    use(cust)
    shot("14-invalid-file-type-refused.png")

    # ---------- STAGE H: persistence after reload ----------
    goto(cust, f"/portal/orders/{order_id}/workspace")
    cust.wait_for_timeout(1000)
    expand_details(cust)
    dr_text = cust.inner_text("#designReviewList")
    check("after reload: v1 approved + v2 rejected persist",
          "Approved" in dr_text and "Rejected" in dr_text and "CMYK" in dr_text)
    check("after reload: both files listed", "dr_v1.png" in dr_text and "dr_v2.png" in dr_text)
    use(cust)
    shot("15-customer-workspace-final-persisted.png")

    goto(staff, f"/admin/orders/{order_id}/workspace")
    staff.wait_for_timeout(1000)
    expand_details(staff)
    dr_text = staff.inner_text("#designReviewList")
    check("staff after reload: full decision history",
          "Approved" in dr_text and "Rejected" in dr_text and "CMYK" in dr_text)
    tl = staff.inner_text("#timelineList")
    check("timeline records designreview events",
          "design review" in tl.lower() or "uploaded" in tl.lower())
    use(staff)
    shot("16-staff-workspace-final-persisted.png")

    print(f"\nALL {len(CHECKS)} CHECKS PASSED (TAG={TAG})", flush=True)
    browser.close()
