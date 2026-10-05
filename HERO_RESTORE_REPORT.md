# Hero restore — pre-refactor composition on the current architecture (2026-09-27)

**Request:** restore the OLD hero section's visual composition and responsive behaviour into the CURRENT
architecture. The spec is the owner's reference screenshots (https://github.com/BakareAhmad/screenshots.git, 8 viewports,
now also kept as 1x copies in `e2e/hero/reference/`).

**Restored:**
- the hero text;
- the image position and composition;
- the responsive structure;
- the relationships between text, image, container and background.

**Not done:**
- no revert of the refactor;
- no old code copied (the markup and CSS are rewritten on the current tokens and components);
- no other section touched;
- no new dependency;
- no JavaScript.

The folded-in earlier request ("use this hero image and blend with the page's background") is delivered by the true-alpha art (§2).

---

## 1. What changed

| Area | File | Change |
|---|---|---|
| Hero markup | `src/DeChimex.Host/Views/StoreHome/Index.cshtml` (hero `<section>` only) | See "Hero markup" below |
| Hero layout | `wwwroot/css/customer.css` (the `.hm-hero` block only) | Mobile-first, on the current tokens (`--cs-max`, `--cs-gutter`, colours). See "Hero layout" below |
| Art URLs | `Services/CustomerShellService.cs` | See "Art URLs" below |
| Art files | `wwwroot/img/` | See "Art files" below |
| Art tool | `DeChimexPrints/tools/hero-art/extract_hero_alpha.py` | The reproducible true-alpha extractor (§2) |
| Tests | `BrandAssetsTests.cs`, `MvcStorefrontTests.cs` | See "Tests" below |
| E2E | `e2e/hero/hero_e2e.py`, `e2e/brand/brand_e2e.py`, `e2e/fixes/run_final.sh`, `e2e/hero/run_final.sh` | See "E2E" below |

**Hero markup** (`Index.cshtml`):
- The reference copy:
  - kicker "Printing • Branding • Promotional" (uppercase via CSS);
  - H1 "Your brand, / *beautifully* / produced." with "beautifully" in the primary colour;
  - the lead;
  - the tagline "One studio, endless possibilities.".
- The CTAs "Shop products →" (primary) and "Explore services →" (outline).
- `<picture>` with a portrait source ≤720 px and a landscape source above.
- The metrics list with icon tiles. It shows only the owner's real figures, with the labels from the owner's Settings screen.
- The hero search form was **removed** to match the reference. Search stays in the header: a box on desktop and an icon on mobile.
- The product-mosaic fallback (shown when no art file exists) is unchanged.

**Hero layout** (`customer.css`):
- Two columns at every width.
- The art column bleeds into the right gutter.
- The metrics show as stacked cards ≤720 px and as one band above that.
- The art gets no box styling: no background, card, radius or shadow.

**Art URLs** (`CustomerShellService.cs`):
- The `<img>` fallback is now `img/hero.png` / `img/hero-768.png` (RGBA), not a JPG.
- `HeroSizes` was recalculated for the new column widths: `(min-width: 1288px) 880px, (min-width: 1024px) 74vw, (min-width: 721px) 72vw, 52vw`.

**Art files** (`wwwroot/img/`): new true-alpha `hero.webp` / `hero-768.webp` / `hero-mobile.webp`, plus full-quality RGBA PNG fallbacks. The flattened `hero.jpg` and `hero-768.jpg` were removed.

**Tests:**
- Picture markup and order (portrait WebP → portrait PNG → landscape WebP → PNG `<img>`).
- The restored copy and CTAs.
- No hero form, and exactly two header search forms.
- RGBA PNG/WebP headers and sizes.
- The fallback paths.

**E2E:**
- The new hero journey (§4).
- The brand journey updated (portrait art ≤720, PNG fallbacks, ring kept, DPR choices).
- Pipeline stage 8c renamed; its evidence marker is now `hero-vs-reference`.

## 2. The art: true alpha, nothing baked in

Sources:
- **Desktop:** the owner's `image-2.jpeg` (1536×1024), the original render with the lavender ring.
- **Mobile:** the pre-refactor portrait `hero-mobile.png` (800×1093).

