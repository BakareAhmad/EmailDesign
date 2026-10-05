"""Local Paystack API + hosted-checkout stub for the payment-lifecycle E2E.

Simulates the exact integration surface the app uses (current Paystack docs):
  POST /transaction/initialize      -> authorization_url pointing at the simulated checkout
  GET  /checkout/{ref}              -> simulated Paystack hosted checkout (channel options)
  GET  /simulate/{ref}/{action}     -> outcome controls; some redirect to callback_url
  GET  /transaction/verify/{ref}    -> documented status vocabulary responses
  POST /refund                      -> refund acceptance ({status:true, data:{...}})
  POST /control/set                 -> per-reference outcome overrides (pending variants, mismatches)
  GET  /control/state               -> full captured state (for the workflow + evidence log)

Secrets hygiene: the Authorization header is only compared against the key in the app's
own appsettings and logged as a boolean (matches / present) — the value is never stored
or printed. Threading server: the app's pooled HttpClient keep-alives must never block
accept() (same lesson as the Brevo stub).
"""
import hashlib
import hmac
import json
import os
import re
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn
from urllib.parse import urlparse, parse_qs

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
SETTINGS_PATH = os.path.join(ROOT, "DeChimexPrints/src/DeChimex.Host/appsettings.json")
WORKDIR = "/home/user/payments-e2e"
STATE_PATH = os.path.join(WORKDIR, "stub_state.json")
LOG_PATH = os.path.join(WORKDIR, "stub_requests.jsonl")

# Account-enabled channel list the simulated hosted checkout offers when the app does
# not restrict `channels` (current docs: omitting the parameter shows every channel
# enabled on the integration). Simulated here as the NGN-relevant set.
ACCOUNT_CHANNELS = ["card", "bank_transfer", "ussd", "mobile_money", "bank", "qr", "apple_pay", "eft"]

LOCK = threading.Lock()
STATE = {"tx": {}, "refunds": [], "verify_calls": [], "init_calls": []}


def load_secret():
    with open(SETTINGS_PATH) as f:
        return json.load(f)["Paystack"]["SecretKey"]


def save_state():
    os.makedirs(WORKDIR, exist_ok=True)
    with open(STATE_PATH, "w") as f:
        json.dump(STATE, f, indent=2)


def load_state():
    global STATE
    if os.path.exists(STATE_PATH):
        try:
            with open(STATE_PATH) as f:
                STATE = json.load(f)
        except Exception:
            pass


def log(entry):
    os.makedirs(WORKDIR, exist_ok=True)
    with open(LOG_PATH, "a") as f:
        f.write(json.dumps(entry) + "\n")


def provider_status_for(outcome):
    """Map a simulated outcome to Paystack's documented transaction statuses."""
    return {
        "success": "success",
        "failed": "failed",
        "abandon": "abandoned",
        "abandoned": "abandoned",
        "pending": "pending",
        "processing": "processing",
        "queued": "queued",
        "ongoing": "ongoing",
        "reversed": "reversed",
        "amount_mismatch": "success",      # pays, but the wrong amount
        "currency_mismatch": "success",    # pays, but in the wrong currency
    }.get(outcome, outcome)


