"""Developer Portal sign-in + dropped-request E2E (Visual Studio "OperationCanceledException" report).

The owner reported that signing in to the Developer Portal locally stopped the Visual Studio
debugger with "Exception User-Unhandled — System.OperationCanceledException" on the dashboard's
service-fee query (ServiceFeeReportService.OverviewAsync). That happens whenever the browser drops
a /dev request that is still running (second click on Sign in, reload, navigating away while the
dashboard loads, the 30 s status poll in flight when the page changes). The fix catches the
client-abort cancellation inside the dev controllers (quiet 499, no error page, no status-code
re-execution). While verifying it, a second bug surfaced and was fixed: status-code re-execution
re-ran the ORIGINAL action, so an admin action returning NotFound() showed a blank 404.

Real browsers + raw sockets against the running app (in-memory DB, Brevo + Paystack stubs):
  A. developer sign-in validation: empty fields, wrong password (error shown, stays on login)
  B. correct sign-in lands on the rendered dashboard
  C. "double-click Sign in": a second submit while the first /dev load is in flight -> dashboard
  D. abort storm: /dev, /dev/api/status, /dev/service-fees and 5 more dev pages are requested and
     the socket is RESET mid-request (like a closed tab) at several delays. The app log must show
     the aborts landed mid-request (499s on the dev paths themselves) and ZERO OperationCanceled
     exceptions reaching the global handler, ZERO /Error?code=499 re-executions, ZERO unhandled.
  E. dashboard poll aborted by the browser (AbortController) -> same quiet result
  F. after all of that: /health Healthy, dashboard + status JSON + service fees render normally
  G. blank-404/400 fix: owner opens a missing customer (details + edit) -> styled 404 with the
     "Admin dashboard" link, the action ran ONCE (not re-executed); a form posted without its
     anti-forgery token -> styled 400 (not blank, not 405), nothing created; unknown URL 404 unchanged
Env: APP_LOG (the app's stdout log), E2E_SHOTS_DIR. Screenshots auto-numbered NN-name.png.
"""
import json
import os
import re
import socket
import struct
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
_src = open(os.path.join(HERE, "../design-review/ws_designreview_e2e.py")).read()
exec(compile(_src.split("with sync_playwright() as p:")[0], "ws_designreview_helpers", "exec"))  # helpers only

SHOTS = os.environ.get("E2E_SHOTS_DIR", os.path.abspath(os.path.join(HERE, "../../evidence")))
APP_LOG = os.environ.get("APP_LOG", "/tmp/fx-app.log")
DEV_EMAIL, DEV_PASSWORD = "developer@dechimexprinthub.local", "DevPortal#2026!"
_n = [0]

DEV_PATHS = ["/dev", "/dev/api/status", "/dev/service-fees", "/dev/health", "/dev/performance",
             "/dev/logs", "/dev/payments", "/dev/service-fees/ledger"]


def shot(name):  # noqa: F811 — auto-numbered variant
    _n[0] += 1
    os.makedirs(SHOTS, exist_ok=True)
    fn = f"{_n[0]:02d}-{name}.png"
    CURRENT[0].screenshot(path=os.path.join(SHOTS, fn), full_page=True)
    print("shot", fn, flush=True)


def log_mark():
    return os.path.getsize(APP_LOG)


def log_since(mark):
    time.sleep(1.5)  # let Serilog flush
    with open(APP_LOG, "rb") as f:
        f.seek(mark)
        return f.read().decode("utf-8", "replace")


def login_form(pg, email, password, novalidate=False):
    goto(pg, "/Account/Login")
    if novalidate:  # bypass the browser's own `required` check to prove the SERVER validates too
        pg.evaluate("() => document.querySelectorAll('form').forEach(f => f.noValidate = true)")
    pg.fill('form[action*="Login"] input[name="Email"]', email)
    pg.fill('form[action*="Login"] input[name="Password"]', password)


def submit(pg):
    pg.click('form[action*="Login"] button[type="submit"]')


