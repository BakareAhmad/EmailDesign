"""Quick HTTP smoke: log in as a user and GET a list of paths, reporting status + errors."""
import http.cookiejar, re, sys, urllib.request, urllib.parse, urllib.error

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5119"

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k): return None

def session():
    cj = http.cookiejar.CookieJar()
    follow = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))
    nofollow = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj), NoRedirect)
    return follow, nofollow

def get(op, path):
    try:
        r = op.open(BASE + path, timeout=60)
        return r.status, r.geturl(), r.read().decode("utf-8", "replace"), dict(r.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.headers.get("Location", ""), e.read().decode("utf-8", "replace"), dict(e.headers)

def token(html):
    m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
    return m.group(1) if m else ""

def post(op, path, data):
    body = urllib.parse.urlencode(data).encode()
    try:
        r = op.open(urllib.request.Request(BASE + path, data=body, method="POST"), timeout=60)
        return r.status, r.geturl(), r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.headers.get("Location", ""), e.read().decode("utf-8", "replace")

def login(email, password):
    f, n = session()
    s, u, html, _ = get(f, "/Account/Login")
    s, u, html = post(f, "/Account/Login", {"Email": email, "Password": password, "__RequestVerificationToken": token(html)})
    return f, n, s, u, html

if __name__ == "__main__":
    f, n, s, u, html = login("developer@dechimexprinthub.local", "DevPortal#2026!")
    print("login ->", s, u)
    paths = ["/dev", "/dev/api/status", "/dev/health", "/dev/performance", "/dev/performance?range=1h", "/dev/performance?range=custom&from=2026-09-01&to=2026-09-25",
             "/dev/jobs", "/dev/database", "/dev/configuration", "/dev/logs", "/dev/logs?severity=Error%2B&range=7d", "/dev/logs?q=seed&category=System",
             "/dev/errors", "/dev/errors?warnings=true", "/dev/integrations", "/dev/payments", "/dev/email", "/dev/service-fees",
             "/dev/service-fees?from=2026-09-01&to=2026-09-25", "/dev/service-fees?from=bad&to=x", "/dev/service-fees/ledger",
             "/dev/service-fees/export", "/dev/maintenance", "/dev/account/password",
             "/admin/dashboard", "/admin/users", "/admin/settings", "/portal", "/dev/errors/zzz"]
    bad = []
    for p in paths:
        s, u, body, h = get(f, p)
        err = ""
        if s >= 500 or "Exception" in body[:3000]:
            m = re.search(r"<title>(.*?)</title>", body)
            err = (m.group(1) if m else body[:200]).strip()
        print(f"{s} {p:70s} -> {u.replace(BASE,'')[:60]} {err}")
        if s >= 500 or err:
            bad.append(p)
        if p.startswith(("/admin", "/portal")) and "AccessDenied" not in u:
            bad.append(p + " (developer must be denied)")
    print(f"RESULT: {len(paths) - len(bad)}/{len(paths)} ok" + ("" if not bad else f" — problems: {bad}"))
    sys.exit(1 if bad else 0)