The earlier `image-1.png` upload had lost the ring's colour: it showed as a black "swoosh". It was not used. The ring is **kept** in the new art.

`tools/hero-art/extract_hero_alpha.py` builds the alpha from the pixels themselves:
- **Background:** the background is the near-white region (min channel ≥ 244, measured on a denoised image) connected to the image border. White regions enclosed by the subject (inside the ring, the mug-handle holes) are made transparent too, but only when they are flat, pure white AND the isnet segmentation hint says "not subject" there (desktop: 5 regions flipped; mobile: 6).
- **Subject:** fully opaque, including the white pedestals and the white mug, which colour-keying would have made see-through.
- **Edges and subject shadows:** a soft luminance ramp at the boundary. Anti-aliasing and the objects' own contact shadows become partial alpha, not a hard cut and not a white fringe.
- **Colour:** semi-transparent pixels are un-premultiplied against white, so edges carry no white fringe. Opaque pixels are untouched (no retouching), and over white the output matches the source.

Usage:
```
export U2NET_HOME=/var/tmp/u2net   # isnet model cache for the hint (kept outside the repo)
python3 tools/hero-art/extract_hero_alpha.py image-2.jpeg wwwroot/img/hero --sizes 1536x1024,768x512
python3 tools/hero-art/extract_hero_alpha.py hero-mobile-source.png wwwroot/img/hero-mobile
```

Exports:
- WebP at q90 (method 6), lossless alpha plane;
- PNG at full quality (not quantised).

Measured on the served files (E2E §A):

| File | Size | Mode | Opaque / partial / transparent |
|---|---|---|---|
| hero.webp | 1536×1024 | RGBA | 51.5% / 3.5% / 45.0% |
| hero-mobile.webp | 800×1093 | RGBA | 61.7% / 2.8% / 35.5% |

In both files the corners and the top 12 px are fully transparent, and the lavender ring is present (84.6k and 52.1k px).

