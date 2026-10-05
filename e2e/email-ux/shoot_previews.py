#!/usr/bin/env python3
"""Email UX screenshot pass.

Renders every preview HTML (produced by EmailTemplateRendererTests into
email-previews/) at desktop and mobile widths and writes full-page shots to
e2e/email-ux/evidence/. Used for the visual consistency audit and the final
evidence bundle.

    python3 e2e/email-ux/shoot_previews.py [--src email-previews] [--out e2e/email-ux/evidence]
"""
import argparse
import pathlib
import sys

from playwright.sync_api import sync_playwright

ROOT = pathlib.Path(__file__).resolve().parents[2]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=str(ROOT / "email-previews"))
    ap.add_argument("--out", default=str(ROOT / "evidence" / "email-ux"))
    args = ap.parse_args()
    src = pathlib.Path(args.src)
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    pages = sorted(src.glob("*.html"))
    if not pages:
        print(f"no previews in {src} - run the EmailTemplateRendererTests first")
        return 1

    with sync_playwright() as p:
        browser = p.chromium.launch()
        for width, label in ((1280, "desktop"), (390, "mobile")):
            ctx = browser.new_context(viewport={"width": width, "height": 800},
                                      device_scale_factor=2)
            page = ctx.new_page()
            for html in pages:
                page.goto(html.resolve().as_uri())
                shot = out / f"{html.stem}-{label}.png"
                page.screenshot(path=str(shot), full_page=True)
                print(f"shot {shot.name}")
            ctx.close()
        browser.close()
    print(f"done -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
