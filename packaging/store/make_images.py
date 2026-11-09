"""Regenerates the Store package's icons from the logo. Run from the repo root: python packaging/store/make_images.py"""
from pathlib import Path

from PIL import Image

OUT = Path("packaging/store/Images")
LOGO = Image.open("assets/logo/loupix.png").convert("RGBA")
NAVY = (0x0D, 0x16, 0x30, 255)


def icon(name, size):
    LOGO.resize((size, size), Image.LANCZOS).save(OUT / name)


def on_navy(name, width, height):
    canvas = Image.new("RGBA", (width, height), NAVY)
    side = round(height * 0.72)
    art = LOGO.resize((side, side), Image.LANCZOS)
    canvas.alpha_composite(art, ((width - side) // 2, (height - side) // 2))
    canvas.save(OUT / name)


OUT.mkdir(parents=True, exist_ok=True)
icon("StoreLogo.png", 50)
icon("Square44x44Logo.png", 44)
icon("Square71x71Logo.png", 71)
icon("Square150x150Logo.png", 150)
icon("Square310x310Logo.png", 310)
on_navy("Wide310x150Logo.png", 310, 150)

# Sharp taskbar and Start menu icons at each size Windows asks for.
for size in (16, 24, 32, 48, 256):
    icon(f"Square44x44Logo.targetsize-{size}.png", size)
    icon(f"Square44x44Logo.targetsize-{size}_altform-unplated.png", size)
    icon(f"Square44x44Logo.targetsize-{size}_altform-lightunplated.png", size)
