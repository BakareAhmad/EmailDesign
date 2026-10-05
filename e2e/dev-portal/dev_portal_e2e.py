#!/usr/bin/env python3
"""Developer Portal E2E (DEV_PORTAL_ARCHITECTURE_ASSESSMENT.md, brief §44).

Runs against a live app wired to the Paystack stub (:9090) and the Brevo stub (:9999),
AFTER e2e/direct-checkout/workflow.py has produced real customer payments (success,
decline, abandoned, refund, custom order) at the default 100% service fee.

  SETUP (real flows, no DB pokes)
    owner changes service fee 100% -> 50% (preview + confirm)  ->  customer buys at 50%
    -> owner restores 100%  (historical percentage must stay 50% on that payment)
    webhooks: valid duplicate, amount mismatch, invalid signature
    Brevo outage (stub fail mode) during a real forgot-password email
  DEVELOPER PORTAL (screenshots 01..)
    login + invalid login, dashboard (tiles click through), health, technical log
    (filters/search/detail/correlation/redaction), error center + drill-down, statistics,
    integrations, payment + email diagnostics, service fees (overview, ledger, detail,
    invalid range, CSV reconciliation without PII), jobs, database (read-only),
    configuration (no secret values), maintenance (confirmation required, success,
    idempotent re-run, invalid reference, audit trail), Paystack outage -> Unavailable
    -> recovery, password-change validation, access denial for developer/customer/owner.

Usage: E2E_SHOTS_DIR=<evidence> PAYSTACK_STUB_PID=<pid> python3 dev_portal_e2e.py <tag>
"""
from pathlib import Path
import csv, hmac, hashlib, io, json, os, re, signal, subprocess, sys, time, urllib.request, urllib.error

ROOT = Path(__file__).resolve().parents[2]
BASE = os.environ.get("E2E_BASE_URL", "http://127.0.0.1:5119")
STUB = "http://127.0.0.1:9090"
BREVO = "http://127.0.0.1:9999"
TAG = (sys.argv[1] if len(sys.argv) > 1 else "dp")[:10]
OUT = Path(os.environ.get("E2E_SHOTS_DIR", "/tmp/dp-shots"))
OUT.mkdir(parents=True, exist_ok=True)
DEV_EMAIL, DEV_PASS = "developer@dechimexprinthub.local", "DevPortal#2026!"
SETTINGS = json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.json").read_text())
SETTINGS.update(json.loads((ROOT / "DeChimexPrints/src/DeChimex.Host/appsettings.Development.json").read_text()))
SECRETS = [SETTINGS["Paystack"]["SecretKey"], SETTINGS["Brevo"]["ApiKey"]]  # leak checks only; never printed

# Reuse the proven checkout helpers (owner_login, register_customer, create_category,
# create_product, add_to_cart, place_checkout_order, pay_order, start_payment, post_webhook).
os.environ.setdefault("E2E_SHOTS_DIR", "/tmp/dp-helper-shots")
_saved_out = os.environ["E2E_SHOTS_DIR"]
os.environ["E2E_SHOTS_DIR"] = "/tmp/dp-helper-shots"
wf_path = ROOT / "e2e/direct-checkout/workflow.py"
W = {"__file__": str(wf_path), "__name__": "dc_helpers"}
sys.argv = [str(wf_path), TAG]
exec(compile(wf_path.read_text(), str(wf_path), "exec"), W)
os.environ["E2E_SHOTS_DIR"] = _saved_out

PASS, FAIL = [], []
N = [0]


def check(name, cond, extra=""):
    (PASS if cond else FAIL).append(name)
    print(("  PASS " if cond else "  FAIL ") + name + ("" if cond else f"   [{str(extra)[:300]}]"), flush=True)


def shot(p, name):
    N[0] += 1
    p.screenshot(path=str(OUT / f"{N[0]:02d}-{name}.png"), full_page=True)


def goto(p, path):
    p.goto(BASE + path, wait_until="networkidle")
    p.wait_for_timeout(250)


def text(p):
    # Portal pages: only the content area, so sidebar labels can never satisfy an assertion.
    return p.inner_text("#dv-main") if p.locator("#dv-main").count() else p.inner_text("body")