def reset_abort(path, cookie, delay):
    """Send a real HTTP request, wait `delay`, then RST the socket (what a closed tab does)."""
    s = socket.create_connection(("127.0.0.1", 5119))
    s.sendall((f"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:5119\r\nAccept: text/html,application/json\r\n"
               f"Cookie: {cookie}\r\n\r\n").encode())
    time.sleep(delay)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
    s.close()


ABORT_RE = r"Request finished HTTP/1\.1 GET http://127\.0\.0\.1:5119(/dev[^ ]*) - 499"


def burst_abort(paths, cookie, delay):
    """Open one socket per path, send every request, wait `delay`, then RST them all at once."""
    socks = []
    for path in paths:
        s = socket.create_connection(("127.0.0.1", 5119))
        socks.append((s, path))
    for s, path in socks:
        s.sendall((f"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:5119\r\nAccept: text/html,application/json\r\n"
                   f"Cookie: {cookie}\r\n\r\n").encode())
    time.sleep(delay)
    for s, _ in socks:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
        s.close()


def quiet(text, paths_re=r"/dev"):
    """Counts of the noisy outcomes the fix removes (all must be zero) in a log slice."""
    return {
        "oce_to_global_handler": len(re.findall(rf"Handled OperationCanceledException on GET {paths_re}", text)),
        "oce_stack_traces": text.count("System.OperationCanceledException"),
        "reexecuted_499": text.count("/Error?code=499"),
        "unhandled": text.count("Unhandled exception"),
    }


