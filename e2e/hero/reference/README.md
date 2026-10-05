# Hero reference screenshots (visual spec)

Source: the owner's repository https://github.com/BakareAhmad/screenshots.git. These are the
`hero-section-*.png` element screenshots of the pre-refactor hero, captured at 2x device pixel ratio.

The copies here are downscaled 2x → 1x and saved as JPEG (quality 85), so one reference pixel equals
one CSS pixel. That lets `e2e/hero/hero_e2e.py` place each one side by side with the current hero
at the same viewport, producing the `NN-hero-vs-reference-<width>px.png` evidence images.

| File | Viewport |
|---|---|
| hero-section-mobile-compact-360x740.jpg | 360 × 740 |
| hero-section-mobile-standard-390x844.jpg | 390 × 844 |
| hero-section-mobile-large-640x800.jpg | 640 × 800 |
| hero-section-tablet-portrait-768x1024.jpg | 768 × 1024 |
| hero-section-tablet-landscape-1024x768.jpg | 1024 × 768 |
| hero-section-laptop-1200x800.jpg | 1200 × 800 |
| hero-section-desktop-1440x900.jpg | 1440 × 900 |
| hero-section-desktop-xl-1920x1080.jpg | 1920 × 1080 |

At 1200 px and wider, the old element screenshots include the sticky header, which overlaid the
hero at those widths. The E2E crops the current page the same way, so the comparison is like for like.

These images are specs for layout and composition only. No old code was copied.
