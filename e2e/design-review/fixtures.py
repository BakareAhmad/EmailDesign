"""Generate distinct PNG fixtures for the design-review E2E."""
from PIL import Image, ImageDraw

W, H = 640, 400


def make(path, color, label):
    img = Image.new("RGB", (W, H), color)
    d = ImageDraw.Draw(img)
    d.rectangle([8, 8, W - 8, H - 8], outline=(255, 255, 255), width=6)
    for x in range(0, W, 40):
        d.line([(x, 0), (x + 60, H)], fill=(255, 255, 255), width=2)
    d.text((W // 2 - 150, H // 2 - 30), label, fill=(0, 0, 0))
    img.save(path)
    print("wrote", path)


make("/home/user/e2e-dr-wip/product.png", (30, 120, 60), "DR PRODUCT")
make("/home/user/e2e-dr-wip/dr_v1.png", (30, 80, 160), "REVIEW V1")
make("/home/user/e2e-dr-wip/dr_v2.png", (170, 40, 40), "REVIEW V2")
