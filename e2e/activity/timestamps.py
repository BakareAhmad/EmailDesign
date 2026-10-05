"""Business-activity timestamp regression E2E (the "1 hours ago" fix).

Runs against a fresh sandbox app on :5119 in Africa/Lagos (UTC+1) — the owner's
timezone and the exact environment of the reported defect: an offset-less ISO
stamp (what SQL Server materialisation emits through the old rows template for
DateTime Kind=Unspecified) is parsed by Date.parse as LOCAL time, skewing every
entry +1 hour so all recent activity read "1 hours ago".

Checks:
  real rows:  datetime attributes are explicit UTC (…Z); fresh entries show
              "Just now" / "N minutes ago" with NO +1h skew and never "1 hours".
  formatter:  the real page formatter (times(), run by its 15s tick) against
              controlled stamps — legacy no-Z (the defect vector) and Z — at
              every bucket boundary: Just now / 1 minute ago / 1 hour ago /
              2 hours ago / absolute UTC fallback for >=24h.
The injected legacy stamps are shaped exactly like the old template output;
the fetches that replace #activityRows are aborted so the specimens survive
the tick that formats them.
"""
from pathlib import Path
import os, re, sys
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2]
BASE = 'http://127.0.0.1:5119'
TAG = (sys.argv[1] if len(sys.argv) > 1 else 'ts')[:10]
SHOTS = Path(os.environ.get('E2E_SHOTS_DIR', '/home/user/activity-ts-dry'))
SHOTS.mkdir(parents=True, exist_ok=True)

f = ROOT / 'e2e/major-categories/workflow.py'
m = {'__file__': str(f)}
exec(compile(f.read_text().split('\nwith sync_playwright()')[0], str(f), 'exec'), m)
h = m['h']; h['TAG'] = TAG
checks = []; seq = 0


def check(name, ok, extra=''):
    assert ok, f'{name} {extra}'
    checks.append(name)
    print(f'PASS {len(checks):02d}: {name}', flush=True)


def shot(page, label, locator=None):
    global seq
    seq += 1
    path = str(SHOTS / f'{seq:02d}-{label}.png')
    (locator or page).screenshot(path=path)
    print('shot', path, flush=True)