def gateway_response_for(outcome):
    return {
        "success": "Successful",
        "failed": "Declined by bank",
        "abandon": "Abandoned",
        "amount_mismatch": "Successful",
        "currency_mismatch": "Successful",
    }.get(outcome, "Pending")


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def _send(self, code, obj=None, content_type="application/json", raw=None):
        if raw is not None:
            body = raw if isinstance(raw, bytes) else raw.encode()
        else:
            body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_html(self, html):
        self._send(200, content_type="text/html; charset=utf-8", raw=html)

    def _read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            chunks = []
            while True:
                line = self.rfile.readline().decode("utf-8", "replace").strip()
                size = int(line.split(";")[0] or "0", 16)
                if size == 0:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.readline()
            return b"".join(chunks).decode("utf-8", "replace")
        length = int(self.headers.get("Content-Length", 0) or 0)
        return self.rfile.read(length).decode("utf-8", "replace") if length else ""

    def _auth_status(self):
        header = self.headers.get("Authorization", "")
        m = re.match(r"Bearer\s+(.+)", header)
        if not m:
            return {"present": False, "matches_configured": False}
        return {"present": True, "matches_configured": hmac.compare_digest(m.group(1).strip(), load_secret())}

    # ---------------- API: initialize / verify / refund ----------------

    def do_POST(self):
        parsed = urlparse(self.path)
        path = parsed.path
        raw = self._read_body()
        try:
            payload = json.loads(raw) if raw else {}
        except Exception:
            payload = {}

        if path == "/transaction/initialize":
            auth = self._auth_status()
            ref = payload.get("reference") or f"AUTO-{len(STATE['init_calls']) + 1}"
            tx = {
                "reference": ref,
                "email": payload.get("email"),
                "amount_minor": payload.get("amount"),
                "currency": payload.get("currency"),
                "callback_url": payload.get("callback_url"),
                "channels": payload.get("channels"),
                "metadata": payload.get("metadata"),
                "auth_header": auth,
                "outcome": "success",
                "events": ["initialized"],
            }
            with LOCK:
                STATE["tx"][ref] = tx
                STATE["init_calls"].append({"reference": ref, "auth_header": auth})
                save_state()
            log({"type": "initialize", "reference": ref, "amount_minor": tx["amount_minor"],
                 "currency": tx["currency"], "channels": tx["channels"], "auth_header": auth,
                 "callback_url": tx["callback_url"]})
            self._send(201, {
                "status": True,
                "message": "Authorization URL created",
                "data": {
                    "authorization_url": f"http://127.0.0.1:9090/checkout/{ref}",
                    "access_code": f"acc_{ref}",
                    "reference": ref,
                },
            })
            return

        if path == "/refund":
            auth = self._auth_status()
            tx_ref = payload.get("transaction")
            with LOCK:
                fail = STATE.get("refund_fail", False)
                n = len(STATE["refunds"]) + 1
                STATE["refunds"].append({"transaction": tx_ref, "amount": payload.get("amount"),
                                         "accepted": not fail, "auth_header": auth})
                save_state()
            log({"type": "refund", "transaction": tx_ref, "accepted": not fail, "auth_header": auth})
            if fail:
                self._send(400, {"status": False, "message": "Refund could not be processed."})
                return
            self._send(200, {
                "status": True,
                "message": "Refund processed",
                "data": {
                    "status": "processed",
                    "reference": f"rfnd-{n:04d}",
                    "amount": payload.get("amount"),
                    "currency": "NGN",
                    "transaction": tx_ref,
                },
            })
            return

        if path == "/control/set":
            ref = payload.get("ref")
            with LOCK:
                tx = STATE["tx"].get(ref)
                if tx is None:
                    self._send(404, {"error": "unknown ref"})
                    return
                for k in ("outcome",):
                    if k in payload:
                        tx[k] = payload[k]
                        tx["events"].append(f"control:{k}={payload[k]}")
                if "refund_fail" in payload:
                    STATE["refund_fail"] = bool(payload["refund_fail"])
                save_state()
            log({"type": "control", "ref": ref, "payload": {k: v for k, v in payload.items() if k != "secret"}})
            self._send(200, {"ok": True})
            return

        if path == "/control/reset":
            with LOCK:
                STATE.clear()
                STATE.update({"tx": {}, "refunds": [], "verify_calls": [], "init_calls": [],
                              "refund_fail": False})
                save_state()
            self._send(200, {"ok": True})
            return

        self._send(404, {"error": "not found"})

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        log({"type": "get", "path": path})

        if path == "/control/state":
            self._send(200, STATE)
            return

        m = re.match(r"^/checkout/([^/]+)$", path)
        if m:
            self._send_html(self._checkout_page(m.group(1)))
            return

        m = re.match(r"^/simulate/([^/]+)/([^/]+)$", path)
        if m:
            self._simulate(m.group(1), m.group(2))
            return

        m = re.match(r"^/transaction/verify/([^/]+)$", path)
        if m:
            self._verify(m.group(1))
            return

        self._send(404, {"error": "not found"})

    # ---------------- simulated hosted checkout ----------------

    def _checkout_page(self, ref):
        with LOCK:
            tx = STATE["tx"].get(ref)
        if tx is None:
            return "<html><body>Unknown transaction</body></html>"
        amount = (tx.get("amount_minor") or 0) / 100.0
        channels = tx.get("channels") or ACCOUNT_CHANNELS
        rows = "".join(f"<li>{c}</li>" for c in channels)
        return f"""<!doctype html><html><head><title>Paystack Checkout (simulated)</title>
<style>body{{font-family:sans-serif;background:#0b1a2a;color:#eef;padding:28px}}
.card{{background:#fff;color:#123;border-radius:10px;padding:22px;max-width:640px;margin:auto}}
button,a.btn{{display:block;width:100%;margin:8px 0;padding:12px;border-radius:8px;border:0;
background:#0ba4db;color:#fff;font-size:15px;cursor:pointer;text-align:center;text-decoration:none}}
.alt{{background:#64748b}} .warn{{background:#d97706}} .bad{{background:#dc2626}} .ghost{{background:#334155}}
small{{color:#64748b}}</style></head><body>
<div class="card">
<h2>Paystack Checkout (simulated)</h2>
<p>Amount: <b>NGN {amount:,.2f}</b> &middot; Reference: <code>{ref}</code></p>
<p><small>Payment options enabled on this integration (hosted checkout):</small></p>
<ul>{rows}</ul>
<a class="btn" href="/simulate/{ref}/success">Pay with card &mdash; simulate SUCCESS</a>
<a class="btn alt" href="/simulate/{ref}/success-ussd">Pay with USSD &mdash; simulate SUCCESS (async channel)</a>
<a class="btn warn" href="/simulate/{ref}/ongoing">Bank transfer &mdash; leave PROCESSING (return early)</a>
<a class="btn bad" href="/simulate/{ref}/failed">Simulate DECLINED</a>
<a class="btn ghost" href="/simulate/{ref}/abandon">Close checkout without paying (ABANDON)</a>
<a class="btn ghost" href="/simulate/{ref}/success-noreturn">Pay, then close the browser (NO return)</a>
</div></body></html>"""

    def _paystack_fee_minor(self, amount_minor):
        """Paystack's published NGN local fee in kobo: 1.5% + NGN 100 (flat waived
        under NGN 2,500), total fee capped at NGN 2,000."""
        flat = 0 if amount_minor < 250_000 else 10_000
        return min(round(amount_minor * 0.015) + flat, 200_000)

    def _simulate(self, ref, action):
        with LOCK:
            tx = STATE["tx"].get(ref)
            if tx is None:
                self._send_html("<html><body>Unknown transaction</body></html>")
                return
            outcome_map = {
                "success": "success", "success-ussd": "success", "success-noreturn": "success",
                "failed": "failed", "abandon": "abandon", "ongoing": "ongoing",
            }
            tx["outcome"] = outcome_map.get(action, action)
            tx["events"].append(f"simulate:{action}")
            save_state()
        log({"type": "simulate", "ref": ref, "action": action})
        if action == "success-noreturn":
            self._send_html("<html><body style='font-family:sans-serif;padding:40px'>"
                           "<h2>Payment complete</h2><p>You may close this tab (browser closed before returning).</p>"
                           "</body></html>")
            return
        callback = tx.get("callback_url") or "http://127.0.0.1:5119/checkout/return"
        sep = "&" if "?" in callback else "?"
        target = f"{callback}{sep}trxref={ref}&reference={ref}"
        self.send_response(302)
        self.send_header("Location", target)
        self.send_header("Content-Length", "0")
        self.end_headers()

    # ---------------- documented verify / unknown-ref behaviour ----------------

    def _verify(self, ref):
        with LOCK:
            STATE["verify_calls"].append(ref)
            tx = STATE["tx"].get(ref)
            save_state()
        log({"type": "verify", "ref": ref, "known": tx is not None})
        if tx is None:
            # Real Paystack answers HTTP 400 "Transaction reference was not found".
            self._send(400, {"status": False, "message": "Transaction reference was not found"})
            return
        outcome = tx.get("outcome", "success")
        if outcome == "verify_error":
            self._send(500, {"status": False, "message": "Internal server error"})
            return
        if outcome == "garbage":
            self._send(200, raw="this is not json {{{")
            return
        amount = tx.get("amount_minor") or 0
        currency = tx.get("currency") or "NGN"
        if outcome == "amount_mismatch":
            amount = amount + 100000
        if outcome == "currency_mismatch":
            currency = "USD"
        status = provider_status_for(outcome)
        self._send(200, {
            "status": True,
            "message": "Verification successful",
            "data": {
                "id": 4099260516,
                "domain": "test",
                "status": status,
                "reference": ref,
                "amount": amount,
                "gateway_response": gateway_response_for(outcome),
                "channel": "card",
                "currency": currency,
                "paid_at": "2026-09-23T10:00:00.000Z",
                "created_at": "2026-09-23T09:59:00.000Z",
                "requested_amount": tx.get("amount_minor") or 0,
                # Paystack's published NGN local fee (kobo): 1.5% + NGN 100 flat,
                # flat waived under NGN 2,500, total fee capped at NGN 2,000.
                "fees": self._paystack_fee_minor(amount),
                "customer": {"email": tx.get("email")},
            },
        })


if __name__ == "__main__":
    os.makedirs(WORKDIR, exist_ok=True)
    load_state()

    class ThreadingHTTPServer(ThreadingMixIn, HTTPServer):
        daemon_threads = True

    ThreadingHTTPServer(("127.0.0.1", 9090), H).serve_forever()
