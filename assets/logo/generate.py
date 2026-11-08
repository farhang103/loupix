#!/usr/bin/env python3
"""Builds every Loupix logo file from one set of numbers.

    python3 assets/logo/generate.py     (needs Pillow and numpy)

Writes assets/logo/loupix.svg, assets/logo/loupix.png (1024 px) and
src/Loupix/Assets/loupix.ico. The app draws the same logo at runtime in
src/Loupix/UI/IconFactory.cs, so change the two together.

The idea: Loupix = loupe + pixels. A lens that reveals chunky pixels, with the
one you picked glowing in the middle. At 48 px and below the 3x3 grid would turn
to mush, so the small version keeps only the picked pixel and thickens the lines.
"""
import math
import struct
from io import BytesIO
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

TILE_TOP, TILE_BOTTOM = "#22345C", "#0D1630"
RING, HANDLE, LENS, PICK = "#F6F2E7", "#FFB627", "#0B1226", "#FFB627"
GRID = [
    ["#2DD4BF", "#38BDF8", "#60A5FA"],
    ["#34D399", PICK, "#818CF8"],
    ["#A3E635", "#F472B6", "#C084FC"],
]

# All geometry is a fraction of the tile's side.
RADIUS = 0.23
CX = CY = 0.445
RING_R = 0.255
HANDLE_END = 0.795
PITCH, CELL = 0.135, 0.115
SMALL_PIXEL = 0.17
SMALL_MAX = 48  # sizes up to this use the simple design


def strokes(small):
    """(ring stroke, handle stroke)"""
    return (0.10, 0.14) if small else (0.085, 0.125)


def rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i : i + 2], 16) for i in (0, 2, 4))


def render(size):
    small = size <= SMALL_MAX
    s = max(size * 12, 3072)
    ring, hs = strokes(small)
    outer, inner = RING_R + ring / 2, RING_R - ring / 2

    y, x = np.mgrid[0:s, 0:s] / (s - 1)
    t = ((x + y) / 2)[..., None]
    grad = (np.array(rgb(TILE_TOP)) * (1 - t) + np.array(rgb(TILE_BOTTOM)) * t).astype("uint8")
    tile = Image.new("L", (s, s), 0)
    ImageDraw.Draw(tile).rounded_rectangle((0, 0, s - 1, s - 1), radius=int(s * RADIUS), fill=255)
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    im.paste(Image.fromarray(grad, "RGB"), (0, 0), tile)
    d = ImageDraw.Draw(im)

    start = CX + (outer - 0.015) * math.cos(math.pi / 4)
    d.line((start * s, start * s, HANDLE_END * s, HANDLE_END * s), fill=rgb(HANDLE), width=int(hs * s))
    r = hs * s / 2
    e = HANDLE_END * s
    d.ellipse((e - r, e - r, e + r, e + r), fill=rgb(HANDLE))

    box = lambda rad: ((CX - rad) * s, (CY - rad) * s, (CX + rad) * s, (CY + rad) * s)
    d.ellipse(box(inner), fill=rgb(LENS))

    pixels = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    pd = ImageDraw.Draw(pixels)
    if small:
        h = SMALL_PIXEL / 2
        pd.rectangle(((CX - h) * s, (CY - h) * s, (CX + h) * s, (CY + h) * s), fill=rgb(PICK))
    else:
        h = CELL / 2
        for j, row in enumerate(GRID):
            for i, colour in enumerate(row):
                px, py = CX + (i - 1) * PITCH, CY + (j - 1) * PITCH
                pd.rectangle(((px - h) * s, (py - h) * s, (px + h) * s, (py + h) * s), fill=rgb(colour))
    lens = Image.new("L", (s, s), 0)
    ImageDraw.Draw(lens).ellipse(box(inner), fill=255)
    im.paste(pixels, (0, 0), Image.composite(pixels.split()[3], Image.new("L", (s, s), 0), lens))

    d.ellipse(box(outer), outline=rgb(RING), width=int(ring * s))
    return im.resize((size, size), Image.LANCZOS)


def svg():
    ring, hs = strokes(False)
    outer = RING_R + ring / 2
    inner = RING_R - ring / 2
    u = lambda v: round(v * 1024, 2)
    start = CX + (outer - 0.015) * math.cos(math.pi / 4)
    cells = []
    for j, row in enumerate(GRID):
        for i, colour in enumerate(row):
            px, py = CX + (i - 1) * PITCH - CELL / 2, CY + (j - 1) * PITCH - CELL / 2
            cells.append(f'<rect x="{u(px)}" y="{u(py)}" width="{u(CELL)}" height="{u(CELL)}" fill="{colour}"/>')
    body = "\n      ".join(cells)
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" role="img" aria-label="Loupix logo">
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="{TILE_TOP}"/>
      <stop offset="1" stop-color="{TILE_BOTTOM}"/>
    </linearGradient>
    <clipPath id="lens"><circle cx="{u(CX)}" cy="{u(CY)}" r="{u(inner)}"/></clipPath>
  </defs>
  <rect width="1024" height="1024" rx="{u(RADIUS)}" fill="url(#tile)"/>
  <line x1="{u(start)}" y1="{u(start)}" x2="{u(HANDLE_END)}" y2="{u(HANDLE_END)}" stroke="{HANDLE}" stroke-width="{u(hs)}" stroke-linecap="round"/>
  <circle cx="{u(CX)}" cy="{u(CY)}" r="{u(inner)}" fill="{LENS}"/>
  <g clip-path="url(#lens)">
      {body}
  </g>
  <circle cx="{u(CX)}" cy="{u(CY)}" r="{u(RING_R)}" fill="none" stroke="{RING}" stroke-width="{u(ring)}"/>
</svg>
"""


def write_ico(path):
    entries = []
    for size in ICO_SIZES:
        buf = BytesIO()
        render(size).save(buf, "PNG", optimize=True)
        entries.append((size, buf.getvalue()))
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(entries)))
        offset = 6 + 16 * len(entries)
        for size, data in entries:
            b = 0 if size >= 256 else size
            f.write(struct.pack("<BBBBHHII", b, b, 0, 0, 1, 32, len(data), offset))
            offset += len(data)
        for _, data in entries:
            f.write(data)


if __name__ == "__main__":
    here = ROOT / "assets" / "logo"
    (here / "loupix.svg").write_text(svg())
    render(1024).save(here / "loupix.png", optimize=True)
    write_ico(ROOT / "src" / "Loupix" / "Assets" / "loupix.ico")
    print("done")
