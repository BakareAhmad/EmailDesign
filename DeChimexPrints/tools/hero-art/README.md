# Hero art: true-alpha cut-outs

`extract_hero_alpha.py` is a dev-time tool; the app never runs it. It turns the owner's hero art,
shot on a white studio background, into RGBA PNG and WebP files that sit straight on the page
colour, with no white box and no halo.

It needs Pillow, numpy and scipy. rembg (isnet) is optional and only gives a hint for enclosed
white regions. None of these are app dependencies.

```bash
export U2NET_HOME=/var/tmp/u2net          # isnet model cache (kept outside the repo)
IMG=DeChimexPrints/src/DeChimex.Host/wwwroot/img
# desktop (landscape): owner upload image-2.jpeg -> hero.png/.webp + hero-768.png/.webp
python3 DeChimexPrints/tools/hero-art/extract_hero_alpha.py image-2.jpeg $IMG/hero --sizes 1536x1024,768x512
# phones <=720 px (portrait): the original portrait art (palette PNG, converted to RGB first)
python3 DeChimexPrints/tools/hero-art/extract_hero_alpha.py hero-mobile-source.png $IMG/hero-mobile
```

Results of the committed files:

| File | Fully opaque | Partial alpha (edges and shadows) | Fully transparent |
|---|---|---|---|
| hero.* | 51.5% | 3.5% | 45.0% |
| hero-mobile.* | 61.7% | 2.8% | 35.5% |

The lavender ring is kept in both. To check a result, composite it on the page colour #F7F6F2.
The art is designed for that background; the site has no dark mode.
