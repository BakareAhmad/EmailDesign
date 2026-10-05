"""Hero restore: the pre-refactor composition on the current architecture, with true-alpha art — E2E.

Owner request (2026-09-27): restore the OLD hero's text, image composition and responsive behaviour
(reference screenshots at 8 viewports, e2e/hero/reference/) without reverting the refactor. The art must
be a true-alpha cut-out (no white or solid rectangle, no baked page background), stacked
page background -> art -> text. The hero search box was removed to match the reference, so this suite
also proves the header search still works.

Real Chromium against the running app (in-memory DB, Brevo + Paystack stubs):
  A. served art: hero(-768).webp/png + hero-mobile.webp/png are RGBA with transparent corners, soft
     alpha edges (the subject's own shadows), solid products, the lavender ring kept; the old JPGs are 404.
  B. home at 9 widths (320 + the 8 reference viewports):
     - no horizontal scroll;
     - two columns (copy left, art right);
     - the right art file for the breakpoint, shown whole and not clipped;
     - H1 on 3 lines, the kicker inside the viewport;
     - CTAs inside the copy column with no text overflow, stacked or in a row as in the reference;
     - metrics as stacked cards (<=720) or one band (>=721);
     - NO box behind the art: every fully transparent art pixel shows exactly the page colour, as do the
       pixels just outside the image box;
     - text above the art (z-order), no broken images;
     - side-by-side "reference | current" evidence image per reference viewport.
  C. PNG fallback (browser without WebP, simulated by dropping the WebP <source>s): PNGs load and stay transparent.
  D. interactions:
     - CTAs by real mouse clicks (the art never intercepts them);
     - keyboard focus reaches both CTAs;
     - header navigation;
     - header search on desktop (the box: a product seeded through the admin UI is found and opens, a
       no-match term shows the empty state, empty input lists the catalogue, script-like input is escaped)
       and on mobile (the icon opens the panel, then the seeded product is found);
     - signed-in customer: CTAs and header search target /portal/shop.
  E. no console errors, page errors, failed requests or 4xx/5xx sub-resources on any page visited.
Env: E2E_SHOTS_DIR. Screenshots auto-numbered NN-name.png.
"""
import io
import os
import re
import time
import urllib.error
import urllib.request

from PIL import Image, ImageChops, ImageDraw, ImageFilter

T0 = time.time()

HERE = os.path.dirname(os.path.abspath(__file__))
_src = open(os.path.join(HERE, "../design-review/ws_designreview_e2e.py")).read()
exec(compile(_src.split("with sync_playwright() as p:")[0], "ws_designreview_helpers", "exec"))  # helpers only

SHOTS = os.environ.get("E2E_SHOTS_DIR", os.path.abspath(os.path.join(HERE, "../../evidence")))
REF_DIR = os.path.join(HERE, "reference")
CANVAS = (247, 246, 242)  # --canvas #F7F6F2
# (width, height, reference name or None)
VIEWPORTS = [(320, 640, None), (360, 740, "mobile-compact"), (390, 844, "mobile-standard"), (640, 800, "mobile-large"),
             (768, 1024, "tablet-portrait"), (1024, 768, "tablet-landscape"), (1200, 800, "laptop"),
             (1440, 900, "desktop"), (1920, 1080, "desktop-xl")]
_n = [0]
PROBLEMS = []  # console errors / page errors / failed or 4xx-5xx sub-resources, across every page

SETTLE_JS = """async () => {
  const imgs = [...document.images].filter(i => i.getBoundingClientRect().width > 0 && (i.complete || i.loading !== 'lazy'));
  const all = Promise.all(imgs.map(i => i.complete ? (i.decode ? i.decode().catch(() => {}) : 0)
                                                   : new Promise(r => { i.addEventListener('load', r); i.addEventListener('error', r); })));
  await Promise.race([all, new Promise(r => setTimeout(r, 4000))]);
}"""