Stacking: the page background (`--canvas` #F7F6F2) → the transparent art (`pointer-events:none`) → the copy (`z-index:1`). On phones the kicker runs over the art's transparent top edge, exactly as in the reference.

## 3. Composition vs the reference (CSS px, DPR 1)

| Viewport | Reference: copy / art | Current: copy / art | Art served |
|---|---|---|---|
| 1920 | centred as at 1440 | 364–745 / 705–1580 | hero.webp |
| 1440 | 126–500 / 460–1360 | 124–505 / 465–1340 | hero.webp |
| 1200 | 112–445 / 415–1112 | 24–393 / 353–1200 | hero.webp |
| 1024 | 20–350 / 335–1024 | 24–336 / 303–1024 | hero-768.webp |
| 768 | 14–245 / 230–768 | 24–262 / 247–768 | hero-768.webp |
| 640 | 26–268 / ~290–615 (portrait) | 24–286 / 296–616 (portrait) | hero-mobile.webp |
| 390 | 26–175 / ~185–365 (portrait) | 16–173 / 183–374 (portrait) | hero-mobile.webp |
| 360 | ~26–165 / ~175–340 (portrait) | 16–162 / 172–344 (portrait) | hero-mobile.webp |
| 320 | not in the reference | 16–155 / 165–304 (portrait) | hero-mobile.webp |

Matching behaviour:
- Two columns everywhere.
- ≥721 px: copy about ⅓, art about ⅔. The art is vertically centred, tucks slightly under the copy and runs into the right gutter.
- ≤720 px: portrait art beside the copy (about 45/55).
- H1 always 3 lines.
- CTAs stacked ≤1399 px (full column width ≤720, arrow at the end) and side by side ≥1400.
- Metrics as stacked shadow cards ≤720 and one white band with dividers ≥721.

**Intentional differences:**
- The side margins at 1024–1200 follow the current `--cs-gutter` / `--cs-max` container tokens (the old page used its own narrower container).
- The page colour is the current `--canvas` rather than white.
- The header is the current header.

## 4. Verification

The final evidence is ONE clean run of `bash e2e/hero/run_final.sh hrrd0928` → `evidence/`.

| Stage | Result |
|---|---|
| Full test suite | 887/887 (459 unit + 428 integration) — `final-test-log.txt` |
| Public `/health` | Healthy (database, paystack, brevo) — `final-health-log.txt` |
| Developer smoke | 29/29 — `final-dev-smoke-log.txt` |
| Checkout regression | 60/60 — `final-checkout-regression-log.txt` |
| Brand images + responsive (regression, updated) | 282/282 — `final-brand-e2e-log.txt` |
| Design-review regression | 36/36 — `final-design-review-regression-log.txt` |
| Fixes regression | 39/39 — `final-fixes-e2e-log.txt` |
| Developer-portal regression | 107/107 — `final-dev-portal-regression-log.txt` |
| Dev sign-in / dropped-request regression | 37/37 — `final-dev-abort-e2e-log.txt` |
| **Hero restore journey** | **186/186 — `final-hero-e2e-log.txt`** |
| App unhandled exceptions / secret-leak sweep | 0 / clean — `final-app-log-summary.txt` |

## 5. Phase 8 checklist

- [x] **Hero text restored.** Kicker, 3-line H1 with the highlighted word, lead, tagline, both CTAs and the metrics. Verified in the HTML (integration test) and the rendered text (E2E).
- [x] **Image positioning and composition.** Art right, copy left, at all 9 widths.
  - The art is shown whole: rendered ratio equals the file ratio, it sits inside the viewport, and no ancestor clips it.
  - Measured against the reference in §3; the side-by-side images are in the evidence.
- [x] **Responsive structure.** Portrait art ≤720 and landscape above, with the breakpoint matching the `<picture>` media query.
  - CTAs stack or sit in a row as in the reference.
  - Metrics show as cards or as a band.
  - H1 stays on 3 lines.
  - The kicker stays in the viewport.
  - CTAs stay inside the copy column with no text overflow, down to 320 px.
- [x] **Transparent art, no box.** All six files are RGBA with transparent corners. At every width, each fully transparent art pixel (up to 194k per width) renders exactly the page colour: 0 differing pixels. The pixels just outside the image box are the page colour too. The PNG fallback was checked the same way (WebP sources removed).
- [x] **Edges, shadows and details preserved; no retouching.** Soft partial-alpha edges and contact shadows, opaque products (including the white pedestals and mug), and the lavender ring kept.
- [x] **Stack order.** Page → art → text (copy `z-index:1`, art `pointer-events:none`).
- [x] **No horizontal scroll.** `scrollWidth <= clientWidth` at 320/360/390/640/768/1024/1200/1440/1920.
- [x] **CTAs and navigation work.**
  - Real mouse clicks (desktop and mobile) and the keyboard (Tab/Enter).
  - Header navigation.
  - Signed-in customer: CTA and header search go to `/portal/shop`.
- [x] **Header search still works** (the hero search was removed).
  - Desktop box: a product created through the admin UI is found and opens.
  - Mobile: the icon opens the panel, and the product is found.
  - Edge cases: a no-match term shows the "No matches" empty state; empty input lists the catalogue; script-like input is escaped and no script runs.
- [x] **No console errors or broken images.**
  - 0 console errors, page errors, failed requests and 4xx/5xx sub-resources across every page the journey visits.
  - 0 broken `<img>` at every width.
- [x] **Evidence vs the references.** `evidence/02…09-hero-vs-reference-<w>px.png` show "REFERENCE | CURRENT" side by side for each of the 8 viewports.
- [x] **No regressions.** The full test suite and every pipeline regression stage are green (§4).
- [x] **Architecture preserved.**
  - Same view, service, CSS file and tokens.
  - No new package, no JS, no other section touched.
  - The mosaic fallback and the portal-shop routing are kept.
- [x] **Keys.** The owner's Brevo and Paystack keys are still in both appsettings files and in the zip. The secret-leak sweep over the evidence is clean.

## 6. Evidence (`evidence/`)

| Screenshot | Shows |
|---|---|
| 01-hero-restored-320px-viewport | 320 px (narrowest phone): two columns, CTAs fit, no scroll |
| 02–09-hero-vs-reference-{360,390,640,768,1024,1200,1440,1920}px | Reference (left) vs restored hero (right) at each reference viewport |
| 10-home-hero-png-fallback-1440px | Browser without WebP: the RGBA PNG loads, still no box |
| 11-home-full-page-1440px | Whole home page: the rest of the page is untouched |
| 12-header-search-desktop-results | Desktop header search finds the product created in the admin |
| 13-header-search-desktop-no-match | Edge case: no match gives the friendly empty state |
| 14-header-search-script-input-escaped | Invalid input: `<script>` is shown as text and nothing executes |
| 15-header-search-mobile-open | Mobile: the search icon opens the header search panel |
| 16-header-search-mobile-results | Mobile header search results |
| 17-customer-home-hero-390px | Signed-in customer home: same hero, links to the portal shop |

Logs: `final-*-log.txt`. Previous tasks' final evidence stays under `e2e/<task>/evidence-final-run/`.

## 7. Responsive Visual Refinement Pass (2026-10-03)

A dedicated visual refinement pass was performed on `.hm-hero` in `wwwroot/css/customer.css` (and verified across 23 viewports in `e2e/hero/hero_e2e.py`) to match the exact composition, optical balance, and spacing of the pre-refactor reference screenshots across all mobile, tablet, laptop, and desktop viewports:

1. **Mobile (`320px`–`720px`) — Independent Portrait Artwork & Copy Composition:**
   - Offset `.hm-visual` below the `.hm-kicker` pill (`margin-top: 30px`), eliminating the overlap between the kicker pill and the jersey collar/lavender ring at `320px`–`430px` and aligning the jersey collar horizontally with the first line of `.hm-title` (`Your brand,`).
   - Capped `.hm-hero-img` on mobile at `max-width: 243px` with fluid right breathing room (`margin-left: auto; margin-right: clamp(0px, calc((100% - 243px) * 0.38), 36px)`), preventing the portrait cut-out from towering over the copy column on large phones/phablets (`520px`–`720px`, e.g., `640×800` where the artwork sits at `x=339..582, y=50..382` matching the reference's `x=339..582, y=50..384`).
   - Restored the `24px` outer horizontal gutter on `.hm-hero > .cs-wrap` for `350px`–`720px` (`16px` at `≤349px` so `320px` buttons fit without overflow) and tuned mobile typography/vertical rhythm so `.hm-lead` wraps onto 4 lines and `.hm-tagline` onto 1 line at `640×800`.

2. **Tablet, Laptop & Desktop (`721px`–`1920px`) — Optical Centering & Fluid Container Scaling:**
   - Removed the negative right margin (`margin-right: calc(-1 * var(--cs-gutter))`) on `.hm-visual` so the landscape artwork respects the container right gutter at every width (`721px`–`1920px`) and aligns cleanly with the right edge of the `.hm-stats` band instead of touching the viewport border at `768px`–`1200px`.
   - Scaled `.hm-hero > .cs-wrap` fluidly up to `max-width: 1700px` with continuous `clamp()` horizontal padding (`clamp(16px, calc(20px + (100vw - 1024px) * 0.38), 88px)`), eliminating the `1240px` container cap that previously compressed the hero at `1440×900` and `1920×1080` while keeping smooth transitions across `720/721`, `767/768/769`, `1023/1024/1025`, and `1399/1400`.
   - Tuned desktop `.hm-hero` vertical padding and `.hm-stats` top margin so the 3-column metrics band fits completely above the fold on `1366×768` (`stats` bottom at `759px <= 768px`) as well as `1200×800`, `1280×800`, `1440×900`, and `1920×1080`.
   - Expanded `e2e/hero/hero_e2e.py` from 186 to **381 automated checks** covering all 9 primary viewports plus 14 additional responsive checkpoints and breakpoint boundaries (`375×812`, `414×896`, `430×932`, `720×900`, `721×900`, `767×1024`, `769×1024`, `900×800`, `1023×768`, `1025×768`, `1280×800`, `1366×768`, `1399×900`, `1400×900`).

