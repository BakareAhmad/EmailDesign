"""Local Brevo v3 stub: captures outbound emails (verification tokens) for E2E."""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer

CAPTURE = "/home/user/e2e-proof-wip/brevo_captured.jsonl"


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
        raw = self._read_body()
        try:
            payload = json.loads(raw)
        except Exception:
            payload = {"_raw": raw}
        payload["_path"] = self.path
        with open(CAPTURE, "a") as f:
            f.write(json.dumps(payload) + "\n")
        self._send(201, {"messageId": "stub-msg-1"})


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", 9999), H).serve_forever()