# Everything the layout checks need, in page (document) coordinates.
LAYOUT_JS = """() => {
  const q = s => document.querySelector(s), R = e => { const r = e.getBoundingClientRect();
    return {l: r.left + scrollX, t: r.top + scrollY, r: r.right + scrollX, b: r.bottom + scrollY, w: r.width, h: r.height}; };
  const pic = q('.hm-hero-img'), img = pic && pic.querySelector('img'), copy = q('.hm-copy'), grid = q('.hm-hero-grid');
  const clippers = []; for (let e = img && img.parentElement; e && e !== document.body; e = e.parentElement) {
    const s = getComputedStyle(e); if (s.overflowX !== 'visible' || s.overflowY !== 'visible') clippers.push(R(e)); }
  const stats = [...document.querySelectorAll('.hm-stats li')];
  return {
    vw: document.documentElement.clientWidth, sw: document.documentElement.scrollWidth, body: getComputedStyle(document.body).backgroundColor,
    cols: getComputedStyle(grid).gridTemplateColumns.trim().split(/\\s+/).length,
    copy: R(copy), copyZ: getComputedStyle(copy).zIndex, visual: R(q('.hm-visual')),
    img: img && Object.assign(R(img), {src: img.currentSrc || img.src, nw: img.naturalWidth, nh: img.naturalHeight,
         picBg: getComputedStyle(pic).backgroundColor, imgBg: getComputedStyle(img).backgroundColor,
         shadow: getComputedStyle(pic).boxShadow, radius: getComputedStyle(pic).borderTopLeftRadius, pe: getComputedStyle(pic).pointerEvents}),
    clippers,
    kicker: R(q('.hm-hero .hm-kicker')),
    lines: [...document.querySelectorAll('.hm-title > *')].map(e => Math.round(e.getBoundingClientRect().height / parseFloat(getComputedStyle(e).lineHeight) * 10) / 10),
    text: [...copy.children].map(R),
    btns: [...document.querySelectorAll('.hm-cta .btn')].map(e => Object.assign(R(e), {over: e.scrollWidth - e.clientWidth, text: e.innerText.trim()})),
    stats: stats.map(e => Object.assign(R(e), {shadow: getComputedStyle(e).boxShadow, bl: getComputedStyle(e).borderLeftWidth})),
    statsBg: stats.length ? getComputedStyle(q('.hm-stats')).backgroundColor : null,
    broken: [...document.images].filter(i => i.complete && i.naturalWidth === 0 && i.currentSrc).map(i => i.currentSrc),
  }; }"""


def pixels(im):
    """Flat pixel sequence (Pillow >= 11.3 renamed getdata -> get_flattened_data)."""
    return im.get_flattened_data() if hasattr(im, "get_flattened_data") else im.getdata()


def settle(pg):
    pg.wait_for_timeout(150)
    pg.evaluate(SETTLE_JS)


def _num(name):
    _n[0] += 1
    os.makedirs(SHOTS, exist_ok=True)
    return os.path.join(SHOTS, f"{_n[0]:02d}-{name}.png")


def shot(name, pg=None, full=False):  # noqa: F811 — auto-numbered variant
    pg = pg or CURRENT[0]
    settle(pg)
    fn = _num(name)
    pg.screenshot(path=fn, full_page=full)
    print("shot", os.path.basename(fn), flush=True)


def _request_failed(r):
    """A failed request, tagged with the page it came from so the source of an abort is traceable."""
    try:
        where = r.frame.page.url
    except Exception:  # noqa: BLE001 - the frame can disappear while the event is handled
        where = "?"
    msg = f"requestfailed: {r.url} {r.failure} @ {where}"
    PROBLEMS.append(msg)
    print(f"[t+{time.time() - T0:6.1f}s] {msg}", flush=True)  # immediate, next to the ok/shot markers


def watch(pg):
    """Record console errors, uncaught page errors, failed requests and 4xx/5xx sub-resources."""
    pg.on("console", lambda m: m.type == "error" and PROBLEMS.append(f"console: {m.text} @ {pg.url}"))
    pg.on("pageerror", lambda e: PROBLEMS.append(f"pageerror: {e} @ {pg.url}"))
    pg.on("requestfailed", _request_failed)
    pg.on("response", lambda r: r.status >= 400 and r.request.resource_type != "document"
          and PROBLEMS.append(f"HTTP {r.status}: {r.url}"))
    return pg


def centre(pg, sel):
    """Viewport centre of an element (scrolled into view) for a real mouse click: if anything, e.g. the art, sat
    on top of it the click would land there instead and the navigation assertion would fail."""
    pg.locator(sel).scroll_into_view_if_needed()
    return pg.eval_on_selector(sel, "e => { const r = e.getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; }")


