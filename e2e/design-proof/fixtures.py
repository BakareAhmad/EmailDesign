"""Generate distinct PNG fixtures for the design-proof E2E."""
from PIL import Image, ImageDraw

W, H = 640, 400


def make(path, color, label):
    img = Image.new("RGB", (W, H), color)
    d = ImageDraw.Draw(img)
    d.rectangle([8, 8, W - 8, H - 8], outline=(255, 255, 255), width=6)
    # Big centered-ish label (default bitmap font, scaled by repetition).
    for i, ch_size in enumerate([(0, 0)]):
        pass
    d.text((W // 2 - 150, H // 2 - 30), label, fill=(255, 255, 255))
    # Diagonal stripe so v1/v2 are visually unmistakable.
    for x in range(0, W, 40):
        d.line([(x, 0), (x + 60, H)], fill=(255, 255, 255), width=2)
    d.text((W // 2 - 150, H // 2 - 30), label, fill=(0, 0, 0))
    img.save(path)
    print("wrote", path)


make("/home/user/e2e-proof-wip/product.png", (30, 80, 160), "E2E PRODUCT")
make("/home/user/e2e-proof-wip/proof_v1.png", (170, 40, 40), "PROOF V1")
make("/home/user/e2e-proof-wip/proof_v2.png", (40, 150, 70), "PROOF V2")
make("/home/user/e2e-proof-wip/chat_photo.png", (200, 120, 20), "CHAT PHOTO")
