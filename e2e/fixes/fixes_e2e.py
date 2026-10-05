"""Fixes E2E — notification audience, error-page home links, design-review live cards.

Real browsers against the running app (in-memory DB, Brevo + Paystack stubs):
  A. staff builds a customizable product (category + required question)
  B. customer registers/verifies; LIVE hub listeners are attached for the customer
     and the owner (their own SignalR connections record every "notification" push)
  C. design review v1: customer uploads in tab A while tab B is open
       - tab B gets the LIVE card, tab A the server card: ONE wording
       - staff approves -> the customer's SERVER-rendered card drops the waiting line live
  D. design review v2: a second staff tab loses stale Approve/Reject live after tab 1
     rejects (reason required); customer sees "Changes requested:" live and after reload
  E. notification audience: live pushes + bell feed + admin list
  F. error pages: customer "My account", staff "Admin dashboard", developer "Developer Portal"
Screenshots: E2E_SHOTS_DIR/NN-name.png (auto-numbered; SHOT_START sets the first number).
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_src = open(os.path.join(HERE, "../design-review/ws_designreview_e2e.py")).read()
exec(compile(_src.split("with sync_playwright() as p:")[0], "ws_designreview_helpers", "exec"))  # helpers only

SHOTS = os.environ.get("E2E_SHOTS_DIR", os.path.abspath(os.path.join(HERE, "../../evidence")))
_n = [int(os.environ.get("SHOT_START", "1")) - 1]


def shot(name):  # noqa: F811 — auto-numbered variant of the helper
    _n[0] += 1
    os.makedirs(SHOTS, exist_ok=True)
    fn = f"{_n[0]:02d}-{name}.png"
    CURRENT[0].screenshot(path=os.path.join(SHOTS, fn), full_page=True)
    print("shot", fn, flush=True)


LISTEN_JS = """async () => {
  window.__rx = [];
  const c = new signalR.HubConnectionBuilder().withUrl('/hubs/notifications').build();
  c.on('notification', n => window.__rx.push(n.title));
  await c.start();
  window.__hub = c;
  return c.state;
}"""


def attach_listener(pg, path):
    goto(pg, path)
    pg.wait_for_function("() => !!window.signalR", timeout=15000)
    return pg.evaluate(LISTEN_JS)


def received(pg):
    return pg.evaluate("() => window.__rx.slice()")


def wait_rx(pg, title, timeout=20000):
    pg.wait_for_function("t => window.__rx.includes(t)", arg=title, timeout=timeout)


def dr_cards(pg):
    return pg.query_selector_all("#designReviewList [data-design-review-id]")


def card(pg, file_name):
    """The design-review card for a file (server renders newest first, live cards append)."""
    for c in dr_cards(pg):
        if file_name in c.inner_text():
            return c
    raise AssertionError(f"no design-review card for {file_name}")


V2_JS = """() => { const c = [...document.querySelectorAll('#designReviewList [data-design-review-id]')]
    .find(e => e.textContent.includes('dr_v2.png')); return c && %s; }"""


def home_link(pg):
    el = pg.query_selector('[data-testid="error-home-link"]') or pg.query_selector(".links a.ghost")
    return (el.get_attribute("href"), el.inner_text().strip()) if el else (None, None)


def login(pg, email, password):
    goto(pg, "/Account/Login")
    pg.fill('form[action*="Login"] input[name="Email"]', email)
    pg.fill('form[action*="Login"] input[name="Password"]', password)
    pg.click('form[action*="Login"] button[type="submit"]')
    pg.wait_for_timeout(2500)


STAFF_TITLES = ("New order request", "Design review uploaded")
CUSTOMER_TITLES = ("Design review approved", "Design review rejected")

with sync_playwright() as p:
    browser = p.chromium.launch()

    # ---------- A. staff builds catalog ----------
    staff_ctx = browser.new_context(viewport={"width": 1366, "height": 900})
    staff = use(staff_ctx.new_page())
    owner_login(staff)
    cat_name = f"E2E FX Category {TAG}"
    goto(staff, "/admin/categories/create")
    staff.fill('form[action*="create"] input[name="Name"]', cat_name)
    staff.fill('form[action*="create"] textarea[name="Description"]', "fixes E2E category")
    staff.click('form[action*="create"] button[type="submit"]')
    staff.wait_for_url(lambda u: u.rstrip("/") == BASE + "/admin/categories", timeout=20000)
    row = staff.query_selector(f'tr:has-text("{cat_name}")')
    cat_id = re.search(r"categories/([0-9a-f-]{36})/options",
                       row.query_selector('a[href*="/options"]').get_attribute("href")).group(1)
    goto(staff, f"/admin/categories/{cat_id}/options")
    staff.wait_for_selector(".opt-row", timeout=10000)
    staff.fill(".opt-row .opt-question", "Slogan or design?")
    staff.select_option(".opt-row .opt-type", "Text")
    req = staff.query_selector(".opt-row .opt-required")
    if req and not req.is_checked():
        req.check()
    staff.click('#saveForm button[type="submit"]')
    staff.wait_for_timeout(2500)
    prod_name = f"E2E FX Product {TAG}"
    goto(staff, "/admin/products/create")
    staff.fill('form[action*="create"] input[name="Name"]', prod_name)
    staff.fill('form[action*="create"] textarea[name="Description"]', "fixes E2E product")
    staff.select_option('form[action*="create"] select[name="CategoryId"]', cat_id)
    staff.fill('form[action*="create"] input[name="SellingPrice"]', "15000")
    staff.fill('form[action*="create"] input[name="InitialQuantity"]', "50")
    c = staff.query_selector('form[action*="create"] input[name="IsCustomizable"]')
    if c and not c.is_checked():
        c.check()
    staff.set_input_files('form[action*="create"] input[name="Images"]', os.path.join(WIP, "product.png"))
    staff.click('form[action*="create"] button[type="submit"]')
    staff.wait_for_url(lambda u: "/admin/products" in u and "create" not in u, timeout=20000)
    check("customizable product created", prod_name in staff.inner_text("body"))

    # ---------- B. customer + live hub listeners ----------
    cust_ctx = browser.new_context(viewport={"width": 1366, "height": 900})
    cust = use(cust_ctx.new_page())
    register_customer(cust, "fx")
    cust_listener = cust_ctx.new_page()
    staff_listener = staff_ctx.new_page()
    check("customer live hub listener connected", attach_listener(cust_listener, "/portal") == "Connected")
    check("owner live hub listener connected", attach_listener(staff_listener, "/admin/dashboard") == "Connected")

    goto(cust, "/portal/shop")
    cust.wait_for_timeout(1200)
    link = cust.get_by_text(prod_name, exact=False).first
    link.wait_for(timeout=15000)
    with cust.expect_navigation(wait_until="domcontentloaded", timeout=20000):
        link.click()
    if not re.search(r"/order(\?|$)", cust.url):
        ol = cust.query_selector('a[href$="/order"], a[href*="/shop/"][href*="/order"]')
        with cust.expect_navigation(wait_until="domcontentloaded", timeout=20000):
            ol.click()
    cust.query_selector('input[name^="opt_"], textarea[name^="opt_"]').fill("Logo on the back")
    cust.click('form[action*="/order"] button[type="submit"]')
    cust.wait_for_url(lambda u: "/portal/orders" in u, timeout=20000)
    m = re.search(r"/portal/orders/([0-9a-f-]{36})/workspace", cust.url)
    if not m:
        ws = cust.query_selector('a[href*="/workspace"]')
        m = re.search(r"/portal/orders/([0-9a-f-]{36})/workspace", ws.get_attribute("href"))
    order_id = m.group(1)
    ws_path = f"/portal/orders/{order_id}/workspace"
    check("custom order request submitted", bool(order_id))
    wait_rx(staff_listener, "New order request")
    check("staff still receive staff notifications LIVE (New order request)", True)

    # ---------- C. design review v1: live card vs server card ----------
    goto(cust, ws_path)
    cust_b = use(cust_ctx.new_page())       # second customer tab, left open
    goto(cust_b, ws_path)
    goto(staff, f"/admin/orders/{order_id}/workspace")

    form = cust.query_selector('form[action*="designreview"]')
    form.query_selector('input[type="file"][name="file"]').set_input_files(os.path.join(WIP, "dr_v1.png"))
    form.query_selector('input[name="notes"]').fill("First version")
    form.query_selector('button[type="submit"]').click()
    cust.wait_for_timeout(2500)
    expand_details(cust)
    srv = cust.inner_text("#designReviewList")
    check("server card: 'Waiting for the team to review'", "Waiting for the team to review" in srv, srv[:200])
    check("server waiting line carries .design-review-waiting",
          cust.query_selector("#designReviewList .design-review-waiting") is not None)
    use(cust)
    shot("customer-design-review-waiting-server-card")

    cust_b.wait_for_selector("#designReviewList [data-design-review-id]", timeout=20000)
    expand_details(cust_b)
    live = cust_b.inner_text("#designReviewList")
    check("live card (other tab): same wording", "Waiting for the team to review" in live, live[:200])
    check("old live wording 'Awaiting staff review' is gone", "Awaiting staff review" not in live)
    use(cust_b)
    shot("customer-second-tab-live-card-same-wording")

    staff.wait_for_selector('#designReviewList form[action*="/approve"]', timeout=20000)
    staff.query_selector('#designReviewList form[action*="/approve"] button[type="submit"]').click()
    staff.wait_for_timeout(2500)
    cust.wait_for_selector("#designReviewList .badge-success", timeout=20000)
    cust.wait_for_timeout(500)
    check("approval flips the customer's SERVER card live", "Approved" in cust.inner_text("#designReviewList"))
    check("waiting line removed live from the server-rendered card",
          cust.query_selector("#designReviewList .design-review-waiting") is None
          and "Waiting for the team to review" not in cust.inner_text("#designReviewList"))
    use(cust)
    shot("customer-server-card-approved-live-waiting-line-removed")
    wait_rx(cust_listener, "Design review approved")
    check("customer receives their own notification LIVE", True)

    # ---------- D. v2: stale staff buttons removed live; label per audience ----------
    form = cust.query_selector('form[action*="designreview"]')
    form.query_selector('input[type="file"][name="file"]').set_input_files(os.path.join(WIP, "dr_v2.png"))
    form.query_selector('input[name="notes"]').fill("Second version")
    form.query_selector('button[type="submit"]').click()
    cust.wait_for_timeout(2500)

    staff2 = use(staff_ctx.new_page())
    goto(staff2, f"/admin/orders/{order_id}/workspace")
    goto(staff, f"/admin/orders/{order_id}/workspace")
    expand_details(staff2)
    v2_on_2 = card(staff2, "dr_v2.png")
    check("staff tab 2: server-rendered pending v2 has Approve/Reject (.design-review-actions)",
          v2_on_2.query_selector(".design-review-actions") is not None)
    shot("staff-second-tab-pending-v2-server-card-with-actions")

    use(staff)
    expand_details(staff)
    card(staff, "dr_v2.png").query_selector('button:has-text("Reject")').click()
    staff.wait_for_selector("#rejectDesignReviewModal", state="visible", timeout=10000)
    staff.click('#rejectDesignReviewForm button[type="submit"]')
    staff.wait_for_timeout(600)
    check("validation: reject requires a reason",
          staff.eval_on_selector("#rejectReason", "el => el.checkValidity()") is False)
    shot("reject-requires-reason-validation")
    staff.fill("#rejectReason", "Colour mode must be CMYK")
    staff.click('#rejectDesignReviewForm button[type="submit"]')
    staff.wait_for_timeout(2500)

    staff2.wait_for_function(V2_JS % "!c.querySelector('.design-review-actions')", timeout=20000)
    v2_on_2 = card(staff2, "dr_v2.png")
    check("staff tab 2: stale Approve/Reject removed live after the rejection",
          v2_on_2.query_selector(".design-review-actions") is None and "Rejected" in v2_on_2.inner_text())
    check("staff tab 2: no duplicate reason line", len(v2_on_2.query_selector_all(".design-review-reason")) <= 1)
    use(staff2)
    shot("staff-second-tab-actions-removed-live")

    cust_b.wait_for_function(V2_JS % "c.querySelector('.badge-danger')", timeout=20000)
    expand_details(cust_b)
    live2 = card(cust_b, "dr_v2.png").inner_text()
    check("customer live card: 'Changes requested:' + reason", "Changes requested:" in live2 and "CMYK" in live2, live2[:200])
    goto(cust, ws_path)
    expand_details(cust)
    srv2 = card(cust, "dr_v2.png").inner_text()
    check("customer server card after reload: same 'Changes requested:' label",
          "Changes requested:" in srv2 and "CMYK" in srv2 and "Rejected:" not in srv2, srv2[:200])
    use(cust)
    shot("customer-rejection-same-label-after-reload")
    wait_rx(cust_listener, "Design review rejected")

    # ---------- E. notification audience ----------
    wait_rx(staff_listener, "Design review uploaded")
    cust_rx, staff_rx = received(cust_listener), received(staff_listener)
    print("customer live pushes:", cust_rx, "| staff live pushes:", staff_rx, flush=True)
    check("customer live pushes contain NO staff notifications", not any(t in cust_rx for t in STAFF_TITLES), cust_rx)
    check("customer live pushes = only their own", set(cust_rx) <= set(CUSTOMER_TITLES) and len(cust_rx) >= 2, cust_rx)
    check("staff live pushes include the staff notifications", all(t in staff_rx for t in STAFF_TITLES), staff_rx)

    feed = cust.evaluate("() => fetch('/portal/notifications/recent').then(r => r.json())")
    feed_titles = [f["title"] for f in feed]
    check("customer bell feed: only own notifications",
          set(feed_titles) <= set(CUSTOMER_TITLES) and not any(t in feed_titles for t in STAFF_TITLES), feed_titles)
    goto(cust, "/portal")
    cust.click("#portalNotifBell")
    cust.wait_for_selector("#portalNotifDrop .portal-notif-list", state="visible", timeout=10000)
    cust.wait_for_timeout(800)
    drop = cust.inner_text("#portalNotifDrop")
    check("bell dropdown shows the customer's notifications, none of staff",
          "Design review" in drop and not any(t in drop for t in STAFF_TITLES), drop[:300])
    use(cust)
    shot("customer-bell-only-own-notifications")

    goto(staff, "/admin/notifications")
    body = staff.inner_text("main") if staff.query_selector("main") else staff.inner_text("body")
    check("staff notifications page still lists staff notifications", all(t in body for t in STAFF_TITLES), body[:300])
    use(staff)
    shot("staff-notifications-list-unchanged")

    # ---------- F. error pages ----------
    goto(cust, ws_path)
    form = cust.query_selector('form[action*="designreview"]')
    form.query_selector('input[type="file"][name="file"]').set_input_files(
        {"name": "virus.exe", "mimeType": "application/octet-stream", "buffer": b"MZ\x90\x00fake"})
    form.query_selector('button[type="submit"]').click()
    cust.wait_for_timeout(2500)
    txt = cust.inner_text("body")
    href, label = home_link(cust)
    check("customer 400 (invalid upload) page reached", "400" in txt, cust.url)
    check("customer 400 page links to 'My account' (/portal)", href == "/portal" and label == "My account", (href, label))
    check("customer 400 page has no 'Admin dashboard'", "Admin dashboard" not in txt and "/admin/dashboard" not in cust.content())
    use(cust)
    shot("customer-400-error-page-my-account-link")

    goto(cust, f"/no-such-page-{TAG}")
    txt = cust.inner_text("body")
    href, label = home_link(cust)
    check("customer 404 page links to 'My account'", "404" in txt and href == "/portal" and label == "My account", (href, label))
    check("customer 404 page has no 'Admin dashboard'", "Admin dashboard" not in txt)
    shot("customer-404-error-page-my-account-link")

    goto(staff, f"/no-such-page-{TAG}")
    href, label = home_link(staff)
    check("staff 404 page still links to 'Admin dashboard'", href == "/admin/dashboard" and label == "Admin dashboard", (href, label))
    use(staff)
    shot("staff-404-error-page-admin-dashboard-link")

    dev_ctx = browser.new_context(viewport={"width": 1366, "height": 900})
    dev = use(dev_ctx.new_page())
    login(dev, "developer@dechimexprinthub.local", "DevPortal#2026!")
    check("developer signed in (lands in /dev)", "/dev" in dev.url, dev.url)
    goto(dev, f"/no-such-page-{TAG}")
    txt = dev.inner_text("body")
    href, label = home_link(dev)
    check("developer 404 page links to 'Developer Portal' (/dev)", href == "/dev" and label == "Developer Portal", (href, label))
    check("developer 404 page has no 'Admin dashboard'", "Admin dashboard" not in txt)
    shot("developer-404-error-page-developer-portal-link")

    anon = use(browser.new_page())
    goto(anon, f"/no-such-page-{TAG}")
    check("anonymous 404 page shows no account link", home_link(anon)[0] is None and "Admin dashboard" not in anon.inner_text("body"))

    print(f"\nRESULT: {len(CHECKS)} passed, 0 failed (TAG={TAG})", flush=True)
    browser.close()