def near(px, ref=CANVAS, tol=4):
    return all(abs(int(a) - b) <= tol for a, b in zip(px[:3], ref))


def fetch(url):
    try:
        with urllib.request.urlopen(BASE + url) as r:
            return r.status, r.headers.get_content_type(), r.read()
    except urllib.error.HTTPError as e:
        return e.code, None, b""


_ALPHA = {}


def art_alpha(src):
    """Alpha channel of the art file the browser actually rendered (fetched from the app)."""
    path = "/" + src.split("/", 3)[3]
    if path not in _ALPHA:
        _ALPHA[path] = Image.open(io.BytesIO(fetch(path)[2])).getchannel("A")
    return _ALPHA[path]


def page_clip(pg, box):
    """Screenshot a document-coordinate box at scroll 0 (full-page capture, so the sticky header never covers it)."""
    pg.evaluate("() => scrollTo(0, 0)")
    clip = {"x": box[0], "y": box[1], "width": box[2] - box[0], "height": box[3] - box[1]}
    return Image.open(io.BytesIO(pg.screenshot(clip=clip, full_page=True))).convert("RGB")


def no_box_check(pg, lab):
    """Every fully transparent pixel of the art (shrunk 4 px away from the subject, and away from the copy that
    sits above the art) must show exactly the page colour; so must the pixels just outside the image box. A grey
    <img> placeholder, a baked white/solid background, a card or a halo would all fail this."""
    L = pg.evaluate(LAYOUT_JS)
    i = L["img"]
    box = (round(i["l"]), round(i["t"]), round(i["l"]) + round(i["w"]), round(i["t"]) + round(i["h"]))
    im = page_clip(pg, box)
    alpha = art_alpha(i["src"]).resize(im.size, Image.BILINEAR)
    clear = alpha.point(lambda a: 255 if a == 0 else 0).filter(ImageFilter.MinFilter(9))  # transparent, >=4 px from any subject pixel
    d = ImageDraw.Draw(clear)
    for t in L["text"]:  # copy (kicker, H1, lead, CTAs...) intentionally sits above the art's transparent margin
        d.rectangle([t["l"] - box[0] - 4, t["t"] - box[1] - 4, t["r"] - box[0] + 4, t["b"] - box[1] + 4], fill=0)
    diff = ImageChops.difference(im, Image.new("RGB", im.size, CANVAS)).convert("L").point(lambda v: 255 if v > 4 else 0)
    bad = ImageChops.multiply(diff, clear)
    n_clear = sum(1 for v in pixels(clear) if v)
    n_bad = sum(1 for v in pixels(bad) if v)
    check(f"{lab}: no box/halo behind the art — {n_clear:,} transparent-art pixels checked, {n_bad} differ from the page colour #F7F6F2",
          n_clear > 2000 and n_bad <= n_clear * 0.001, (n_clear, n_bad))
    # just outside the image box: 6 px above and below (and right of it at <=720, where the art has a gutter)
    pts = []
    for f in (0.2, 0.4, 0.6, 0.8, 0.95):
        x = i["l"] + i["w"] * f
        pts += [(x, i["t"] - 6), (x, i["b"] + 6)]
    pts = [p for p in pts if 0 <= p[0] < L["vw"] and not any(t["l"] - 4 <= p[0] <= t["r"] + 4 and t["t"] - 4 <= p[1] <= t["b"] + 4 for t in L["text"])]
    if L["vw"] <= 720:
        pts += [(min(L["vw"] - 2, i["r"] + 6), i["t"] + i["h"] * f) for f in (0.3, 0.6)]
    ring = page_clip(pg, (0, max(0, round(i["t"]) - 10), L["vw"], round(i["b"]) + 10))
    px = [ring.getpixel((int(x), int(y - max(0, round(i["t"]) - 10)))) for x, y in pts]
    check(f"{lab}: page colour just outside the image box ({len(px)} points, e.g. {px[0]}) — no card edge",
          len(px) >= 4 and all(near(p) for p in px), px)
    return L


