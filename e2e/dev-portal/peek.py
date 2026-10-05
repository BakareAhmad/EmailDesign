"""Dev helper: screenshot a list of dev pages (not evidence)."""
import sys
from playwright.sync_api import sync_playwright
BASE = "http://127.0.0.1:5119"
out = sys.argv[1]; pages = sys.argv[2:]
with sync_playwright() as p:
    b = p.chromium.launch(); pg = b.new_page(viewport={"width": 1440, "height": 900})
    pg.goto(BASE + "/Account/Login"); pg.fill("#Email", "developer@dechimexprinthub.local"); pg.fill("#Password", "DevPortal#2026!")
    pg.click("form button[type=submit]"); pg.wait_for_load_state("networkidle")
    for i, path in enumerate(pages):
        pg.goto(BASE + path); pg.wait_for_load_state("networkidle")
        pg.screenshot(path=f"{out}/{i:02d}.png", full_page=True)
        print(i, path, pg.title())
    b.close()