with sync_playwright() as p:
    browser = p.chromium.launch()
    ctx = browser.new_context(viewport={"width": 1366, "height": 900})
    pg = use(ctx.new_page())

    # ---------------- A. sign-in validation ----------------
    goto(pg, "/Account/Login")
    check("login page renders the sign-in form", pg.locator('form[action*="Login"] input[name="Email"]').count() == 1)
    shot("developer-sign-in-page")

    login_form(pg, "", "", novalidate=True)
    submit(pg)
    pg.wait_for_load_state("domcontentloaded"); pg.wait_for_timeout(600)
    body = pg.inner_text("main") if pg.locator("main").count() else pg.inner_text("body")
    check("empty email + password: server refuses and stays on the login page",
          "/Account/Login" in pg.url and re.search(r"required|enter", body, re.I) is not None, pg.url)
    shot("sign-in-empty-fields-validation")

    login_form(pg, DEV_EMAIL, "WrongPassword!1")
    submit(pg)
    pg.wait_for_load_state("domcontentloaded"); pg.wait_for_timeout(600)
    check("wrong password: 'Invalid email or password', still on login, no portal access",
          "/Account/Login" in pg.url and "Invalid email or password" in pg.inner_text("body"), pg.url)
    shot("sign-in-wrong-password-error")
    r = pg.request.get(BASE + "/dev", max_redirects=0)
    check("after the failed attempt /dev still requires sign-in", r.status == 302 and "/Account/Login" in r.headers.get("location", ""))

    # ---------------- B. correct sign-in ----------------
    mark = log_mark()
    login_form(pg, DEV_EMAIL, DEV_PASSWORD)
    submit(pg)
    pg.wait_for_url(re.compile(r"/dev$"), timeout=20000)
    pg.wait_for_selector("[data-dv-tiles]", timeout=15000)
    check("correct credentials land on the Developer Portal dashboard", pg.url.endswith("/dev"))
    check("dashboard tiles rendered", pg.locator("[data-dv-tiles] [data-tile]").count() >= 5,
          pg.locator("[data-dv-tiles] [data-tile]").count())
    check("service-fee figures (today / this month) rendered on the dashboard",
          re.search(r"service fee", pg.inner_text("main"), re.I) is not None)
    q = quiet(log_since(mark))
    check("normal sign-in: no cancellation noise in the app log", q == {k: 0 for k in q}, q)
    shot("dashboard-after-sign-in")
    cookie = "; ".join(f"{c['name']}={c['value']}" for c in ctx.cookies())

    # ---------------- C. double-click Sign in ----------------
    ctx2 = browser.new_context(viewport={"width": 1366, "height": 900})
    pg2 = use(ctx2.new_page())
    mark = log_mark()
    login_form(pg2, DEV_EMAIL, DEV_PASSWORD)
    submit(pg2)
    try:
        submit(pg2)  # the second click while the first navigation is still running
    except Exception:
        pass  # the form may already be gone — that is the "fast" path, also fine
    pg2.goto(BASE + "/dev", wait_until="commit")  # and a reload on top, cancelling whatever is in flight
    pg2.wait_for_selector("[data-dv-tiles]", timeout=20000)
    check("double-submit + reload during sign-in still ends on the rendered dashboard", pg2.url.endswith("/dev"))
    q = quiet(log_since(mark))
    check("double-submit: no OperationCanceledException reaches the global handler / logs", q == {k: 0 for k in q}, q)
    shot("dashboard-after-double-submit-and-reload")
    ctx2.close()
    use(pg)

    # ---------------- D. abort storm ----------------
    mark = log_mark()
    fired = 0
    for rnd in range(3):
        for delay in (0.0, 0.003, 0.008, 0.015, 0.03):
            for path in DEV_PATHS:
                reset_abort(path, cookie, delay)
                fired += 1
    text = log_since(mark)
    aborted = re.findall(ABORT_RE, text)
    # A warm app can answer these pages in 2-6 ms, faster than a sequential reset lands. Top up with
    # concurrent bursts (32 requests in flight at once, all reset together) until the precondition
    # holds; every burst is inside the same log window the zero-noise checks below inspect.
    bursts = 0
    while (len(aborted) < 3 or not {"/dev", "/dev/api/status"} & set(aborted)) and bursts < 12:
        bursts += 1
        burst_abort([pth for pth in DEV_PATHS for _ in range(4)], cookie, 0.002 + 0.003 * (bursts % 4))
        fired += 4 * len(DEV_PATHS)
        text = log_since(mark)
        aborted = re.findall(ABORT_RE, text)
    if bursts:
        print(f"   storm top-up: {bursts} concurrent burst(s) of {4 * len(DEV_PATHS)}", flush=True)
    print(f"   storm: {fired} requests reset, {len(aborted)} ended as 499 mid-request: "
          f"{sorted(set(aborted))}", flush=True)
    check("abort storm: requests really were dropped mid-request (499 recorded on the dev paths)", len(aborted) >= 3,
          len(aborted))
    check("the dashboard itself (/dev) was among the aborted requests", "/dev" in aborted or "/dev/api/status" in aborted,
          sorted(set(aborted)))
    q = quiet(text)
    check("abort storm: ZERO cancellations reached the global handler", q["oce_to_global_handler"] == 0, q)
    check("abort storm: ZERO OperationCanceledException stack traces logged", q["oce_stack_traces"] == 0, q)
    check("abort storm: ZERO status-code re-executions (/Error?code=499) for gone clients", q["reexecuted_499"] == 0, q)
    check("abort storm: ZERO unhandled exceptions", q["unhandled"] == 0, q)
    check("the aborted requests are logged under their own path (not rewritten to /Error)",
          "GET /Error responded 499" not in text)

    # ---------------- E. dashboard poll aborted by the browser ----------------
    mark = log_mark()
    res = pg.evaluate("""async () => {
        const out = [];
        for (let i = 0; i < 12; i++) {
            const ac = new AbortController();
            const p = fetch('/dev/api/status', {signal: ac.signal, headers: {Accept: 'application/json'}})
                .then(() => 'completed').catch(e => e.name);
            setTimeout(() => ac.abort(), i % 4);  // 0-3 ms: mid-request
            out.push(await p);
        }
        return out;
    }""")
    check("browser aborted the dashboard status poll (AbortController)", "AbortError" in res, res)
    q = quiet(log_since(mark))
    check("aborted polls: no cancellation noise in the app log", q == {k: 0 for k in q}, q)

    # ---------------- F. everything still works ----------------
    health = json.loads(urllib.request.urlopen(BASE + "/health").read())
    check("public /health still Healthy after the storm", health.get("status") == "Healthy", health.get("status"))
    st = pg.request.get(BASE + "/dev/api/status", headers={"Accept": "application/json"})
    js = st.json()
    check("status JSON returns tiles after the storm", st.status == 200 and len(js.get("tiles", [])) >= 5, st.status)
    goto(pg, "/dev/service-fees")
    check("service-fees page (the query from the report) renders", "Service fees" in pg.inner_text("main"))
    goto(pg, "/dev")
    pg.wait_for_selector("[data-dv-tiles]")
    check("dashboard renders normally after the storm", pg.locator("[data-dv-tiles] [data-tile]").count() >= 5)
    shot("dashboard-after-abort-storm")
    goto(pg, "/dev/logs?severity=Error")
    body = pg.inner_text("main")
    check("technical log has no OperationCanceled error entries", "OperationCanceled" not in body)
    shot("technical-log-no-cancellation-errors")

    # ---------------- G. blank-404 fix ----------------
    owner = use(browser.new_context(viewport={"width": 1366, "height": 900}).new_page())
    owner_login(owner)
    missing = "00000000-0000-0000-0000-0000000e2e40"
    for suffix, label in (("", "details"), ("/edit", "edit")):
        mark = log_mark()
        resp = owner.goto(f"{BASE}/admin/customers/{missing}{suffix}", wait_until="domcontentloaded")
        txt = owner.inner_text("body")
        link = owner.locator('[data-testid="error-home-link"]')
        check(f"missing customer ({label}): 404 status", resp.status == 404, resp.status)
        check(f"missing customer ({label}): styled error page, not a blank document",
              len(txt.strip()) > 50 and "not found" in txt.lower(), txt[:80])
        check(f"missing customer ({label}): staff home link 'Admin dashboard'",
              link.count() == 1 and link.inner_text().strip() == "Admin dashboard")
        runs = log_since(mark).count("Executed action DeChimex.Host.Controllers.Mvc.Admin.AdminCustomersController")
        check(f"missing customer ({label}): the action ran once (not re-executed)", runs == 1, runs)
        shot(f"admin-missing-customer-{label}-styled-404")
    # A real form whose anti-forgery token is gone (expired tab / tampered) — used to be a BLANK 400
    # (the POST was re-executed), and with only the endpoint cleared it would have been a 405.
    goto(owner, "/admin/customers/create")
    owner.fill('main input[name="FirstName"]', "NoToken")
    owner.fill('main input[name="LastName"]', TAG)
    owner.evaluate("() => document.querySelectorAll('main form input[name=__RequestVerificationToken]').forEach(i => i.remove())")
    owner.evaluate("() => document.querySelectorAll('main form').forEach(f => f.noValidate = true)")
    with owner.expect_navigation() as nav:
        owner.click('main form[method="post"] button[type="submit"]')
    resp = nav.value
    txt = owner.inner_text("body")
    link = owner.locator('[data-testid="error-home-link"]')
    check("form posted without its anti-forgery token: rejected with 400", resp.status == 400, resp.status)
    check("rejected form shows the styled error page (not blank, not 405)",
          len(txt.strip()) > 50 and "405" not in txt and link.count() == 1 and link.inner_text().strip() == "Admin dashboard", txt[:80])
    shot("admin-form-without-antiforgery-token-styled-400")
    goto(owner, "/admin/customers?search=NoToken")
    check("nothing was created by the rejected form", "NoToken" not in owner.inner_text("main"))

    resp = owner.goto(f"{BASE}/no-such-page-{TAG}", wait_until="domcontentloaded")
    check("unknown URL still gives the styled 404", resp.status == 404 and "not found" in owner.inner_text("body").lower())

    print(f"\nRESULT: {len(CHECKS)} passed, 0 failed (TAG={TAG})", flush=True)
    browser.close()