def layout_checks(pg, w, lab):
    L = pg.evaluate(LAYOUT_JS)
    i, c, v, k = L["img"], L["copy"], L["visual"], L["kicker"]
    check(f"{lab}: no horizontal scroll ({L['sw']} <= {L['vw']}), page background {L['body']}",
          L["sw"] <= L["vw"] and L["body"] == "rgb(247, 246, 242)", L)
    check(f"{lab}: two columns — copy left ({c['l']:.0f}-{c['r']:.0f}), art right ({v['l']:.0f}-{v['r']:.0f}), side by side",
          L["cols"] == 2 and c["l"] < v["l"] and v["t"] < c["b"] and c["t"] < v["b"] and (c["l"] + c["r"]) < (v["l"] + v["r"]), L)
    file = i["src"].rsplit("/", 1)[-1]
    want = ("hero-mobile.webp",) if w <= 720 else ("hero.webp", "hero-768.webp")
    ratio = 800 / 1093 if w <= 720 else 1.5
    check(f"{lab}: {file} served (portrait <=720, landscape above), decoded {i['nw']}x{i['nh']}",
          file in want and i["nw"] > 0, i)
    check(f"{lab}: art shown whole — ratio {i['w'] / i['h']:.3f} == {ratio:.3f}, inside the viewport ({i['l']:.0f}-{i['r']:.0f}), no clipping ancestor",
          abs(i["w"] / i["h"] - ratio) < 0.01 and i["l"] >= 0 and i["r"] <= L["vw"] + 0.5
          and all(cl["l"] <= i["l"] + .5 and cl["r"] >= i["r"] - .5 and cl["t"] <= i["t"] + .5 and cl["b"] >= i["b"] - .5 for cl in L["clippers"]), L)
    check(f"{lab}: art has no box styling (transparent picture + img backgrounds, no shadow, no radius) and never takes clicks",
          i["picBg"] in ("rgba(0, 0, 0, 0)", "transparent") and i["imgBg"] in ("rgba(0, 0, 0, 0)", "transparent")
          and i["shadow"] == "none" and i["radius"] == "0px" and i["pe"] == "none", i)
    check(f"{lab}: text stacks above the art (copy z-index {L['copyZ']})", L["copyZ"] == "1", L["copyZ"])
    check(f"{lab}: H1 'Your brand, / beautifully / produced.' on exactly 3 lines {L['lines']}", L["lines"] == [1, 1, 1], L["lines"])
    check(f"{lab}: kicker inside the viewport ({k['l']:.0f}-{k['r']:.0f} of {L['vw']})", k["l"] >= 8 and k["r"] <= L["vw"] - 8, k)
    b1, b2 = L["btns"]
    check(f"{lab}: CTAs '{b1['text']}' / '{b2['text']}' inside the copy column, no text overflow",
          all(b["l"] >= c["l"] - .5 and b["r"] <= c["r"] + .5 and b["over"] <= 0 for b in L["btns"]), L["btns"])
    if w <= 720:
        check(f"{lab}: portrait art positioned below kicker pill (art top {i['t']:.0f} >= kicker bottom {k['b']:.0f})",
              i["t"] >= k["b"] - 2, (i, k))
        if w >= 520:
            check(f"{lab}: large-mobile portrait art capped ({i['w']:.0f}px <= 246px) with right breathing room ({L['vw'] - i['r']:.0f}px)",
                  i["w"] <= 246 and L["vw"] - i["r"] >= 24, i)
        check(f"{lab}: CTAs stacked, full column width ({b1['w']:.0f}px), arrows at the end",
              b2["t"] >= b1["b"] and abs(b1["w"] - c["w"]) < 1 and abs(b2["w"] - c["w"]) < 1, L["btns"])
    elif w < 1400:
        check(f"{lab}: landscape art respects container right gutter ({L['vw'] - i['r']:.0f}px >= 14px)",
              L["vw"] - i["r"] >= 14, i)
        check(f"{lab}: CTAs stacked as in the 768/1024/1200 references", b2["t"] >= b1["b"] and abs(b1["l"] - b2["l"]) < 1, L["btns"])
    else:
        check(f"{lab}: landscape art respects container right gutter ({L['vw'] - i['r']:.0f}px >= 14px)",
              L["vw"] - i["r"] >= 14, i)
        check(f"{lab}: CTAs side by side as in the 1440/1920 references", abs(b1["t"] - b2["t"]) < 1 and b2["l"] > b1["r"], L["btns"])
    s = L["stats"]
    if w <= 720:
        check(f"{lab}: 3 metrics as stacked shadow cards", len(s) == 3 and s[0]["b"] <= s[1]["t"] and s[1]["b"] <= s[2]["t"]
              and all(x["shadow"] != "none" for x in s), s)
    else:
        check(f"{lab}: 3 metrics in one white band with dividers, below the art",
              len(s) == 3 and len({round(x["t"]) for x in s}) == 1 and L["statsBg"] == "rgb(255, 255, 255)"
              and s[1]["bl"] == "1px" and s[0]["t"] >= i["b"] - 1, (s, L["statsBg"]))
    check(f"{lab}: no broken images", not L["broken"], L["broken"])
    return L