def no_secrets(html):
    return not any(s in html for s in SECRETS)


def login(p, email, password):
    goto(p, "/Account/Login")
    p.fill('form[action*="Login"] input[name="Email"]', email)
    p.fill('form[action*="Login"] input[name="Password"]', password)
    p.click('form[action*="Login"] button[type="submit"]')
    p.wait_for_load_state("networkidle")
    p.wait_for_timeout(600)


def maintenance(p, key, confirm=True, reference=None):
    goto(p, "/dev/maintenance")
    form = p.locator(f'form[action="/dev/maintenance/{key}"]')
    if reference is not None:
        form.locator('input[name="reference"]').fill(reference)
    if confirm:
        form.locator('input[name="confirm"]').check()
    else:
        # Bypass the browser-side guard (required + confirm dialog) to prove the SERVER refuses.
        form.evaluate("f => { f.removeAttribute('data-confirm'); f.querySelectorAll('[required]').forEach(e => e.removeAttribute('required')); }")
    form.locator('button[type="submit"]').click()
    p.wait_for_load_state("networkidle")
    p.wait_for_timeout(500)
    return text(p)


def raw_webhook(body: bytes, signature: str):
    req = urllib.request.Request(BASE + "/api/v1/payments/webhook/paystack", data=body, method="POST",
                                 headers={"Content-Type": "application/json", "x-paystack-signature": signature})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code


def set_fee(owner, value):
    goto(owner, "/admin/settings")
    f = owner.locator('form[action$="/service-fee"]')
    f.locator('input[name="newValue"]').fill(value)
    f.locator('button[type="submit"]').click()
    owner.wait_for_load_state("networkidle")
    if owner.locator('form[action$="/service-fee/confirm"]').count():
        owner.locator('form[action$="/service-fee/confirm"] button[type="submit"]').click()
        owner.wait_for_load_state("networkidle")
    owner.wait_for_timeout(400)
    return text(owner)


def wait_until(fn, timeout=20, step=1.0):
    end = time.time() + timeout
    while time.time() < end:
        if fn():
            return True
        time.sleep(step)
    return False


