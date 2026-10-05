"""Brand images + responsive layout E2E (owner-supplied logo + hero art, 2026-09-26).

Real Chromium against the running app (in-memory DB, Brevo + Paystack stubs):
  A. assets: every brand file is served (type/size), manifest icons resolve
  B. home at 10 widths (320..1920): no horizontal overflow, the true-alpha hero cut-out at every width
     (portrait hero-mobile.webp <=720 px, landscape above, as in the pre-refactor references), shown
     WHOLE (rendered ratio == art ratio -> no crop/distortion), inside the viewport, decoded, and on the
     page with no box: no card (transparent background, no shadow/radius) and the rendered art's
     transparent edges are the page colour #F7F6F2; header logo loaded at 38x38 in the viewport
  C. high-DPI phones/desktops pick the right resolution (DPR 1/2/3)
  D. footer logo, auth layout (+ invalid-login error state), 404 page
  E. signed-in customer portal pages at 320/768/1440
  F. admin: topbar logo (mobile), off-canvas sidebar logo, desktop sidebar logo, table pages
  G. shared invoice document (logo in the header) at 320/1440
  H. developer portal at 390/1440
Every page visited: favicon link present and resolvable.
Screenshots: E2E_SHOTS_DIR/NN-name.png (auto-numbered).
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_src = open(os.path.join(HERE, "../design-review/ws_designreview_e2e.py")).read()
exec(compile(_src.split("with sync_playwright() as p:")[0], "ws_designreview_helpers", "exec"))  # helpers only

SHOTS = os.environ.get("E2E_SHOTS_DIR", os.path.abspath(os.path.join(HERE, "../../evidence")))
WIDTHS = [320, 360, 375, 390, 414, 768, 1024, 1280, 1440, 1920]
ADMIN = ("activity analytics categories customers dashboard gallery inventory invoices major-categories marketing messages "
         "notifications orders procurement products receipts reports sales services settings suppliers users").split()
DEV = ("/dev /dev/health /dev/logs /dev/errors /dev/performance /dev/integrations /dev/payments /dev/email /dev/service-fees "
       "/dev/service-fees/ledger /dev/jobs /dev/database /dev/configuration /dev/maintenance /dev/account/password").split()
_n = [0]

SETTLE_JS = """async () => {
  // Wait (max 4 s) for every rendered image that is loading eagerly or already fetched; off-screen
  // loading=lazy images never load until scrolled to, so they are skipped rather than awaited.
  const imgs = [...document.images].filter(i => i.getBoundingClientRect().width > 0 && (i.complete || i.loading !== 'lazy'));
  const all = Promise.all(imgs.map(i => i.complete ? (i.decode ? i.decode().catch(() => {}) : 0)
                                                   : new Promise(r => { i.addEventListener('load', r); i.addEventListener('error', r); })));
  await Promise.race([all, new Promise(r => setTimeout(r, 4000))]);
}"""


def settle(pg):
    pg.wait_for_timeout(150)
    pg.evaluate(SETTLE_JS)


def shot(name, pg=None, clip_selector=None):
    pg = pg or CURRENT[0]
    settle(pg)
    _n[0] += 1
    os.makedirs(SHOTS, exist_ok=True)
    fn = f"{_n[0]:02d}-{name}.png"
    kw = {"full_page": True}
    if clip_selector:
        box = pg.eval_on_selector(clip_selector, "e => { const r = e.getBoundingClientRect(); return {b: r.bottom + scrollY}; }")
        kw["clip"] = {"x": 0, "y": 0, "width": pg.viewport_size["width"], "height": min(box["b"] + 16, 4000)}
    pg.screenshot(path=os.path.join(SHOTS, fn), **kw)
    print("shot", fn, flush=True)


OVERFLOW_JS = """() => { const vw = document.documentElement.clientWidth, bad = [];
  for (const el of document.querySelectorAll('body *')) { const r = el.getBoundingClientRect();
    if (r.width > 0 && r.height > 0 && (r.right > vw + 1 || r.left < -1)) {
      let p = el.parentElement, clipped = false;
      while (p && p !== document.body) { const o = getComputedStyle(p).overflowX; if (o !== 'visible') { clipped = true; break; } p = p.parentElement; }
      if (!clipped && getComputedStyle(el).position !== 'fixed') bad.push(el.tagName + '.' + String(el.className).slice(0, 30));
    } }
  return {sw: document.documentElement.scrollWidth, vw, bad: bad.slice(0, 5)}; }"""


def no_overflow(pg, label):
    r = pg.evaluate(OVERFLOW_JS)
    check(f"{label}: no horizontal overflow ({r['sw']} <= {r['vw']})", r["sw"] <= r["vw"], r)


IMG_JS = """sel => { const i = document.querySelector(sel); if (!i) return null; const r = i.getBoundingClientRect();
  const s = getComputedStyle(i);
  return {src: i.currentSrc || i.src, nw: i.naturalWidth, nh: i.naturalHeight, w: r.width, h: r.height, l: r.left, r: r.right,
          vw: document.documentElement.clientWidth, shown: s.display !== 'none' && s.visibility !== 'hidden' && r.width > 0}; }"""


def img(pg, sel):
    return pg.evaluate(IMG_JS, sel)


def logo_ok(pg, sel, size, label):
    i = img(pg, sel)
    check(f"{label}: logo loaded ({sel})", bool(i) and i["nw"] > 0 and i["src"].endswith("/img/logo.png"), i)
    if size:
        check(f"{label}: logo box {size}px", abs(i["w"] - size) <= 1 and abs(i["h"] - size) <= 1, i)
    check(f"{label}: logo inside viewport", i["l"] >= 0 and i["r"] <= i["vw"], i)


def favicon_ok(pg, label):
    r = pg.evaluate("""async () => { const l = document.querySelector('link[rel="icon"]'); if (!l) return null;
        const res = await fetch(l.href); return {href: l.getAttribute('href'), status: res.status, type: res.headers.get('content-type')}; }""")
    check(f"{label}: favicon link resolves", bool(r) and r["status"] == 200 and "icon" in (r["type"] or ""), r)


def at(pg, width, height=900):
    pg.set_viewport_size({"width": width, "height": height})


with sync_playwright() as p:
    browser = p.chromium.launch()

    # ---------- A. assets ----------
    import urllib.request
    import urllib.error
    import json as _json
    for url, ctype in [("/img/logo.png", "image/png"), ("/img/hero.png", "image/png"), ("/img/hero-768.png", "image/png"),
                       ("/img/hero.webp", "image/webp"), ("/img/hero-768.webp", "image/webp"),
                       ("/img/hero-mobile.webp", "image/webp"), ("/img/hero-mobile.png", "image/png"),
                       ("/favicon.ico", "image/x-icon"), ("/img/apple-touch-icon.png", "image/png")]:
        with urllib.request.urlopen(BASE + url) as r:
            body = r.read()
            check(f"asset {url} served as {ctype} ({len(body):,} B)", r.status == 200 and r.headers.get_content_type() == ctype and len(body) > 500)
    from PIL import Image as _Image
    import io as _io
    CANVAS = (247, 246, 242)  # --canvas #F7F6F2
    near = lambda px, ref=CANVAS, tol=3: all(abs(int(a) - b) <= tol for a, b in zip(px[:3], ref))
    for url, size in (("/img/hero.webp", (1536, 1024)), ("/img/hero-768.webp", (768, 512)), ("/img/hero-mobile.webp", (800, 1093)),
                      ("/img/hero.png", (1536, 1024)), ("/img/hero-768.png", (768, 512)), ("/img/hero-mobile.png", (800, 1093))):
        im = _Image.open(_io.BytesIO(urllib.request.urlopen(BASE + url).read()))
        corners = [im.getpixel(xy) for xy in ((2, 2), (im.width - 3, 2), (2, im.height - 3), (im.width - 3, im.height - 3))]
        check(f"{url}: transparent cut-out {im.size} (RGBA, corners fully transparent)",
              im.mode == "RGBA" and im.size == size and all(c[3] == 0 for c in corners), (im.mode, im.size, corners))
        a = im.getchannel("A"); solid = sum(1 for v in a.getdata() if v == 255) / (im.width * im.height)
        check(f"{url}: products are solid ({solid:.0%} fully opaque) — no see-through art", solid > 0.25, solid)
    for url in ("/img/hero.jpg", "/img/hero-768.jpg"):
        try:
            st = urllib.request.urlopen(BASE + url).status
        except urllib.error.HTTPError as e:
            st = e.code
        check(f"{url}: flattened JPG fallback removed (404) — the fallback is the RGBA PNG", st == 404, st)
    full = _Image.open(_io.BytesIO(urllib.request.urlopen(BASE + "/img/hero.webp").read()))
    # The owner's desktop art (image-2.jpeg) has a lavender ring behind the products; the cut-out keeps it
    # (an earlier upload had lost its colour and showed it as a black swoosh).
    lav = sum(1 for (r, g, b, al) in full.getdata() if al > 128 and b > 200 and b - r > 18 and b - g > 18 and r > 150)
    dark = sum(1 for (r, g, b, al) in full.getdata() if al > 200 and max(r, g, b) < 20)
    check(f"hero art keeps its lavender ring ({lav:,} px) and has no black swoosh ({dark:,} near-black px)", lav > 2000 and dark < 30000, (lav, dark))
    man = _json.loads(urllib.request.urlopen(BASE + "/site.webmanifest").read())
    check("web manifest icons resolve", all(urllib.request.urlopen(BASE + i["src"]).status == 200 for i in man["icons"]))

    # ---------- B. home at 10 widths ----------
    home = use(browser.new_page(viewport={"width": 320, "height": 900}))
    for w in WIDTHS:
        at(home, w)
        goto(home, "/")
        settle(home)
        lab = f"home @{w}px"
        no_overflow(home, lab)
        h = img(home, ".hm-hero-img img")
        file = h["src"].rsplit("/", 1)[-1]
        check(f"{lab}: transparent hero cut-out served ({file}; portrait <=720 px)",
              file == "hero-mobile.webp" if w <= 720 else file in ("hero.webp", "hero-768.webp"), h)
        art = 800 / 1093 if w <= 720 else 1536 / 1024
        check(f"{lab}: hero shown whole, not cropped/stretched (ratio {h['w'] / h['h']:.3f} vs {art:.3f})",
              abs(h["w"] / h["h"] - art) / art < 0.015, h)
        check(f"{lab}: hero inside viewport and decoded", h["l"] >= 0 and h["r"] <= h["vw"] and h["nw"] > 0, h)
        box = home.eval_on_selector(".hm-hero-img", """e => { const s = getComputedStyle(e);
            return {bg: s.backgroundColor, imgBg: getComputedStyle(e.querySelector('img')).backgroundColor, shadow: s.boxShadow, radius: s.borderTopLeftRadius,
                    page: getComputedStyle(document.querySelector('.hm-hero')).backgroundColor,
                    body: getComputedStyle(document.body).backgroundColor}; }""")
        check(f"{lab}: no card around the hero (transparent, no shadow, no radius)",
              box["bg"] in ("rgba(0, 0, 0, 0)", "transparent") and box["imgBg"] in ("rgba(0, 0, 0, 0)", "transparent") and box["shadow"] == "none" and box["radius"] == "0px", box)
        edge = _Image.open(_io.BytesIO(home.locator(".hm-hero-img img").screenshot())).convert("RGB")
        ew, eh = edge.size
        # Right-hand and top edges; points under the kicker are skipped (on phones it deliberately runs over the
        # art's transparent top, as in the reference).
        kick = home.evaluate("""() => { const i = document.querySelector('.hm-hero-img img').getBoundingClientRect(),
            k = document.querySelector('.hm-hero .hm-kicker').getBoundingClientRect();
            return [k.left - i.left - 4, k.top - i.top - 4, k.right - i.left + 4, k.bottom - i.top + 4]; }""")
        pts = [(x, y) for x, y in ((ew - 2, 1), (ew * 3 // 4, 1), (ew // 2, 1), (ew - 2, eh - 2))
               if not (kick[0] <= x <= kick[2] and kick[1] <= y <= kick[3])]
        samples = [edge.getpixel(pt) for pt in pts]
        check(f"{lab}: hero's transparent edges ARE the page background (no box, {samples[0]} vs page {box['body']})",
              box["body"] == "rgb(247, 246, 242)" and len(samples) >= 3 and all(near(s, tol=4) for s in samples), samples)
        logo_ok(home, ".cs-top .cs-logo img", 38, lab)
        shot(f"home-hero-{w}px", home, ".hm-hero")
    favicon_ok(home, "customer layout")

    # ---------- C. high-DPI ----------
    # <=720 px: the single portrait file at any DPR; above: sizes = 72vw (721-1023) / 74vw / 880px (>=1288).
    for w, dpr, want in [(375, 1, "hero-mobile.webp"), (390, 3, "hero-mobile.webp"), (768, 1, "hero-768.webp"), (768, 2, "hero.webp"),
                         (1024, 1, "hero-768.webp"), (1440, 1, "hero.webp"), (1440, 2, "hero.webp")]:
        ctx = browser.new_context(viewport={"width": w, "height": 900}, device_scale_factor=dpr)
        pg = ctx.new_page()
        goto(pg, "/")
        settle(pg)
        src = img(pg, ".hm-hero-img img")["src"].rsplit("/", 1)[-1]
        check(f"@{w}px DPR{dpr}: browser picks {want}", src == want, src)
        ctx.close()

    # ---------- D. footer, auth (+ error state), 404 ----------
    at(home, 375)
    goto(home, "/")
    home.eval_on_selector(".cs-foot", "e => e.scrollIntoView()")
    logo_ok(home, ".cs-foot .cs-logo img", 38, "footer @375px")
    settle(home)
    home.eval_on_selector(".cs-foot", "e => e.scrollIntoView()")
    _n[0] += 1
    home.locator(".cs-foot").screenshot(path=os.path.join(SHOTS, f"{_n[0]:02d}-footer-logo-375px.png"))
    print("shot", f"{_n[0]:02d}-footer-logo-375px.png", flush=True)

    auth = use(browser.new_page(viewport={"width": 320, "height": 800}))
    for w in (320, 1440):
        at(auth, w)
        goto(auth, "/Account/Login")
        no_overflow(auth, f"login @{w}px")
        logo_ok(auth, ".au-top .cs-logo img", 38, f"login @{w}px")
    favicon_ok(auth, "auth layout")
    at(auth, 320)
    goto(auth, "/Account/Login")
    auth.fill('form[action*="Login"] input[name="Email"]', "nobody@example.com")
    auth.fill('form[action*="Login"] input[name="Password"]', "wrong-password-1")
    auth.click('form[action*="Login"] button[type="submit"]')
    auth.wait_for_timeout(2000)
    err = auth.query_selector(".alert-danger, .validation-summary-errors, .field-validation-error, [role=alert]")
    check("login @320px invalid credentials: error shown", err is not None and err.inner_text().strip() != "", auth.url)
    no_overflow(auth, "login @320px with error")
    logo_ok(auth, ".au-top .cs-logo img", 38, "login @320px with error")
    shot("login-invalid-credentials-320px", auth)
    at(auth, 1440)
    goto(auth, "/Account/Login")
    shot("login-1440px", auth)

    at(auth, 360)
    goto(auth, f"/no-such-page-{TAG}")
    check("404 @360px: error page rendered", "404" in auth.inner_text("body"))
    no_overflow(auth, "404 @360px")
    logo_ok(auth, ".cs-top .cs-logo img", 38, "404 @360px")
    shot("error-404-360px", auth)

    # ---------- E. customer portal ----------
    cctx = browser.new_context(viewport={"width": 1366, "height": 900})
    cust = use(cctx.new_page())
    register_customer(cust, "br")
    for w in (320, 768, 1440):
        at(cust, w)
        for path in ("/portal", "/portal/shop", "/portal/orders", "/portal/profile"):
            goto(cust, path)
            no_overflow(cust, f"portal {path} @{w}px")
        goto(cust, "/portal")
        logo_ok(cust, ".cs-top .cs-logo img", 38, f"portal @{w}px")
        if w != 768:
            shot(f"customer-portal-{w}px", cust)

    # ---------- F. admin ----------
    actx = browser.new_context(viewport={"width": 1440, "height": 900})
    adm = use(actx.new_page())
    owner_login(adm)
    at(adm, 1440)
    goto(adm, "/admin/dashboard")
    logo_ok(adm, ".sidebar-brand img", None, "admin sidebar @1440px")
    no_overflow(adm, "admin dashboard @1440px")
    favicon_ok(adm, "admin layout")
    shot("admin-dashboard-sidebar-logo-1440px", adm)
    for w in (320, 768):
        at(adm, w, 800)
        for a in ADMIN:
            goto(adm, "/admin/" + a)
            no_overflow(adm, f"admin /admin/{a} @{w}px")
    # detail pages with real data (created by the checkout regression)
    at(adm, 320, 800)
    for listing, pat in (("/admin/orders", r"/admin/orders/[0-9a-f-]{36}"), ("/admin/customers", r"/admin/customers/[0-9a-f-]{36}"),
                         ("/admin/invoices", r"/admin/invoices/[0-9a-f-]{36}")):
        goto(adm, listing)
        mm = re.search(r'href="(' + pat + r')"', adm.content())
        check(f"admin {listing}: a detail page exists", mm is not None)
        goto(adm, mm.group(1))
        no_overflow(adm, f"admin {listing}/<id> details @320px")
    goto(adm, "/admin/products")
    f = adm.evaluate("""() => { const f = document.querySelector('.content form:has(> input[type=search])');
        const i = f.querySelector('input').getBoundingClientRect(), b = f.querySelector('button').getBoundingClientRect();
        return {same: Math.abs(i.top - b.top) < 8, right: b.right}; }""")
    check("admin products @320px: search box and button share a row inside the screen", f["same"] and f["right"] <= 320, f)
    goto(adm, "/admin/dashboard")
    tt = adm.evaluate("() => { const t = document.querySelector('.topbar-title'); const r = t.getBoundingClientRect(), a = document.querySelector('.topbar-actions').getBoundingClientRect(); return {titleRight: r.right, actionsLeft: a.left, text: t.textContent.trim()}; }")
    check("admin topbar @320px: page title no longer slides under the avatar", tt["titleRight"] <= tt["actionsLeft"] + 1, tt)
    goto(adm, "/admin/dashboard")
    logo_ok(adm, ".topbar-brand img", None, "admin topbar @320px")
    t = img(adm, ".topbar-brand img")
    check("admin topbar @320px: logo visible and fits its 32px badge", t["shown"] and t["w"] <= 32, t)
    shot("admin-dashboard-topbar-logo-320px", adm)
    adm.click("#menuToggle")
    adm.wait_for_timeout(600)
    s = img(adm, ".sidebar-brand img")
    check("admin @320px: off-canvas menu shows the logo", s["shown"] and s["nw"] > 0 and s["l"] >= 0, s)
    shot("admin-menu-open-logo-320px", adm)

    # ---------- G. shared invoice document ----------
    at(adm, 1440)
    goto(adm, "/admin/invoices")
    link = adm.query_selector('table a[href*="/admin/invoices/"]')
    check("an invoice exists (created by the checkout regression)", link is not None)
    goto(adm, link.get_attribute("href"))
    html = adm.content()
    m = re.search(r'(https?://[^"\s]+?/i/[a-f0-9]{32})', html)
    if not m:
        adm.query_selector('form[action$="/share"] button[type="submit"]').click()
        adm.wait_for_timeout(1500)
        m = re.search(r'(https?://[^"\s]+?/i/[a-f0-9]{32})', adm.content())
    check("invoice share link available", m is not None)
    share_path = "/i/" + m.group(1).rsplit("/i/", 1)[1]
    anon = use(browser.new_page(viewport={"width": 320, "height": 900}))
    for w in (320, 1440):
        at(anon, w)
        goto(anon, share_path)
        logo_ok(anon, ".doc-print .doc-logo", 44, f"shared invoice @{w}px")
        no_overflow(anon, f"shared invoice @{w}px")
        d = anon.evaluate("""() => { const d = document.querySelector('.doc-print'), a = document.querySelector('.bar a.ghost');
            return {fits: d.scrollWidth <= d.clientWidth + 1, sw: d.scrollWidth, cw: d.clientWidth, link: getComputedStyle(a).color}; }""")
        check(f"shared invoice @{w}px: line items fit inside the document (no clipped Amount column)", d["fits"], d)
        check(f"shared invoice @{w}px: bar links readable (white on dark)", d["link"] == "rgb(255, 255, 255)", d)
        shot(f"shared-invoice-logo-{w}px", anon)
    favicon_ok(anon, "shared invoice")

    # ---------- H. developer portal ----------
    dctx = browser.new_context(viewport={"width": 1440, "height": 900})
    dev = use(dctx.new_page())
    goto(dev, "/Account/Login")
    dev.fill('form[action*="Login"] input[name="Email"]', "developer@dechimexprinthub.local")
    dev.fill('form[action*="Login"] input[name="Password"]', "DevPortal#2026!")
    dev.click('form[action*="Login"] button[type="submit"]')
    dev.wait_for_timeout(2500)
    check("developer signed in", "/dev" in dev.url, dev.url)
    for w in (375, 1024, 1440):
        at(dev, w)
        for path in (DEV if w != 1440 else ["/dev"]):
            goto(dev, path)
            no_overflow(dev, f"developer {path} @{w}px")
    favicon_ok(dev, "developer layout")
    at(dev, 375)
    goto(dev, "/dev/email")
    nav = dev.evaluate("""() => { const n = document.querySelector('.dv-nav'), a = n.querySelector('a.is-active');
        const nr = n.getBoundingClientRect(), ar = a.getBoundingClientRect(), main = document.querySelector('#dv-main').getBoundingClientRect();
        return {row: getComputedStyle(n).flexDirection, scrolls: n.scrollWidth > n.clientWidth, activeVisible: ar.left >= nr.left - 1 && ar.right <= nr.right + 1,
                mainTop: Math.round(main.top + scrollY)}; }""")
    check("developer @375px: nav is a horizontal strip (no 850px sidebar stack)", nav["row"] == "row" and nav["scrolls"] and nav["mainTop"] < 260, nav)
    check("developer @375px: current page's nav item scrolled into view", nav["activeVisible"], nav)
    shot("developer-portal-email-375px", dev)
    at(dev, 1440)
    goto(dev, "/dev")
    check("developer @1440px: sidebar layout unchanged", dev.evaluate("() => getComputedStyle(document.querySelector('.dv-nav')).flexDirection") == "column")

    print(f"\nRESULT: {len(CHECKS)} passed, 0 failed (TAG={TAG})", flush=True)
    browser.close()
