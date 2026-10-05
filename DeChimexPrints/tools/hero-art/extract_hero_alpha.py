#!/usr/bin/env python3
"""Cut the owner's hero art out of its white studio background -> true-alpha PNG + WebP.

Dev-time tool only (not used by the app at runtime). Needs Pillow, numpy and scipy; rembg (isnet) is
optional and only used as a *hint* for enclosed white regions.

Why not a segmentation model alone: isnet/u2net drop the white pedestals and the white mug (white on
white), and pure colour-to-alpha makes the products see-through. So:

  1. near-white pixels (min channel >= 244) that are connected to the image border are background;
  2. inside that background, alpha follows a soft luma ramp (L >= 252 -> 0, L <= 244 -> 1), which keeps the
     subject's own soft shadows as partial alpha and anti-aliases the edges (no hard cut, no halo);
  3. colour is un-premultiplied against white, so semi-transparent edge pixels don't carry a white fringe;
  4. enclosed white regions (inside the lavender ring, mug-handle holes) become background ONLY when they are
     flat, pure white AND the isnet hint says "not subject" there; white pedestals are shaded and the white mug
     is subject, so they stay opaque;
  5. the lavender ring is not near-white (min channel ~215-235) so it stays, in colour.

Usage:
  extract_hero_alpha.py SRC OUT_BASENAME [--sizes 1536x1024,768x512] [--no-hint]
    -> OUT_BASENAME.png/.webp (first size) and OUT_BASENAME-<w>.png/.webp for the others
"""
import argparse
import os

import numpy as np
from PIL import Image
from scipy import ndimage

T_BG = 244      # min channel at/above this = candidate background (near-white)
L_CLEAR = 252   # (denoised) luma at/above this = fully transparent
FLAT_MIN = 250  # enclosed region: mean min-channel at/above this ...
FLAT_STD = 3.0  # ... and std at/below this = "flat pure white"
HINT_LOW = 0.2  # enclosed region: mean isnet mask below this = not subject


def isnet_hint(rgb):
    try:
        from rembg import new_session, remove
    except ImportError:
        return None
    os.environ.setdefault("U2NET_HOME", "/var/tmp/u2net")
    sess = new_session("isnet-general-use")
    m = remove(Image.fromarray(rgb), session=sess, only_mask=True)
    return np.asarray(m.convert("L"), dtype=np.float32) / 255.0


def extract(rgb, use_hint=True, log=print):
    rgb = rgb.astype(np.float32)
    # denoised measures: JPEG noise in the soft floor shadows / at pedestal edges would otherwise speckle
    minc = ndimage.median_filter(rgb.min(axis=2), size=5)
    luma = ndimage.gaussian_filter(0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2], 1.2)
    cand = minc >= T_BG
    lab, n = ndimage.label(cand, structure=np.ones((3, 3)))
    border = np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))
    bg = np.isin(lab, border[border > 0])

    # enclosed near-white components: flip only flat pure white that the hint does not call subject
    hint = isnet_hint(rgb.astype(np.uint8)) if use_hint else None
    enclosed = [i for i in range(1, n + 1) if i not in set(border.tolist())]
    sizes = ndimage.sum(np.ones_like(minc), lab, enclosed) if enclosed else []
    means = ndimage.mean(minc, lab, enclosed) if enclosed else []
    stds = ndimage.standard_deviation(minc, lab, enclosed) if enclosed else []
    hints = ndimage.mean(hint, lab, enclosed) if (enclosed and hint is not None) else [0.0] * len(enclosed)
    flipped = 0
    for i, sz, mu, sd, h in zip(enclosed, sizes, means, stds, hints):
        if sz >= 30 and mu >= FLAT_MIN and sd <= FLAT_STD and h < HINT_LOW and hint is not None:
            bg |= lab == i
            flipped += 1
            log(f"  flip enclosed #{i}: {int(sz)} px, mean {mu:.1f}, std {sd:.1f}, hint {h:.2f}")
    log(f"  {n} near-white components, {len(enclosed)} enclosed, {flipped} flipped to background")

    a = np.ones_like(luma)
    ramp = np.clip((L_CLEAR - luma) / (L_CLEAR - T_BG), 0.0, 1.0)
    a[bg] = ramp[bg]
    # un-premultiply against white: C = a*F + (1-a)*255  ->  F = (C - (1-a)*255) / a
    out = rgb.copy()
    part = (a > 0) & (a < 1)
    af = a[part][:, None]
    out[part] = np.clip((rgb[part] - (1 - af) * 255.0) / af, 0, 255)
    out[a == 0] = 255  # fully transparent pixels: colour irrelevant, keep them clean for WebP
    rgba = np.dstack([out, np.round(a * 255)]).astype(np.uint8)
    return Image.fromarray(rgba, "RGBA")


def stats(im):
    a = np.asarray(im.getchannel("A"))
    px = np.asarray(im).astype(int)
    r, g, b = px[..., 0], px[..., 1], px[..., 2]
    lav = ((a > 128) & (b > 200) & (b - r > 18) & (b - g > 18) & (r > 150)).sum()
    return f"opaque {(a == 255).mean():.1%}, partial {((a > 0) & (a < 255)).mean():.1%}, transparent {(a == 0).mean():.1%}, lavender {lav:,} px"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--sizes", default="")
    ap.add_argument("--no-hint", action="store_true")
    args = ap.parse_args()
    src = Image.open(args.src).convert("RGB")  # palette sources (hero-mobile.png) must become RGB first
    cut = extract(np.asarray(src), use_hint=not args.no_hint)
    sizes = [tuple(map(int, s.split("x"))) for s in args.sizes.split(",") if s] or [src.size]
    for k, (w, h) in enumerate(sizes):
        im = cut if (w, h) == cut.size else cut.resize((w, h), Image.LANCZOS)
        base = args.out if k == 0 else f"{args.out}-{w}"
        im.save(base + ".png", optimize=True)
        im.save(base + ".webp", quality=90, method=6, exact=False)
        print(f"{base}.png/.webp {w}x{h}: {stats(im)}")


if __name__ == "__main__":
    main()