def reference_evidence(pg, name, w, h):
    """Save "reference | current" for this viewport (the reference is the old hero element screenshot)."""
    hero = pg.eval_on_selector(".hm-hero", "e => { const r = e.getBoundingClientRect(); return [r.left, r.top + scrollY, r.width, r.height]; }")
    bottom = round(hero[1] + hero[3])
    # Taller viewport while capturing so fixed UI (the mobile bottom nav) sits below the hero instead of over it.
    pg.set_viewport_size({"width": w, "height": max(h, bottom + 120)})
    pg.wait_for_timeout(200)
    # The old element shots include the header at >=1200 px only (it overlaid the hero there), so crop the same way.
    cur = page_clip(pg, (0, 0 if w >= 1200 else round(hero[1]), w, bottom))
    pg.set_viewport_size({"width": w, "height": h})
    ref = Image.open(os.path.join(REF_DIR, f"hero-section-{name}-{w}x{h}.jpg")).convert("RGB")
    gap, top = 16, 30
    out = Image.new("RGB", (ref.width + gap + cur.width, max(ref.height, cur.height) + top), (44, 44, 52))
    out.paste(ref, (0, top))
    out.paste(cur, (ref.width + gap, top))
    d = ImageDraw.Draw(out)
    d.text((8, 9), f"REFERENCE (pre-refactor) {name} {w}x{h}", fill=(255, 220, 0))
    d.text((ref.width + gap + 8, 9), f"CURRENT (restored) {w}x{h}", fill=(90, 255, 120))
    fn = _num(f"hero-vs-reference-{w}px")
    out.save(fn)
    print("shot", os.path.basename(fn), flush=True)


