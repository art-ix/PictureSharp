#!/usr/bin/env python3
"""16-bit PNG corpus and reference samples for the 0.9.0 high-bit-depth tests.

The files are written by the minimal PNG writer below (zlib + explicit scanline filters, independent of PictureSharp's
encoder), from sample arrays that are the ground truth. Static files are additionally decoded with ImageMagick
(`convert <file> -depth 16 -endian MSB rgba:-`, a Q16 build) and must match the ground truth; its version is recorded. Pillow is
not used: it rounds 16-bit RGB/RGBA to 8 bits.

References are SHA-256 hashes of the expected RGBA samples as 16-bit little-endian values (the layout of
PixelFormat.Rgba64), one per frame. APNG frames are composed here with exact rational arithmetic (straight-alpha
source-over, rounded half up), not with PictureSharp's formula.

Run with: python3 -I tests/assets/png16_reference.py
"""

import hashlib
import json
import os
import struct
import subprocess
import zlib
from fractions import Fraction

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "corpus", "png16")
MAX = 65535

ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
CHANNELS = {0: 1, 2: 3, 4: 2, 6: 4}


def chunk(kind, body):
    data = kind + body
    return struct.pack(">I", len(body)) + data + struct.pack(">I", zlib.crc32(data) & 0xFFFFFFFF)


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def filter_rows(rows, bpp, first_filter):
    """Filters raw rows, cycling through all five filter types."""
    out = bytearray()
    prev = bytes(len(rows[0])) if rows else b""
    for y, row in enumerate(rows):
        ftype = (first_filter + y) % 5
        out.append(ftype)
        for i, x in enumerate(row):
            a = row[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            pred = [0, a, b, (a + b) >> 1, paeth(a, b, c)][ftype]
            out.append((x - pred) & 0xFF)
        prev = row
    return bytes(out)


def raw_rows(samples, width, height, ctype, x0=0, y0=0, dx=1, dy=1):
    """Big-endian 16-bit scanlines of the given pass from samples[y][x] = tuple of channel values."""
    rows = []
    for y in range(y0, height, dy):
        row = bytearray()
        for x in range(x0, width, dx):
            for v in samples[y][x]:
                row += struct.pack(">H", v)
        rows.append(bytes(row))
    return rows


def image_data(samples, width, height, ctype, interlaced):
    bpp = CHANNELS[ctype] * 2
    if not interlaced:
        return filter_rows(raw_rows(samples, width, height, ctype), bpp, 0)
    data = b""
    for index, (x0, y0, dx, dy) in enumerate(ADAM7):
        if x0 >= width or y0 >= height:
            continue
        data += filter_rows(raw_rows(samples, width, height, ctype, x0, y0, dx, dy), bpp, index)
    return data


def ihdr(width, height, ctype, interlaced):
    return chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 16, ctype, 0, 0, 1 if interlaced else 0))


def write_png(name, samples, width, height, ctype, interlaced=False, trns=None):
    body = b"\x89PNG\r\n\x1a\n" + ihdr(width, height, ctype, interlaced)
    if trns is not None:
        body += chunk(b"tRNS", b"".join(struct.pack(">H", v) for v in trns))
    body += chunk(b"IDAT", zlib.compress(image_data(samples, width, height, ctype, interlaced), 9))
    body += chunk(b"IEND", b"")
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(body)


def to_rgba(samples, ctype, trns):
    """Expected straight-alpha RGBA64 of each pixel."""
    out = []
    for row in samples:
        line = []
        for s in row:
            if ctype == 0:
                rgba = (s[0], s[0], s[0], 0 if trns is not None and s[0] == trns[0] else MAX)
            elif ctype == 2:
                rgba = (*s, 0 if trns is not None and tuple(s) == tuple(trns) else MAX)
            elif ctype == 4:
                rgba = (s[0], s[0], s[0], s[1])
            else:
                rgba = tuple(s)
            line.append(rgba)
        out.append(line)
    return out


def le_bytes(rgba_rows):
    return b"".join(struct.pack("<4H", *p) for row in rgba_rows for p in row)


def sha(rgba_rows):
    return hashlib.sha256(le_bytes(rgba_rows)).hexdigest()


def value(x, y, c, seed):
    # Spread over the full range, with low bytes that differ from the high bytes.
    return (x * 7919 + y * 104729 + c * 52361 + seed * 1299709 + (x * y * 31337)) % 65536


def pattern(width, height, ctype, seed):
    n = CHANNELS[ctype]
    rows = [[tuple(value(x, y, c, seed) for c in range(n)) for x in range(width)] for y in range(height)]
    rows[0][0] = tuple([0] * n)
    rows[0][1] = tuple([MAX] * n)
    return rows