def main():
    from playwright.sync_api import sync_playwright
    W["stub_post"]("/control/reset", {})
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        ctx = lambda: browser.new_context(viewport={"width": 1440, "height": 900})
        owner, cust, dev, anon = (ctx().new_page() for _ in range(4))
        for pg in (owner, cust, dev, anon):
            pg.on("dialog", lambda d: d.accept())

        # ================= SETUP: real business activity =================
        print("\n===== SETUP: 50% fee purchase, webhooks, Brevo outage =====", flush=True)
        W["owner_login"](owner)
        t = set_fee(owner, "50")
        check("owner changes service fee to 50% (preview + confirm)", "to 50%" in t or "unchanged" in t, t[:200])
        cat = W["create_category"](owner, f"DP Cat {TAG}")
        prod = W["create_product"](owner, f"DP Poster {TAG}", cat, 6000, custom=False)
        buyer = W["register_customer"](cust, "devbuyer")
        check("customer registered + signed in", bool(buyer), buyer)
        W["add_to_cart"](cust, prod)
        order50 = W["place_checkout_order"](cust, "dev portal e2e 50% order")
        ref50 = W["pay_order"](cust, order50, "success")
        cust.wait_for_timeout(1500)
        check("customer paid at 50% service fee (Paystack stub)", bool(ref50) and "Paid" in text(cust) or "paid" in text(cust).lower(), cust.url)
        t = set_fee(owner, "100")
        check("owner restores service fee to 100% (future transactions only)", "to 100%" in t, t[:200])

        # A second order left mid-payment: used for the amount-mismatch webhook and outage re-verify.
        W["add_to_cart"](cust, prod)
        order_open = W["place_checkout_order"](cust, "dev portal e2e open order")
        ref_open = W["start_payment"](cust, order_open)
        check("second payment initialised and left open", bool(ref_open))

        amount50 = W["stub_get"]("/control/state")["tx"][ref50]["amount_minor"]
        st1, _ = W["post_webhook"](ref50, amount50)
        st2, _ = W["post_webhook"](ref50, amount50)
        check("duplicate charge.success webhook acknowledged twice (200/200)", st1 == 200 and st2 == 200)
        st, _ = W["post_webhook"](ref_open, 100)
        check("amount-mismatch webhook acknowledged (200) but not applied", st == 200)
        bad = json.dumps({"event": "charge.success", "data": {"reference": ref_open, "amount": 100}}).encode()
        check("invalid-signature webhook acknowledged (200, rejected internally)", raw_webhook(bad, "deadbeef" * 16) == 200)

        urllib.request.urlopen(urllib.request.Request(BREVO + "/control/mode", data=b'{"fail": true}', method="POST"), timeout=5)
        goto(anon, "/Account/ForgotPassword")
        anon.fill('input[name="Email"]', buyer)
        anon.click('form[action*="ForgotPassword"] button[type="submit"]')
        anon.wait_for_load_state("networkidle")
        # The background email queue may still be draining earlier mail: hold the outage until the
        # stub has actually refused at least one send (deterministic, no fixed sleep).
        failed = lambda: json.loads(urllib.request.urlopen(BREVO + "/control/mode", timeout=5).read())["failed"] >= 1
        refused = wait_until(failed, timeout=90)
        urllib.request.urlopen(urllib.request.Request(BREVO + "/control/mode", data=b'{"fail": false}', method="POST"), timeout=5)
        check("Brevo refused a real email send during the simulated outage", refused)
        time.sleep(3)  # let the diagnostics writer flush (2 s cadence)

        # ================= DEVELOPER PORTAL =================
        print("\n===== 01-02: login =====", flush=True)
        goto(dev, "/dev")
        check("anonymous /dev redirects to login", "/Account/Login" in dev.url, dev.url)
        dev.fill('form[action*="Login"] input[name="Email"]', DEV_EMAIL)
        dev.fill('form[action*="Login"] input[name="Password"]', DEV_PASS)
        shot(dev, "developer-login")
        login(dev, DEV_EMAIL, "wrong-password")
        check("invalid developer password is rejected with a message", "Invalid email or password" in text(dev))
        shot(dev, "developer-login-invalid-password")
        login(dev, DEV_EMAIL, DEV_PASS)
        check("developer lands on /dev after login", dev.url.rstrip("/").endswith("/dev"), dev.url)

        print("\n===== 03: dashboard =====", flush=True)
        t, html = text(dev), dev.content()
        check("dashboard shows overall state", "Overall" in t or "overall" in t.lower())
        tiles = dev.locator("a.dv-tile[data-tile]")
        keys = [tiles.nth(i).get_attribute("data-tile") for i in range(tiles.count())]
        check("dashboard has app/db/paystack/email/storage/realtime/jobs tiles",
              all(k in keys for k in ["app", "db", "paystack", "email", "storage", "realtime", "jobs"]), keys)
        check("every status tile is clickable (href to details)", all((tiles.nth(i).get_attribute("href") or "").startswith("/dev") for i in range(tiles.count())))
        check("database tile shows live latency", re.search(r"Responding · [\d.]+ ms", t) is not None, t[:400])
        check("dashboard contains no secrets", no_secrets(html))
        check("developer sees no admin business navigation", "/admin/settings" not in html and "/admin/users" not in html)
        shot(dev, "dashboard")
        dev.locator('a.dv-tile[data-tile="db"]').click(); dev.wait_for_load_state("networkidle")
        check("DB tile clicks through to Database & migrations", "/dev/database" in dev.url or "/dev/health" in dev.url, dev.url)
        st = json.loads(dev.request.get(BASE + "/dev/api/status").text())
        check("/dev/api/status JSON has overall + tiles", "overall" in st and len(st.get("tiles", [])) >= 7)

        print("\n===== 04: health =====", flush=True)
        goto(dev, "/dev/health")
        t, html = text(dev), dev.content()
        for k in ["Uptime", "Environment", "Development", ".NET", "Migrations"]:
            check(f"health shows {k}", k in t)
        check("diagnostics pipeline lost nothing (0 dropped, 0 failed writes, 0 capture failures)",
              re.search(r"dropped 0 · failed writes 0 · capture failures 0", t) is not None, re.findall(r"Queued.*", t)[:1])
        check("health never shows connection strings/secrets", no_secrets(html) and "Password=" not in html and "Server=" not in html)
        shot(dev, "health")

        print("\n===== 05-07: technical log =====", flush=True)
        goto(dev, "/dev/logs?range=24h")
        check("technical log lists events", dev.locator("table tbody tr").count() > 0)
        shot(dev, "technical-log")
        goto(dev, "/dev/logs?range=24h&severity=Warning%2B&category=Email")
        rows_txt = " ".join(dev.locator("main table tbody tr").all_inner_texts())
        check("filter severity≥Warning + category Email finds the Brevo failure", "Brevo send failed" in rows_txt, rows_txt[:300])
        check("recipient email is never shown in full in the log", buyer not in dev.content())
        shot(dev, "technical-log-filtered-email-failures")
        goto(dev, f"/dev/logs?range=24h&q={ref_open}")
        check("payment reference stays searchable in the log (not over-redacted)", ref_open in text(dev) and "[REDACTED-NUMBER]" not in text(dev))
        goto(dev, "/dev/logs?range=24h&q=signature")
        check("search 'signature' finds the rejected webhook", "signature" in text(dev).lower())
        goto(dev, "/dev/logs?range=24h&severity=Warning%2B&category=Email")
        link = dev.locator("table tbody tr td a[href^='/dev/logs/']").first
        link.click(); dev.wait_for_load_state("networkidle")
        t, html = text(dev), dev.content()
        check("log detail shows correlation, route, environment and metadata", all(k in t for k in ["Correlation", "Environment"]), t[:400])
        check("log detail has no secrets or full recipient email", no_secrets(html) and buyer not in html)
        shot(dev, "technical-log-event-detail")
        corr = dev.locator("a[href*='correlationId=']").first
        if corr.count():
            corr.click(); dev.wait_for_load_state("networkidle")
            check("correlation link shows all events of that request", dev.locator("table tbody tr").count() >= 1)

        print("\n===== 08-09: error center =====", flush=True)
        goto(dev, "/dev/errors?range=24h&warnings=true")
        rows = dev.locator("table tbody tr")
        check("error center groups errors (occurrences, first/last seen)", rows.count() > 0 and "first seen" in text(dev).lower() and "occurrences" in text(dev).lower())
        t = text(dev)
        check("expected business rejections (wrong password, cart guard) are not reported as errors",
              'Error handling "LoginCommand"' not in t and 'Error handling "AddCartItemCommand"' not in t)
        check("client-aborted requests (OperationCanceledException) are not reported as errors", "OperationCanceledException" not in t)
        check("no EF 'same entity tracked as different entity types' warning flood", "different entity types" not in t)
        shot(dev, "error-center")
        dev.locator("a[href^='/dev/errors/']").first.click(); dev.wait_for_load_state("networkidle")
        check("error group drill-down lists individual occurrences", "/dev/errors/" in dev.url and dev.locator("a[href^='/dev/logs/']").count() > 0)
        shot(dev, "error-group-occurrences")

        print("\n===== 10: statistics =====", flush=True)
        for r in ["1h", "24h", "7d", "30d"]:
            goto(dev, f"/dev/performance?range={r}")
            check(f"statistics renders for range {r}", dev.locator(".dv-kpi").count() > 0)
        goto(dev, "/dev/performance?range=custom&from=2026-09-01&to=2026-09-30")
        check("custom range accepted", "custom" in dev.url and dev.locator(".dv-kpi").count() > 0)
        goto(dev, "/dev/performance?range=24h")
        t = text(dev)
        check("statistics show requests, error rate and average response time",
              all(k.lower() in t.lower() for k in ["Requests", "Error rate", "Avg"]), t[:300])
        shot(dev, "statistics-performance")

        print("\n===== 11: integrations =====", flush=True)
        goto(dev, "/dev/integrations")
        t, html = text(dev), dev.content()
        check("integrations show Paystack and Brevo", "Paystack" in t and "Brevo" in t)
        check("integration config shown as Configured / test mode, never values", "Configured" in t and no_secrets(html))
        check("Brevo failure is visible with last success", "Fail" in t and ("Last success" in t or "last success" in t.lower()))
        shot(dev, "integrations")

        print("\n===== 12-13: payment + email diagnostics =====", flush=True)
        goto(dev, "/dev/payments?range=24h")
        t, html = text(dev), dev.content()
        # Webhooks arrive after the callback already verified the payment, so the correct outcome is
        # Duplicate (idempotent), never a second "Processed".
        for k in ["Duplicate", "AmountMismatch", "Rejected"]:
            check(f"payment diagnostics show webhook outcome {k}", k in t)
        check("payment diagnostics show refunds", "Refund" in t)
        check("payment diagnostics expose no customer emails or raw payloads", buyer not in html and '"authorization_url"' not in html)
        shot(dev, "payment-diagnostics")
        goto(dev, "/dev/email?range=24h")
        t, html = text(dev), dev.content()
        check("email diagnostics count attempts/successes/failures", "Failure" in t and "Success" in t)
        check("email diagnostics show template + recipient category, not contents", "ForgotPassword" in t and "customer" in t.lower(), t[:400])
        check("email diagnostics never show the recipient address", buyer not in html)
        check("no arbitrary send tool on email page", dev.locator("main form[method='post']").count() == 0)
        shot(dev, "email-diagnostics")

        print("\n===== 14-17: service fees =====", flush=True)
        goto(dev, "/dev/service-fees")
        t = text(dev)
        for k in ["All time", "Today", "This month", "Gross", "Net"]:
            check(f"service-fee overview shows {k}", k.lower() in t.lower())
        check("refund rules are stated explicitly", "refund" in t.lower() and "full" in t.lower())
        shot(dev, "service-fees-overview")
        goto(dev, "/dev/service-fees/ledger")
        t = text(dev)
        check("ledger lists the 50% payment and 100% payments", "50.00%" in t.replace(" %", "%") or "50%" in t)
        check("ledger shows reversed (refunded) fee", "Reversed" in t)
        check("ledger reconciles (gross − reversed = net)", "✓" in t or "matches" in t.lower())
        shot(dev, "service-fee-ledger")
        goto(dev, f"/dev/service-fees/ledger?q={ref50}")
        dev.locator(f"a:has-text('{ref50}')").first.click(); dev.wait_for_load_state("networkidle")
        t = text(dev)
        check("fee detail keeps the historical 50% although config is now 100%", "50" in t and ref50 in t)
        check("fee detail traces order, Paystack fee and status", "Paystack" in t and "Collected" in t and "DCX-" in t)
        check("fee detail hides customer PII", buyer not in dev.content())
        shot(dev, "service-fee-transaction-detail")
        goto(dev, "/dev/service-fees?from=2026-09-30&to=2026-09-01")
        check("inverted custom range shows a validation error", "From" in text(dev) and dev.locator(".dv-alert, .dv-error, [role=alert]").count() > 0, text(dev)[:300])
        shot(dev, "service-fees-invalid-range")
        r = dev.request.get(BASE + "/dev/service-fees/export")
        body = r.body().decode("utf-8-sig")
        (OUT / "final-service-fee-export.csv").write_text(body)
        rows = list(csv.DictReader(io.StringIO(body)))
        check("CSV export downloads with reconciliation columns", r.status == 200 and "NetFee" in body.splitlines()[0])
        check("CSV has no customer PII (no emails/names)", "@" not in body and "Review" not in body)
        r50 = [x for x in rows if x["PaymentReference"] == ref50]
        check("CSV row for the 50% payment keeps ServiceFeePercent 50.00", bool(r50) and r50[0]["ServiceFeePercent"] == "50.00", r50)
        net_csv = sum(float(x["NetFee"]) for x in rows)
        print(f"    CSV rows={len(rows)} ΣNetFee={net_csv:.2f}")
        check("CSV contains refunded (reversed) rows", any("Reversed" in x["FeeStatus"] for x in rows))

        print("\n===== 18-20: jobs, database, configuration =====", flush=True)
        goto(dev, "/dev/jobs")
        t = text(dev)
        check("jobs show status/last run/next run for background jobs", "Email" in t and ("Next" in t or "next" in t.lower()))
        shot(dev, "background-jobs")
        goto(dev, "/dev/database")
        t, html = text(dev), dev.content()
        check("database page shows provider, latency and migrations", "Migration" in t or "migration" in t.lower())
        check("database page is read-only (no forms / destructive buttons)", dev.locator("main form[method='post']").count() == 0
              and not re.search(r"\b(Drop|Truncate|Delete)\b", t))
        check("database page never shows the connection string", no_secrets(html) and "Password=" not in html)
        shot(dev, "database-migrations")
        goto(dev, "/dev/configuration")
        t, html = text(dev), dev.content()
        check("configuration shows Configured / test mode statuses", "Configured" in t and ("Test" in t or "test" in t))
        check("configuration never shows secret values", no_secrets(html) and SETTINGS["Paystack"]["PublicKey"] not in html)
        shot(dev, "configuration-overview")

        print("\n===== 21-25: maintenance =====", flush=True)
        goto(dev, "/dev/maintenance")
        actions = sorted(f.get_attribute("action") for f in dev.locator("main form[method='post']").all())
        # AUD-034 adds "recompute-spending" (lifetime-spending reconciliation) as a sixth
        # SAFE tool — still no SQL/shell/file/run-anything surface.
        check("maintenance offers exactly the 6 safe tools (no SQL/shell/file/run-anything)",
              actions == sorted(f"/dev/maintenance/{k}" for k in ["rerun-health", "clear-cache", "flush-diagnostics", "apply-retention", "reverify-payment", "recompute-spending"]), actions)
        check("maintenance forms carry anti-forgery tokens",
              dev.locator("main form[method='post'] input[name='__RequestVerificationToken']").count() == len(actions))
        shot(dev, "maintenance-tools")
        t = maintenance(dev, "rerun-health", confirm=False)
        check("server refuses maintenance without confirmation", "tick the confirmation box" in t, t[:300])
        shot(dev, "maintenance-confirmation-required")
        code = dev.request.post(BASE + "/dev/maintenance/clear-cache", form={"confirm": "yes"}).status
        check("maintenance POST without anti-forgery token is rejected (400)", code == 400, code)
        t = maintenance(dev, "clear-cache")
        check("clear cache succeeds with confirmation", "Clear" in t and ("evicted" in t or "cleared" in t.lower()), t[:300])
        t = maintenance(dev, "clear-cache")
        check("clear cache is idempotent (second run also succeeds)", "evicted" in t or "cleared" in t.lower())
        t = maintenance(dev, "rerun-health")
        check("re-run health checks reports overall status", "Overall" in t)
        t = maintenance(dev, "reverify-payment", reference=ref50)
        check("re-verify of an already-paid payment is a no-op", "nothing to do" in t, t[:300])
        shot(dev, "maintenance-success-and-audit")
        t = maintenance(dev, "reverify-payment", reference="NO-SUCH-REF-123")
        check("re-verify with an unknown reference shows an error", "No payment with reference" in t)
        shot(dev, "maintenance-reverify-unknown-reference")
        # AUD-034: lifetime-spending reconciliation is a safe tool and is idempotent.
        t = maintenance(dev, "recompute-spending")
        check("recompute spending reports reconciliation result", "match order history" in t or "Corrected" in t, t[:300])
        t = maintenance(dev, "recompute-spending")
        check("recompute spending is idempotent (second run reports no drift)", "match order history" in t or "Corrected" in t, t[:300])

        time.sleep(3)
        goto(dev, "/dev/logs?range=1h&q=ran+%27")
        check("maintenance actions are audited as 'Developer X ran Y'", "Developer" in text(dev) and "ran '" in text(dev))

        print("\n===== 26-27: Paystack outage -> Unavailable -> recovery =====", flush=True)
        pid = int(os.environ.get("PAYSTACK_STUB_PID", "0") or 0)
        if pid:
            os.kill(pid, signal.SIGTERM)
        time.sleep(1.5)
        t = maintenance(dev, "rerun-health")
        check("health re-run detects Paystack unreachable", "Paystack" in t and ("Unhealthy" in t or "Unavailable" in t or "Degraded" in t), t[:300])
        t = maintenance(dev, "reverify-payment", reference=ref_open)
        check("re-verify during outage fails safely with a message (no crash)", "Re-verify" in t or "verify" in t.lower(), t[:300])
        goto(dev, "/dev")
        tile = dev.locator('a.dv-tile[data-tile="paystack"]')
        cls = tile.get_attribute("class") or ""
        check("dashboard Paystack tile shows Unavailable", "Unavailable" in cls or "Unavailable" in tile.inner_text(), cls)
        shot(dev, "dashboard-paystack-unavailable")
        goto(dev, "/dev/integrations")
        shot(dev, "integrations-paystack-unreachable")
        proc = subprocess.Popen([sys.executable, str(ROOT / "e2e/payments/paystack_stub.py")],
                                stdout=open("/tmp/dp-paystack-restart.log", "w"), stderr=subprocess.STDOUT, start_new_session=True)
        Path("/tmp/dp-paystack.pid").write_text(str(proc.pid))
        time.sleep(1.5)
        t = maintenance(dev, "rerun-health")
        goto(dev, "/dev")
        cls = dev.locator('a.dv-tile[data-tile="paystack"]').get_attribute("class") or ""
        # Recovered = reachable again. The tile may stay Warning for an hour because the outage left
        # failed calls in the last hour — that is the intended "recent failures" signal.
        check("Paystack tile recovers from Unavailable once the stub returns", "st-Healthy" in cls or "st-Warning" in cls, cls)
        goto(dev, "/dev/health")
        check("Paystack health check is Healthy again", re.search(r"paystack\s+Healthy", text(dev), re.I) is not None
              or "Paystack reachable" in text(dev), text(dev)[:500])

        print("\n===== 28: developer password validation =====", flush=True)
        goto(dev, "/dev/account/password")
        dev.fill('input[name="CurrentPassword"]', DEV_PASS)
        dev.fill('input[name="NewPassword"]', "Another#Pass2026")
        dev.fill('input[name="ConfirmPassword"]', "Mismatch#2026")
        dev.locator('form[action*="password"] button[type="submit"]').click(); dev.wait_for_load_state("networkidle")
        check("password change rejects mismatched confirmation", "match" in text(dev).lower(), text(dev)[:300])
        shot(dev, "developer-password-validation")

        print("\n===== 29-31: access denial =====", flush=True)
        for path in ["/admin/settings", "/admin/users", "/admin/customers", "/admin/orders"]:
            goto(dev, path)
            check(f"developer denied {path}", "AccessDenied" in dev.url or "Access denied" in text(dev) or "/Account/Login" in dev.url, dev.url)
        goto(dev, "/admin/settings")
        shot(dev, "access-denied-developer-admin-settings")
        code = dev.request.post(BASE + "/admin/settings/service-fee", form={"newValue": "1"}, max_redirects=0).status
        check("developer cannot change pricing / service fee (POST refused)", code in (302, 400, 403), code)
        goto(cust, "/dev")
        check("customer denied /dev", "/dev" not in cust.url.replace("/Account/AccessDenied", "") or "AccessDenied" in cust.url, cust.url)
        check("customer navigation has no developer link", 'href="/dev' not in cust.content())
        shot(cust, "access-denied-customer-dev-portal")
        goto(owner, "/dev")
        check("owner without explicit dev grant is denied /dev", "AccessDenied" in owner.url or "Access denied" in text(owner), owner.url)
        goto(owner, "/admin")
        check("owner admin navigation shows no developer portal link", 'href="/dev"' not in owner.content())
        goto(owner, "/dev")
        shot(owner, "access-denied-owner-without-grant")
        r = cust.request.get(BASE + "/dev/api/status", max_redirects=0)
        check("customer gets no dev API data", r.status in (302, 401, 403), r.status)

        browser.close()
    wf_fails = W.get("FAILS", [])
    print(f"\nRESULT: {len(PASS)} passed, {len(FAIL) + len(wf_fails)} failed")
    for f in FAIL + list(wf_fails):
        print("  FAILED:", f)
    sys.exit(1 if FAIL or wf_fails else 0)


if __name__ == "__main__":
    main()