with sync_playwright() as p:
    browser = p.chromium.launch()

    # ---------------- A. served art ----------------
    for url, ctype, size in (("/img/hero.webp", "image/webp", (1536, 1024)), ("/img/hero-768.webp", "image/webp", (768, 512)),
                             ("/img/hero-mobile.webp", "image/webp", (800, 1093)), ("/img/hero.png", "image/png", (1536, 1024)),
                             ("/img/hero-768.png", "image/png", (768, 512)), ("/img/hero-mobile.png", "image/png", (800, 1093))):
        st, ct, body = fetch(url)
        im = Image.open(io.BytesIO(body))
        corners = [im.getpixel(xy)[3] if im.mode == "RGBA" else None
                   for xy in ((2, 2), (im.width - 3, 2), (2, im.height - 3), (im.width - 3, im.height - 3))]
        check(f"{url}: 200 {ctype}, {size[0]}x{size[1]} RGBA (true alpha), transparent corners",
              st == 200 and ct == ctype and im.mode == "RGBA" and im.size == size and corners == [0, 0, 0, 0], (st, ct, im.mode, im.size, corners))
    for url in ("/img/hero.webp", "/img/hero-mobile.webp"):
        im = Image.open(io.BytesIO(fetch(url)[2]))
        a = list(pixels(im.getchannel("A")))
        solid, soft = sum(1 for v in a if v == 255) / len(a), sum(1 for v in a if 0 < v < 255) / len(a)
        check(f"{url}: products solid ({solid:.0%} fully opaque), soft alpha edges + the subject's own shadows kept ({soft:.1%} partial alpha), "
              f"background clear ({a.count(0) / len(a):.0%} fully transparent)", solid > 0.25 and soft > 0.01 and a.count(0) / len(a) > 0.2, (solid, soft))
        lav = sum(1 for (r, g, b, al) in pixels(im) if al > 128 and b > 200 and b - r > 18 and b - g > 18 and r > 150)
        check(f"{url}: the lavender ring behind the products is kept ({lav:,} ring-coloured pixels)", lav > 2000, lav)
        edge_px = sum(1 for (r, g, b, al) in pixels(im.crop((0, 0, im.width, 12))) if al > 0)
        check(f"{url}: nothing opaque along the top edge (no baked rectangle / crop line)", edge_px == 0, edge_px)
    for url in ("/img/hero.jpg", "/img/hero-768.jpg"):
        check(f"{url}: flattened JPG fallback removed (404) — a JPG cannot carry transparency", fetch(url)[0] == 404)

    # ---------------- B. home at 9 widths ----------------
    pg = watch(use(browser.new_page(viewport={"width": 320, "height": 640})))
    for w, h, name in VIEWPORTS:
        pg.set_viewport_size({"width": w, "height": h})
        goto(pg, "/")
        settle(pg)
        lab = f"home @{w}px"
        if w == VIEWPORTS[0][0]:
            txt = pg.inner_text(".hm-copy")
            check("hero copy: kicker, H1, lead naming both ordering paths, CTAs (rewritten copy, no filler)",
                  "PRINTING • BRANDING • PROMOTIONAL" in txt and "Your brand,\nbeautifully\nproduced." in txt
                  and "Branded mugs, jerseys, flasks, corporate gifts and print work, produced by DE-CHIMEX PRINT HUB. Order ready-to-buy products online, or send your own items for custom branding." in txt
                  and "Shop products" in txt and "Explore services" in txt
                  and "endless possibilities" not in txt and "turns your ideas into premium" not in txt, txt)
            check("no search box in the hero (search lives in the header)", pg.locator(".hm-hero form, .hm-hero input").count() == 0)
            check("metrics show the owner's figures and labels",
                  [t.strip() for t in pg.locator(".hm-stats li").all_inner_texts()] == ["2,400+\nProjects delivered", "180+\nHappy brands", "48h\nAvg. turnaround"],
                  pg.locator(".hm-stats li").all_inner_texts())
        layout_checks(pg, w, lab)
        no_box_check(pg, lab)
        if name:
            reference_evidence(pg, name, w, h)
        else:
            shot(f"hero-restored-{w}px-viewport", pg)

    # ---------------- B2. responsive checkpoints & breakpoint boundaries ----------------
    for w, h in ((375, 812), (414, 896), (430, 932), (720, 900), (721, 900),
                 (767, 1024), (769, 1024), (900, 800), (1023, 768), (1025, 768),
                 (1280, 800), (1366, 768), (1399, 900), (1400, 900)):
        pg.set_viewport_size({"width": w, "height": h})
        goto(pg, "/")
        settle(pg)
        L = layout_checks(pg, w, f"checkpoint @{w}x{h}")
        if (w, h) == (1366, 768):
            check("checkpoint @1366x768: 3-column metrics band fits above the 768px laptop fold",
                  L["stats"][0]["b"] <= 768, L["stats"])

    # ---------------- C. PNG fallback ----------------
    # A browser without WebP is simulated at the source: the home document is served with the
    # picture's WebP <source>s removed, exactly the markup such a browser would receive, so the
    # WebP candidate never exists. Stripping the <source>s from a live page instead makes
    # Chromium re-select the picture mid-flight and cancel its own WebP request
    # (net::ERR_ABORTED), an abort the harness caused, not the app.
    def serve_png_only(route):
        resp = route.fetch()
        route.fulfill(response=resp, body=re.sub(r'<source[^>]*type="image/webp"[^>]*>', "", resp.text()))

    ctx = pg.context
    ctx.route(BASE + "/", serve_png_only)
    for w in (390, 1440):
        pg.set_viewport_size({"width": w, "height": 900})
        goto(pg, "/")
        pg.wait_for_function("() => /\\.png$/.test(document.querySelector('.hm-hero-img img').currentSrc || '')", timeout=8000)
        settle(pg)
        L = pg.evaluate(LAYOUT_JS)
        file = L["img"]["src"].rsplit("/", 1)[-1]
        check(f"PNG fallback @{w}px: browser without WebP loads {file}",
              file == ("hero-mobile.png" if w <= 720 else "hero.png") and L["img"]["nw"] > 0, L["img"])
        check(f"PNG fallback @{w}px: served HTML carries no WebP source", 
              pg.evaluate("() => !document.querySelector('.hm-hero-img source[type=\"image/webp\"]')"))
        no_box_check(pg, f"PNG fallback @{w}px")
    ctx.unroute(BASE + "/", serve_png_only)
    shot("home-hero-png-fallback-1440px", pg)

    # ---------------- D. interactions ----------------
    pg.set_viewport_size({"width": 1440, "height": 900})
    goto(pg, "/")
    shot("home-full-page-1440px", pg, full=True)
    with pg.expect_navigation(wait_until="domcontentloaded", timeout=10000):
        pg.mouse.click(*centre(pg, ".hm-cta a.btn-primary"))
    check("'Shop products' CTA (real mouse click, art never intercepts) opens the catalogue", pg.url.rstrip("/").endswith("/products"), pg.url)
    goto(pg, "/")
    pg.click(".hm-cta a.btn-outline")
    pg.wait_for_load_state("domcontentloaded")
    check("'Explore services' CTA opens services", pg.url.rstrip("/").endswith("/services"), pg.url)
    goto(pg, "/")
    pg.focus(".hm-cta a.btn-primary")
    pg.keyboard.press("Tab")
    check("keyboard: Tab moves from 'Shop products' to 'Explore services'",
          pg.evaluate("() => document.activeElement.textContent.trim()") == "Explore services")
    with pg.expect_navigation(wait_until="domcontentloaded", timeout=10000):
        pg.keyboard.press("Enter")
    check("keyboard: Enter on 'Explore services' navigates", pg.url.rstrip("/").endswith("/services"), pg.url)
    goto(pg, "/")
    pg.locator("header a", has_text="About").first.click()
    pg.wait_for_load_state("domcontentloaded")
    check("header navigation still works from home (About)", "/about" in pg.url.lower(), pg.url)

    # Seed a uniquely named product through the admin UI so the search checks never depend on other suites' data.
    actx = browser.new_context(viewport={"width": 1280, "height": 900})
    admin = watch(use(actx.new_page()))
    owner_login(admin)
    cat_name, prod_name = f"Hero Search {TAG}", f"Hero Mug {TAG}"
    goto(admin, "/admin/categories/create")
    admin.fill('form[action*="create"] input[name="Name"]', cat_name)
    with admin.expect_navigation(wait_until="domcontentloaded", timeout=15000):
        admin.click('form[action*="create"] button[type="submit"]')
    cat_href = admin.locator("tr").filter(has=admin.get_by_text(cat_name, exact=True)).locator('a:has-text("Edit")').get_attribute("href")
    goto(admin, "/admin/products/create")
    form = 'form[action*="create"]'
    admin.fill(f'{form} input[name="Name"]', prod_name)
    admin.fill(f'{form} textarea[name="Description"]', "hero e2e search fixture")
    admin.select_option(f'{form} select[name="CategoryId"]', cat_href.rstrip("/").split("/")[-1])
    admin.fill(f'{form} input[name="SellingPrice"]', "4500")
    admin.fill(f'{form} input[name="InitialQuantity"]', "20")
    admin.set_input_files(f'{form} input[name="Images"]', os.path.join(WIP, "product.png"))
    with admin.expect_navigation(wait_until="domcontentloaded", timeout=15000):
        admin.click(f'{form} button[type="submit"]')
    check(f"search fixture created through the admin UI: '{prod_name}' in '{cat_name}'", prod_name in admin.inner_text("main"), admin.url)
    actx.close()
    use(pg)

    # header search — desktop box
    goto(pg, "/")
    check("desktop: header search box visible, mobile search icon hidden",
          pg.is_visible("#csSearchDesktop") and not pg.is_visible("#csSearchToggle"))
    pg.fill("#csSearchDesktop", prod_name)
    with pg.expect_navigation(wait_until="domcontentloaded", timeout=10000):
        pg.press("#csSearchDesktop", "Enter")
    settle(pg)
    hit = pg.locator("main a[href^='/products/']", has_text=prod_name)
    check(f"desktop header search '{prod_name}': results page lists the product (link {hit.first.get_attribute('href') if hit.count() else None}), term kept in the box",
          "/products" in pg.url and "search=" in pg.url and hit.count() > 0 and pg.input_value("#csSearchDesktop") == prod_name, pg.url)
    shot("header-search-desktop-results", pg)
    with pg.expect_navigation(wait_until="domcontentloaded", timeout=10000):
        hit.first.click()
    check("search result opens the product page", prod_name in pg.inner_text("main") and "/products/" in pg.url, pg.url)
    goto(pg, "/")
    pg.fill("#csSearchDesktop", f"zzqx-nothing-{TAG}")
    with pg.expect_navigation(wait_until="domcontentloaded", timeout=10000):
        pg.press("#csSearchDesktop", "Enter")
    check("desktop header search with no match: friendly 'No matches' empty state (not an error)",
          "No matches" in pg.inner_text("main") and "Something went wrong" not in pg.inner_text("main"), pg.url)
    shot("header-search-desktop-no-match", pg)
    goto(pg, "/")
    pg.click(".cs-search button[type=submit]")
    pg.wait_for_load_state("domcontentloaded")
    check("desktop header search, empty input: lists the catalogue (no error page)",
          "/products" in pg.url and "Something went wrong" not in pg.inner_text("main"), pg.url)
    goto(pg, "/")
    dialogs = []
    pg.on("dialog", lambda dl: (dialogs.append(dl.message), dl.dismiss()))
    pg.fill("#csSearchDesktop", "<script>alert(1)</script>")
    with pg.expect_navigation(wait_until="domcontentloaded", timeout=10000):
        pg.press("#csSearchDesktop", "Enter")
    pg.wait_for_timeout(400)
    check("desktop header search with script-like input: escaped, no script ran, no error page",
          not dialogs and "/products" in pg.url and "Something went wrong" not in pg.inner_text("main"), (dialogs, pg.url))
    shot("header-search-script-input-escaped", pg)

    # header search — mobile icon
    m = watch(use(browser.new_page(viewport={"width": 390, "height": 844})))
    goto(m, "/")
    check("mobile: search icon visible, desktop box hidden, panel closed",
          m.is_visible("#csSearchToggle") and not m.is_visible("#csSearchDesktop") and not m.is_visible("#csMobileSearch"))
    m.click("#csSearchToggle")
    m.wait_for_selector("#csMobileSearch", state="visible", timeout=4000)
    check("mobile: the search icon opens the search panel (aria-expanded=true)", m.get_attribute("#csSearchToggle", "aria-expanded") == "true")
    shot("header-search-mobile-open", m)
    m.fill("#csSearchMobile", prod_name)
    with m.expect_navigation(wait_until="domcontentloaded", timeout=10000):
        m.press("#csSearchMobile", "Enter")
    settle(m)
    check(f"mobile header search '{prod_name}': results page lists the product", "/products" in m.url and "search=" in m.url
          and m.locator("main a[href^='/products/']", has_text=prod_name).count() > 0, m.url)
    shot("header-search-mobile-results", m)
    goto(m, "/")
    with m.expect_navigation(wait_until="domcontentloaded", timeout=10000):
        m.mouse.click(*centre(m, ".hm-cta a.btn-outline"))
    check("mobile: 'Explore services' CTA (real mouse click beside the art) opens services", m.url.rstrip("/").endswith("/services"), m.url)
    m.close()

    # Signed-in customer: same hero; CTAs and header search target the portal shop.
    ctx = browser.new_context(viewport={"width": 390, "height": 844})
    cu = watch(use(ctx.new_page()))
    register_customer(cu, "hero")
    goto(cu, "/")
    settle(cu)
    check("signed-in customer home: 'Shop products' CTA + both header search forms point at /portal/shop",
          cu.get_attribute(".hm-cta a.btn-primary", "href") == "/portal/shop" and cu.get_attribute("#csMobileSearch", "action") == "/portal/shop"
          and cu.get_attribute(".cs-search", "action") == "/portal/shop")
    layout_checks(cu, 390, "signed-in customer @390px")
    no_box_check(cu, "signed-in customer @390px")
    shot("customer-home-hero-390px", cu)
    ctx.close()

    # ---------------- E. console / network ----------------
    check(f"no console errors, page errors, failed requests or 4xx/5xx sub-resources on any page visited ({len(PROBLEMS)})",
          not PROBLEMS, PROBLEMS[:10])

    print(f"\nRESULT: {len(CHECKS)} passed, 0 failed (TAG={TAG})", flush=True)
    browser.close()