def imagemagick_rgba(path):
    try:
        raw = subprocess.run(["convert", path, "-depth", "16", "-endian", "MSB", "rgba:-"], check=True, capture_output=True).stdout
    except (OSError, subprocess.CalledProcessError):
        return None
    vals = struct.unpack(">%dH" % (len(raw) // 2), raw)
    return [tuple(vals[i:i + 4]) for i in range(0, len(vals), 4)]


def imagemagick_version():
    try:
        return subprocess.run(["convert", "-version"], check=True, capture_output=True, text=True).stdout.splitlines()[0]
    except (OSError, subprocess.CalledProcessError):
        return None


# ------------------------------------------------------------------ APNG


def over(src, dst):
    """Straight-alpha source-over in exact arithmetic, rounded half up."""
    sa, da = src[3], dst[3]
    if sa == MAX or da == 0:
        return src
    if sa == 0:
        return dst
    out_a = Fraction(sa) + Fraction(da) * (MAX - sa) / MAX
    def channel(s, d):
        v = (Fraction(s) * sa + Fraction(d) * da * (MAX - sa) / MAX) / out_a
        return int(v + Fraction(1, 2))
    return (channel(src[0], dst[0]), channel(src[1], dst[1]), channel(src[2], dst[2]), int(out_a + Fraction(1, 2)))


def write_apng(name, width, height, frames):
    """frames: (x, y, w, h, dispose, blend, rgba rows). Frame 0 is the default image (full canvas)."""
    body = b"\x89PNG\r\n\x1a\n" + ihdr(width, height, 6, False)
    body += chunk(b"acTL", struct.pack(">II", len(frames), 0))
    seq = 0
    canvas = [[(0, 0, 0, 0)] * width for _ in range(height)]
    expected = []
    for index, (x0, y0, w, h, dispose, blend, rows) in enumerate(frames):
        body += chunk(b"fcTL", struct.pack(">IIIIIHHBB", seq, w, h, x0, y0, 1, 10, dispose, blend))
        seq += 1
        data = zlib.compress(image_data(rows, w, h, 6, False), 9)
        if index == 0:
            body += chunk(b"IDAT", data)
        else:
            body += chunk(b"fdAT", struct.pack(">I", seq) + data)
            seq += 1
        saved = [row[x0:x0 + w] for row in canvas[y0:y0 + h]]
        for y in range(h):
            for x in range(w):
                src = tuple(rows[y][x])
                canvas[y0 + y][x0 + x] = src if blend == 0 else over(src, canvas[y0 + y][x0 + x])
        expected.append([list(row) for row in canvas])
        op = 1 if index == 0 and dispose == 2 else dispose
        if op == 1:
            for y in range(h):
                for x in range(w):
                    canvas[y0 + y][x0 + x] = (0, 0, 0, 0)
        elif op == 2:
            for y in range(h):
                canvas[y0 + y][x0:x0 + w] = saved[y]
    body += chunk(b"IEND", b"")
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(body)
    return expected


def main():
    os.makedirs(OUT, exist_ok=True)
    refs = {}
    tool = imagemagick_version()

    def static(name, width, height, ctype, seed, interlaced=False, trns=None, samples=None):
        samples = samples or pattern(width, height, ctype, seed)
        write_png(name, samples, width, height, ctype, interlaced, trns)
        rgba = to_rgba(samples, ctype, trns)
        magick = imagemagick_rgba(os.path.join(OUT, name))
        flat = [p for row in rgba for p in row]
        # ImageMagick does not apply a tRNS key of a 16-bit image the way the PNG specification does in every build;
        # it is compared on the colour channels only for keyed files.
        if magick is not None:
            compare = (lambda p: p[:3]) if trns is not None else (lambda p: p)
            if [compare(p) for p in magick] != [compare(p) for p in flat]:
                raise SystemExit(f"ImageMagick disagrees with the ground truth for {name}")
        refs[name] = {"width": width, "height": height, "frames": [sha(rgba)], "imagemagick": magick is not None}

    static("gray16.png", 13, 7, 0, 1)
    static("rgb16.png", 13, 7, 2, 2)
    static("graya16.png", 13, 7, 4, 3)
    static("rgba16.png", 13, 7, 6, 4)
    static("gray16_adam7.png", 17, 11, 0, 5, interlaced=True)
    static("rgb16_adam7.png", 17, 11, 2, 6, interlaced=True)
    static("graya16_adam7.png", 17, 11, 4, 7, interlaced=True)
    static("rgba16_adam7.png", 17, 11, 6, 8, interlaced=True)

    # tRNS keys that differ from neighbouring samples only in the low byte.
    gray = pattern(9, 5, 0, 9)
    gray[1][1], gray[1][2], gray[1][3] = (0x1234,), (0x1235,), (0x1334,)
    static("gray16_trns.png", 9, 5, 0, 9, trns=(0x1234,), samples=gray)
    rgb = pattern(9, 5, 2, 10)
    rgb[2][2], rgb[2][3], rgb[2][4] = (0x4000, 0x8001, 0xC002), (0x4000, 0x8001, 0xC003), (0x4000, 0x8101, 0xC002)
    static("rgb16_trns.png", 9, 5, 2, 10, trns=(0x4000, 0x8001, 0xC002), samples=rgb)

    def solid(w, h, rgba):
        return [[rgba] * w for _ in range(h)]

    def ramp(w, h, alpha):
        return [[(value(x, y, 0, 11), value(x, y, 1, 11), value(x, y, 2, 11), alpha(x, y)) for x in range(w)] for y in range(h)]

    frames = [
        (0, 0, 8, 6, 0, 0, ramp(8, 6, lambda x, y: MAX)),                          # default image, SOURCE, NONE
        (1, 1, 4, 3, 1, 1, ramp(4, 3, lambda x, y: (x * 21845 + y * 9000) % 65536)),  # OVER partial alpha, BACKGROUND
        (2, 0, 5, 4, 2, 0, solid(5, 4, (1000, 2000, 3000, 40000))),               # SOURCE, PREVIOUS
        (0, 2, 6, 4, 0, 1, ramp(6, 4, lambda x, y: 0 if x == 0 else 30000 + x)),  # OVER with transparent column
    ]
    expected = write_apng("apng16.png", 8, 6, frames)
    refs["apng16.png"] = {"width": 8, "height": 6, "frames": [sha(f) for f in expected], "imagemagick": False}

    with open(os.path.join(HERE, "png16_reference.json"), "w") as f:
        json.dump({"tool": tool, "layout": "RGBA 16-bit little-endian, SHA-256 per frame", "files": refs}, f, indent=2, sort_keys=True)
        f.write("\n")


if __name__ == "__main__":
    main()
