#!/usr/bin/env python3
"""Draws the app icon (the sidebar's brand mark: dark "T" on an orange rounded square) without any image library and
writes build/icons/TinkerCNC.ico (Windows), TinkerCNC.icns (macOS) and the web favicon.
Run again after changing the colours or the shape: python3 build/make-icons.py"""
import os, struct, zlib

ORANGE = (0xF0, 0x8A, 0x24)  # --accent
INK = (0x1A, 0x12, 0x06)     # --accent-ink
CORNER = 9 / 34              # brand-mark: 34 px square, 9 px radius
GLYPH = 22 / 34              # the 24-unit glyph is drawn 22 px wide inside it
# "M4 4h16v3.4h-6.3V20h-3.4V7.4H4z": the bar and the stem of the T, in the glyph's 24-unit box
RECTS = [(4, 4, 20, 7.4), (10.3, 7.4, 13.7, 20)]
SAMPLES = 4                  # per axis, for anti-aliasing

def inside_square(x, y, size):
    r = CORNER * size
    cx = min(max(x, r), size - r)
    cy = min(max(y, r), size - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r

def inside_glyph(x, y, size):
    scale = GLYPH * size / 24
    offset = (size - GLYPH * size) / 2
    gx, gy = (x - offset) / scale, (y - offset) / scale
    return any(x0 <= gx <= x1 and y0 <= gy <= y1 for x0, y0, x1, y1 in RECTS)

def render(size):
    rows = []
    step = 1 / SAMPLES
    for py in range(size):
        row = bytearray([0])  # PNG filter: none
        for px in range(size):
            square = glyph = 0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    x, y = px + (sx + .5) * step, py + (sy + .5) * step
                    if inside_square(x, y, size):
                        square += 1
                        glyph += inside_glyph(x, y, size)
            n = SAMPLES * SAMPLES
            colour = [round((o * (square - glyph) + i * glyph) / max(square, 1)) for o, i in zip(ORANGE, INK)]
            row += bytes(colour + [round(255 * square / n)])
        rows.append(bytes(row))
    return png(size, b"".join(rows))

def png(size, raw):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")

def ico(images):
    head = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, data = b"", b""
    for size, image in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(image), offset + len(data))
        data += image
    return head + entries + data

def icns(images):
    body = b"".join(kind + struct.pack(">I", 8 + len(image)) + image for kind, image in images)
    return b"icns" + struct.pack(">I", 8 + len(body)) + body

if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "icons")
    os.makedirs(out, exist_ok=True)
    sizes = {s: render(s) for s in (16, 32, 48, 64, 128, 256, 512, 1024)}
    with open(os.path.join(out, "TinkerCNC.ico"), "wb") as f:
        f.write(ico([(s, sizes[s]) for s in (16, 32, 48, 256)]))
    with open(os.path.join(out, "TinkerCNC.icns"), "wb") as f:
        f.write(icns([(b"ic07", sizes[128]), (b"ic08", sizes[256]), (b"ic09", sizes[512]), (b"ic10", sizes[1024])]))
    with open(os.path.join(out, "TinkerCNC.png"), "wb") as f:
        f.write(sizes[256])
    with open(os.path.join(here, "..", "src", "TinkerCNC.Web", "wwwroot", "favicon.png"), "wb") as f:
        f.write(sizes[64])
    print("icons written to", out)