with sync_playwright() as pw:
    b = pw.chromium.launch()
    # The owner's timezone (UTC+1): offset-less stamps parse as LOCAL here —
    # this is the environment in which "1 hours ago" was reported.
    ctx = b.new_context(viewport={'width': 1500, 'height': 1000}, timezone_id='Africa/Lagos')
    admin = ctx.new_page()
    admin.on('dialog', lambda d: d.accept())
    h['owner_login'](admin)
    m['create_category'](admin, f'TS category {TAG}')  # real business-activity row
    admin.goto(BASE + '/admin/activity', wait_until='domcontentloaded')
    admin.wait_for_selector('[data-activity-id]', timeout=15000)

    # ---------- STAGE 1: real rows in the owner's timezone ----------
    stamps = admin.eval_on_selector_all('#activityRows time[datetime]', 'els=>els.map(e=>e.getAttribute("datetime"))')
    check('every rendered datetime attribute is explicit UTC (…Z)', bool(stamps) and all(s.endswith('Z') for s in stamps))
    skew = admin.eval_on_selector_all(
        '#activityRows time[datetime]',
        'els=>els.map(e=>Math.abs(Date.now()-Date.parse(e.getAttribute("datetime"))))')
    check('fresh rows parse within minutes of now — no +1h skew', all(ms < 600000 for ms in skew))
    texts = admin.eval_on_selector_all('#activityRows time[datetime]', 'els=>els.map(e=>e.textContent.trim())')
    check('fresh rows read "Just now"/"N minutes ago"', all(t == 'Just now' or re.match(r'^\d+ minutes? ago$', t) for t in texts), str(texts))
    check('the reported "1 hours ago" symptom is gone', all('1 hours' not in t and 'hours ago' not in t for t in texts), str(texts))
    check('relative text is correctly pluralised', all(not re.match(r'^1 minutes ', t) for t in texts), str(texts))
    shot(admin, 'live-rows-no-skew-in-lagos')

    # ---------- STAGE 2: persistence after full reload ----------
    admin.reload(wait_until='domcontentloaded')
    admin.wait_for_selector('[data-activity-id]', timeout=15000)
    stamps2 = admin.eval_on_selector_all('#activityRows time[datetime]', 'els=>els.map(e=>e.getAttribute("datetime"))')
    check('after reload: datetime attributes still explicit UTC', bool(stamps2) and all(s.endswith('Z') for s in stamps2))
    texts2 = admin.eval_on_selector_all('#activityRows time[datetime]', 'els=>els.map(e=>e.textContent.trim())')
    check('after reload: relative text still free of the +1h skew',
          all('hours ago' not in t for t in texts2) and all(t == 'Just now' or re.match(r'^\d+ minutes? ago$', t) for t in texts2), str(texts2))
    shot(admin, 'reload-persistence-correct-timestamps')

    # ---------- STAGE 3: the real formatter against controlled stamps ----------
    # Abort the rows fetch so the refresh() that follows times() in the tick
    # cannot replace the specimens. Legacy stamps are shaped exactly like the
    # old template's output for Kind=Unspecified (offset-less ISO).
    admin.route(re.compile(r'format=rows'), lambda route: route.abort())
    admin.evaluate("""() => {
      const host = document.getElementById('activityRows');
      const legacy = s => new Date(Date.now() - s * 1000).toISOString().replace('Z', '');
      const utc = s => new Date(Date.now() - s * 1000).toISOString();
      const wrap = document.createElement('div');
      wrap.id = 'tsMatrix';
      wrap.innerHTML = [
        ['legacy-30s', legacy(30), 'age 30s, legacy no-Z stamp:'],
        ['legacy-90s', legacy(90), 'age 90s, legacy no-Z stamp:'],
        ['legacy-3650s', legacy(3650), 'age 1h01m, legacy no-Z stamp:'],
        ['utc-2h30m', utc(9000), 'age 2h30m, Z stamp:'],
        ['utc-25h', utc(90000), 'age 25h, Z stamp:']
      ].map(([id, attr, label]) =>
        '<article class="activity-entry" data-test="' + id + '" style="padding:12px 0;border-bottom:1px solid var(--n-100)">' +
        '<strong style="font-size:15px">' + label + '</strong> ' +
        '<time class="text-xs text-muted" datetime="' + attr + '" title="2026-09-22 12:34:56 UTC">PENDING</time></article>'
      ).join('');
      host.prepend(wrap);
    }""")
    # The page's own 15s tick calls times() unconditionally — wait for it.
    admin.wait_for_function(
        "document.querySelector('[data-test=legacy-30s] time').textContent!=='PENDING'", timeout=30000)
    got = admin.evaluate(
        "()=>Object.fromEntries([...document.querySelectorAll('#tsMatrix [data-test]')]"
        ".map(a=>[a.dataset.test, a.querySelector('time').textContent.trim()]))")
    check('30s legacy stamp (the exact defect vector) reads "Just now"', got['legacy-30s'] == 'Just now', repr(got.get('legacy-30s')))
    check('90s legacy stamp reads "1 minute ago" (singular)', got['legacy-90s'] == '1 minute ago', repr(got.get('legacy-90s')))
    check('1h01m legacy stamp reads "1 hour ago" (singular)', got['legacy-3650s'] == '1 hour ago', repr(got.get('legacy-3650s')))
    check('2h30m stamp reads "2 hours ago" (plural)', got['utc-2h30m'] == '2 hours ago', repr(got.get('utc-2h30m')))
    check('25h stamp falls back to the absolute UTC text', got['utc-25h'].endswith('UTC'), repr(got.get('utc-25h')))
    shot(admin, 'age-buckets-singular-and-plural', admin.locator('#tsMatrix'))
    shot(admin, 'absolute-fallback-25h', admin.locator('[data-test=utc-25h]'))

    print(f'\nALL {len(checks)} CHECKS PASSED (TAG={TAG})', flush=True)
    b.close()
