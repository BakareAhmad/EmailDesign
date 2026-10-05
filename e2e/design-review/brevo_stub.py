"""Local Brevo v3 stub for the design-review E2E.

Captures outbound emails (verification tokens) AND the request headers — the
`api-key` header proves the configured Brevo key is loaded and used at runtime
(the app skips the HTTP call entirely when the key is blank)."""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn

CAPTURE = "/home/user/e2e-dr-wip/brevo_captured.jsonl"
FAIL_SENDS = False
FAILED_COUNT = 0


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def handle_expect_100(self):
        self.send_response_only(100)
        self.end_headers()
        return True

    def _send(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/control/mode":
            return self._send(200, {"fail": FAIL_SENDS, "failed": FAILED_COUNT})
        self._send(200, {"email": "stub@local", "plan": [{"type": "free"}]})

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

    def do_POST(self):
        global FAIL_SENDS, FAILED_COUNT
        raw = self._read_body()
        # Opt-in outage simulation (dev-portal E2E): POST /control/mode {"fail": true|false}.
        # Default is off, so every other harness keeps the always-201 behaviour.
        if self.path == "/control/mode":
            FAIL_SENDS = bool((json.loads(raw or "{}") or {}).get("fail"))
            return self._send(200, {"fail": FAIL_SENDS})
        if FAIL_SENDS:
            FAILED_COUNT += 1
            return self._send(500, {"code": "internal_error", "message": "stub: simulated Brevo outage"})
        try:
            payload = json.loads(raw)
        except Exception:
            payload = {"_raw": raw}
        payload["_path"] = self.path
        payload["_headers"] = {k: v for k, v in self.headers.items()}
        with open(CAPTURE, "a") as f:
            f.write(json.dumps(payload) + "\n")
        self._send(201, {"messageId": "stub-msg-1"})


if __name__ == "__main__":
    # Threading server: a parked keep-alive connection from a pooled HttpClient
    # must never block accept() for other sends (a single-threaded server stalled
    # outbound email capture by 60s+ and starved the E2E token wait).
    class ThreadingHTTPServer(ThreadingMixIn, HTTPServer):
        daemon_threads = True

    ThreadingHTTPServer(("127.0.0.1", 9999), H).serve_forever()
