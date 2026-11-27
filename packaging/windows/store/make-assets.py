#!/usr/bin/env python3
"""Makes the logos and tiles the Microsoft Store package needs from the app icon.

    python3 packaging/windows/store/make-assets.py        (needs Pillow: pip install pillow)

Reads packaging/icons/leanstudio.png and writes packaging/windows/store/Assets/*.png, in each scale and
target size Windows asks for. The icon already has its own shape on a clear background, so every logo
is the icon centered on a clear canvas, and the wide tile sets it in the middle. Run it again whenever
the icon changes.
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "..", "icons", "leanstudio.png")
ASSETS = os.path.join(HERE, "Assets")

icon = Image.open(SOURCE).convert("RGBA")


def logo(width, height, fill=0.8):
    """The icon centered on a clear canvas, taking `fill` of the shorter side."""
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    side = max(1, round(min(width, height) * fill))
    canvas.alpha_composite(icon.resize((side, side), Image.LANCZOS), ((width - side) // 2, (height - side) // 2))
    return canvas


def save(image, name):
    image.save(os.path.join(ASSETS, name), optimize=True)


os.makedirs(ASSETS, exist_ok=True)
for old in os.listdir(ASSETS):
    if old.endswith(".png"):
        os.remove(os.path.join(ASSETS, old))

for scale in (100, 125, 150, 200, 400):
    k = scale / 100
    save(logo(round(44 * k), round(44 * k), 1.0), f"Square44x44Logo.scale-{scale}.png")
    save(logo(round(50 * k), round(50 * k), 1.0), f"StoreLogo.scale-{scale}.png")
    save(logo(round(71 * k), round(71 * k), 0.7), f"SmallTile.scale-{scale}.png")
    save(logo(round(150 * k), round(150 * k), 0.6), f"Square150x150Logo.scale-{scale}.png")
    save(logo(round(310 * k), round(150 * k), 0.6), f"Wide310x150Logo.scale-{scale}.png")
    save(logo(round(310 * k), round(310 * k), 0.6), f"LargeTile.scale-{scale}.png")

# The taskbar, Start list, Alt+Tab and Explorer pick these exact sizes.
for size in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
    image = logo(size, size, 1.0)
    for suffix in ("", "_altform-unplated", "_altform-lightunplated"):
        save(image, f"Square44x44Logo.targetsize-{size}{suffix}.png")

print(f"wrote {len(os.listdir(ASSETS))} files to {ASSETS}")
