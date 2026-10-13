#!/usr/bin/env python3
"""Generates the deterministic test corpus in tests/assets/corpus.

Sources are synthetic (no third-party photos), variants are produced with the reference
tools: cwebp / img2webp / webpmux (libwebp 1.6.0), ImageMagick `convert` and Pillow.

Run with:  python3 -I tests/assets/generate_corpus.py
Then:      python3 -I tests/assets/make_reference.py
"""
import io
import os
import shutil
import struct
import subprocess
import sys

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.abspath(__file__))

# Match the C# oracle selection without changing the system installation.
WEBP_TOOLS = os.environ.get("WEBPSHARP_WEBP_TOOLS", os.path.join(ROOT, "..", "..", "reference", "libwebp-1.6.0-build"))
if os.path.isdir(WEBP_TOOLS):
    os.environ["PATH"] = os.path.abspath(WEBP_TOOLS) + os.pathsep + os.environ.get("PATH", "")
for tool in ['cwebp', 'img2webp', 'webpmux']:
    version = subprocess.check_output([tool, "-version"], text=True).splitlines()[0].strip().rsplit(" ", 1)[-1]
    if version != "1.6.0":
        raise RuntimeError(f"{tool} {version}: expected 1.6.0; run scripts/build-webp-reference.sh")

CORPUS = os.path.join(ROOT, "corpus")
SRC = os.path.join(CORPUS, "src")


def run(*args):
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def path(*parts):
    p = os.path.join(CORPUS, *parts)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    return p


# --------------------------------------------------------------------------- sources

def smooth_noise(rng, w, h, scale):
    """Value noise upsampled bilinearly: smooth, photo-like texture."""
    gw, gh = max(2, w // scale + 2), max(2, h // scale + 2)
    grid = rng.random((gh, gw, 3)) * 255
    small = Image.fromarray(grid.astype(np.uint8), "RGB")
    return np.asarray(small.resize((w, h), Image.BICUBIC), dtype=np.float64)


def make_photo(w, h, seed):
    rng = np.random.default_rng(seed)
    img = smooth_noise(rng, w, h, 64) * 0.55 + smooth_noise(rng, w, h, 12) * 0.3 + smooth_noise(rng, w, h, 3) * 0.15
    yy, xx = np.mgrid[0:h, 0:w]
    img[..., 0] += 40 * np.sin(xx / 17.0)
    img[..., 2] += 40 * np.cos(yy / 23.0)
    img += rng.normal(0, 4, img.shape)  # sensor-like grain
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), "RGB")


def make_graphic(w, h):
    """Flat colors, hard edges, text-like strokes: favors palettes and lossless."""
    img = Image.new("RGB", (w, h), (240, 240, 235))
    a = np.array(img)
    colors = [(200, 30, 30), (30, 120, 200), (20, 160, 60), (250, 200, 0), (40, 40, 40)]
    for i, c in enumerate(colors):
        x0, y0 = (i * 37) % w, (i * 53) % h
        a[y0:y0 + h // 3, x0:x0 + w // 4] = c
    for y in range(0, h, 9):
        a[y, ::2] = (0, 0, 0)
    for x in range(0, w, 13):
        a[::3, x] = (90, 0, 140)
    return Image.fromarray(a, "RGB")


def add_alpha(img, seed, binary=False):
    w, h = img.size
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.sqrt((xx - w / 2) ** 2 + (yy - h / 2) ** 2) / (min(w, h) / 2)
    alpha = np.clip((1.2 - r) * 255, 0, 255)
    if binary:
        alpha = np.where(alpha > 128, 255, 0)
    rng = np.random.default_rng(seed)
    rgba = np.dstack([np.asarray(img), alpha.astype(np.uint8)])
    # random junk under fully transparent pixels exercises -exact handling
    mask = rgba[..., 3] == 0
    rgba[mask, :3] = rng.integers(0, 256, (mask.sum(), 3))
    return Image.fromarray(rgba, "RGBA")


def generate_sources():
    os.makedirs(SRC, exist_ok=True)
    srcs = {
        "photo": make_photo(256, 192, 1),
        "photo_odd": make_photo(171, 93, 2),          # not a multiple of 16
        "graphic": make_graphic(200, 150),
        "tiny": make_photo(1, 1, 3),
        "thin": make_photo(3, 37, 4),
        "wide": make_photo(301, 5, 5),
    }
    srcs["photo_alpha"] = add_alpha(make_photo(200, 160, 6), 7)
    srcs["graphic_alpha"] = add_alpha(make_graphic(160, 120), 8, binary=True)
    srcs["odd_alpha"] = add_alpha(make_photo(77, 51, 9), 10)
    for name, img in srcs.items():
        img.save(os.path.join(SRC, name + ".png"))
    return list(srcs)


# --------------------------------------------------------------------------- WebP

WEBP_LOSSY = {
    "q0": ["-q", "0"], "q50": ["-q", "50"], "q75": ["-q", "75"], "q100": ["-q", "100"],
    "m0": ["-q", "75", "-m", "0"], "m2": ["-q", "75", "-m", "2"], "m6": ["-q", "80", "-m", "6"],
    "seg1": ["-segments", "1"], "sns0": ["-sns", "0"], "sns100": ["-sns", "100"],
    "f0": ["-f", "0"], "f100": ["-f", "100", "-sharpness", "7"], "nostrong": ["-nostrong"],
    "simplefilter": ["-nostrong", "-f", "60", "-sharpness", "3"],
    "autofilter": ["-af"], "part3": ["-partition_limit", "50", "-q", "90"],
    "sharp": ["-sharp_yuv"], "pass": ["-pass", "6", "-size", "4000"],
    "text": ["-preset", "text"], "photo": ["-preset", "photo"],
}

WEBP_ALPHA = {
    "a_default": [], "a_none": ["-alpha_filter", "none"], "a_best": ["-alpha_filter", "best"],
    "a_raw": ["-alpha_method", "0"], "a_q50": ["-alpha_q", "50"], "a_q0": ["-alpha_q", "0"],
    "a_exact": ["-exact"], "a_m6": ["-m", "6", "-alpha_filter", "best"],
}

WEBP_LOSSLESS = {
    "ll_z0": ["-lossless", "-z", "0"], "ll_z3": ["-lossless", "-z", "3"], "ll_z6": ["-lossless", "-z", "6"],
    "ll_z9": ["-lossless", "-z", "9"], "ll_exact": ["-lossless", "-exact"],
    "ll_near60": ["-lossless", "-near_lossless", "60"], "ll_q0m0": ["-lossless", "-q", "0", "-m", "0"],
}


def cwebp(src, dst, args):
    run("cwebp", "-quiet", *args, src, "-o", dst)


def generate_webp(sources):
    for name in sources:
        src = os.path.join(SRC, name + ".png")
        alpha = "alpha" in name
        for tag, args in WEBP_LOSSY.items():
            if name in ("photo", "photo_odd", "graphic") or tag in ("q75", "q100", "m6"):
                cwebp(src, path("webp", "lossy", f"{name}_{tag}.webp"), args)
        if alpha:
            for tag, args in WEBP_ALPHA.items():
                cwebp(src, path("webp", "alpha", f"{name}_{tag}.webp"), args)
        for tag, args in WEBP_LOSSLESS.items():
            if name in ("photo", "graphic", "graphic_alpha", "photo_alpha", "odd_alpha") or tag == "ll_z6":
                cwebp(src, path("webp", "lossless", f"{name}_{tag}.webp"), args)

    # Container features: metadata chunks via webpmux.
    meta = path("webp", "meta", "x")
    exif = make_exif(orientation=6)
    with open(meta[:-1] + "exif.bin", "wb") as f:
        f.write(exif)
    with open(meta[:-1] + "icc.bin", "wb") as f:
        f.write(b"\0\0\x01\x00fake-icc-profile" + bytes(range(64)))
    with open(meta[:-1] + "xmp.bin", "wb") as f:
        f.write(b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF/></x:xmpmeta>')
    base_lossy = path("webp", "lossy", "photo_q75.webp")
    base_alpha = path("webp", "alpha", "photo_alpha_a_default.webp")
    base_ll = path("webp", "lossless", "photo_ll_z6.webp")
    out = path("webp", "meta", "lossy_exif.webp")
    run("webpmux", "-set", "exif", meta[:-1] + "exif.bin", base_lossy, "-o", out)
    out2 = path("webp", "meta", "alpha_icc_xmp.webp")
    run("webpmux", "-set", "icc", meta[:-1] + "icc.bin", base_alpha, "-o", out2)
    run("webpmux", "-set", "xmp", meta[:-1] + "xmp.bin", out2, "-o", out2)
    run("webpmux", "-set", "exif", meta[:-1] + "exif.bin", base_ll, "-o", path("webp", "meta", "lossless_exif.webp"))


def make_anim_frames(n, w, h, alpha, alpha_value=200):
    frames = []
    bg = make_photo(w, h, 42)
    for i in range(n):
        if alpha:
            img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        else:
            img = bg.copy().convert("RGBA")
        a = np.array(img)
        cx, cy = 10 + i * (w - 20) // max(1, n - 1), h // 2
        yy, xx = np.mgrid[0:h, 0:w]
        disk = (xx - cx) ** 2 + (yy - cy) ** 2 < (min(w, h) // 4) ** 2
        a[disk] = (255, 40 * i % 256, 60, alpha_value if alpha else 255)
        frames.append(Image.fromarray(a, "RGBA"))
    return frames


def generate_animations():
    tmp = path("anim_src", "x")[:-1]
    for kind, alpha in (("opaque", False), ("alpha", True)):
        files = []
        for i, f in enumerate(make_anim_frames(6, 96, 64, alpha)):
            p = os.path.join(tmp, f"{kind}_{i}.png")
            f.save(p)
            files.append(p)
        run("img2webp", "-loop", "0", "-d", "80", "-lossless", *files, "-o", path("webp", "anim", f"{kind}_lossless.webp"))
        run("img2webp", "-loop", "3", "-d", "120", "-lossy", "-q", "70", *files, "-o", path("webp", "anim", f"{kind}_lossy.webp"))
        run("img2webp", "-loop", "0", "-mixed", "-d", "50", *files, "-o", path("webp", "anim", f"{kind}_mixed.webp"))
        if alpha:
            # Pillow writes frames with dispose/blend variants different from img2webp.
            frames = [Image.open(p).convert("RGBA") for p in files]
            frames[0].save(path("webp", "anim", "alpha_pillow.webp"), save_all=True, append_images=frames[1:],
                           duration=[40, 80, 120, 40, 80, 120], loop=1, lossless=True, background=(255, 0, 0, 128))
    shutil.rmtree(tmp)


# --------------------------------------------------------------------------- PNG

def strip_png_timestamps(path):
    """Removes ImageMagick's date:* tEXt and tIME chunks so the corpus is reproducible."""
    with open(path, "rb") as f:
        data = f.read()
    out = bytearray(data[:8])
    pos = 8
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        ctype = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if not (ctype == b"tIME" or (ctype == b"tEXt" and body.startswith(b"date:"))):
            out += data[pos:pos + 12 + length]
        pos += 12 + length
    with open(path, "wb") as f:
        f.write(out)


def generate_png(sources):
    photo = os.path.join(SRC, "photo.png")
    alpha = os.path.join(SRC, "photo_alpha.png")
    graphic = os.path.join(SRC, "graphic.png")
    galpha = os.path.join(SRC, "graphic_alpha.png")
    conv = [
        ("rgb8", photo, ["PNG24:"]), ("rgba8", alpha, ["PNG32:"]),
        ("rgb16", photo, ["-depth", "16", "PNG48:"]), ("rgba16", alpha, ["-depth", "16", "PNG64:"]),
        ("palette", graphic, ["PNG8:"]), ("palette_trns", galpha, ["PNG8:"]),
        ("gray8", photo, ["-colorspace", "Gray", "-depth", "8", "PNG:"]),
        ("gray16", photo, ["-colorspace", "Gray", "-depth", "16", "PNG:"]),
        ("gray4", photo, ["-colorspace", "Gray", "-depth", "4", "-define", "png:bit-depth=4", "-define", "png:color-type=0", "PNG:"]),
        ("gray2", photo, ["-colorspace", "Gray", "-depth", "2", "-define", "png:bit-depth=2", "-define", "png:color-type=0", "PNG:"]),
        ("gray1", photo, ["-colorspace", "Gray", "-threshold", "50%", "-define", "png:bit-depth=1", "-define", "png:color-type=0", "PNG:"]),
        ("graya8", alpha, ["-colorspace", "Gray", "-define", "png:color-type=4", "PNG:"]),
        ("rgb8_interlaced", photo, ["-interlace", "PNG", "PNG24:"]),
        ("rgba8_interlaced", alpha, ["-interlace", "PNG", "PNG32:"]),
        ("rgba16_interlaced", alpha, ["-interlace", "PNG", "-depth", "16", "PNG64:"]),
        ("palette_interlaced", graphic, ["-interlace", "PNG", "PNG8:"]),
        ("odd_rgba", os.path.join(SRC, "odd_alpha.png"), ["PNG32:"]),
        ("tiny", os.path.join(SRC, "tiny.png"), ["PNG24:"]),
        ("thin_interlaced", os.path.join(SRC, "thin.png"), ["-interlace", "PNG", "PNG24:"]),
        ("dpi", photo, ["-units", "PixelsPerInch", "-density", "300x150", "PNG24:"]),
    ]
    for name, src, args in conv:
        out = path("png", name + ".png")
        run("convert", src, *args[:-1], args[-1] + out)
        strip_png_timestamps(out)
    # Pillow variants: palette with partial alpha (tRNS), low-depth palette, zlib levels, APNG.
    g = Image.open(galpha).convert("RGBA").quantize(16)
    g.save(path("png", "palette4_trns.png"), bits=4)
    Image.open(photo).save(path("png", "rgb_level0.png"), compress_level=0)
    Image.open(photo).save(path("png", "rgb_level9.png"), compress_level=9)
    # Binary alpha only: Pillow's APNG "over" blending is not spec-exact for partial alpha.
    frames = make_anim_frames(5, 64, 48, True, alpha_value=255)
    frames[0].save(path("png", "apng_alpha.png"), save_all=True, append_images=frames[1:], duration=100, loop=0,
                   disposal=[0, 1, 2, 0, 1], blend=[0, 1, 1, 0, 1])
    img = Image.open(photo)
    img.save(path("png", "exif.png"), exif=b"Exif\0\0" + make_exif(orientation=3))


# --------------------------------------------------------------------------- JPEG

def generate_jpeg():
    photo = os.path.join(SRC, "photo.png")
    odd = os.path.join(SRC, "photo_odd.png")
    conv = [
        ("q90_420", photo, ["-quality", "90", "-sampling-factor", "2x2,1x1,1x1"]),
        ("q90_422", photo, ["-quality", "90", "-sampling-factor", "2x1,1x1,1x1"]),
        ("q90_444", photo, ["-quality", "90", "-sampling-factor", "1x1,1x1,1x1"]),
        ("q90_411", photo, ["-quality", "90", "-sampling-factor", "4x1,1x1,1x1"]),
        ("q90_440", photo, ["-quality", "90", "-sampling-factor", "1x2,1x1,1x1"]),
        ("q50_420", photo, ["-quality", "50"]),
        ("q100_444", photo, ["-quality", "100", "-sampling-factor", "1x1,1x1,1x1"]),
        ("progressive_420", photo, ["-quality", "85", "-interlace", "JPEG"]),
        ("progressive_444", photo, ["-quality", "85", "-interlace", "JPEG", "-sampling-factor", "1x1,1x1,1x1"]),
        ("gray", photo, ["-quality", "85", "-colorspace", "Gray"]),
        ("gray_progressive", photo, ["-quality", "85", "-colorspace", "Gray", "-interlace", "JPEG"]),
        ("cmyk", photo, ["-quality", "90", "-colorspace", "CMYK"]),
        ("odd_420", odd, ["-quality", "85"]),
        ("odd_progressive", odd, ["-quality", "85", "-interlace", "JPEG"]),
        ("dpi", photo, ["-quality", "80", "-units", "PixelsPerInch", "-density", "72x72"]),
        ("optimized", photo, ["-quality", "80", "-define", "jpeg:optimize-coding=true"]),
    ]
    for name, src, args in conv:
        run("convert", src, *args, path("jpeg", name + ".jpg"))
    img = Image.open(photo)
    img.save(path("jpeg", "restart_blocks.jpg"), quality=85, restart_marker_blocks=5)
    img.save(path("jpeg", "restart_rows_progressive.jpg"), quality=85, restart_marker_rows=1, progressive=True)
    for o in range(1, 9):
        img.save(path("jpeg", f"orientation_{o}.jpg"), quality=85, exif=b"Exif\0\0" + make_exif(orientation=o))
    generate_jpeg_variants(img)


# Baseline JPEG writer for variants no tool produces (component layouts, color transforms, table forms).

def std_huffman_tables():
    """The Annex K tables, taken from the DHT segments libjpeg writes when it does not optimize coding."""
    buf = io.BytesIO()
    Image.new("RGB", (16, 16)).save(buf, "JPEG", quality=90)
    data = buf.getvalue()
    tables, pos = {}, 2
    while data[pos + 1] != 0xDA:
        marker, length = data[pos + 1], struct.unpack(">H", data[pos + 2:pos + 4])[0]
        seg = data[pos + 4:pos + 2 + length]
        while marker == 0xC4 and seg:
            counts = list(seg[1:17])
            n = sum(counts)
            tables[seg[0]] = (counts, list(seg[17:17 + n]))
            seg = seg[17 + n:]
        pos += 2 + length
    return tables  # keys: 0x00/0x01 DC, 0x10/0x11 AC


def huffman_codes(counts, values):
    codes, code, k = {}, 0, 0
    for length in range(1, 17):
        for _ in range(counts[length - 1]):
            codes[values[k]] = (code, length)
            code += 1
            k += 1
        code <<= 1
    return codes


def zigzag():
    order = sorted(((i + j, (i if (i + j) % 2 else j), i * 8 + j) for i in range(8) for j in range(8)))
    return [n for _, _, n in order]


DCT = np.array([[np.sqrt((1 if k == 0 else 2) / 8) * np.cos((2 * n + 1) * k * np.pi / 16) for n in range(8)]
                for k in range(8)])


class BitWriter:
    def __init__(self):
        self.out, self.acc, self.n = bytearray(), 0, 0

    def write(self, value, length):
        self.acc, self.n = (self.acc << length) | (value & ((1 << length) - 1)), self.n + length
        while self.n >= 8:
            self.n -= 8
            byte = (self.acc >> self.n) & 0xFF
            self.out.append(byte)
            if byte == 0xFF:
                self.out.append(0)
        self.acc &= (1 << self.n) - 1

    def flush(self):
        if self.n:
            self.write((1 << (8 - self.n)) - 1, 8 - self.n)


def segment(marker, payload):
    return bytes([0xFF, marker]) + struct.pack(">H", len(payload) + 2) + payload


def write_jpeg(out, channels, comps, apps=(), interleaved=True, restart=0, quant_scale=1, sof=0xC0, dht=True):
    """channels: full-size planes (h x w); comps: (id, h, v, table) per plane. Writes one scan per component
    unless interleaved. quant_scale > 1 produces 16-bit quantization tables."""
    height, width = channels[0].shape
    max_h, max_v = max(c[1] for c in comps), max(c[2] for c in comps)
    mcus_x, mcus_y = -(-width // (8 * max_h)), -(-height // (8 * max_v))
    zz = zigzag()
    quant = [np.array([[(1 + i + j) * (2 if t else 1) * quant_scale for j in range(8)] for i in range(8)]) for t in (0, 1)]
    precision = 1 if quant_scale > 1 else 0
    huff = std_huffman_tables()

    blocks = []
    for plane, (_, h, v, t) in zip(channels, comps):
        fx, fy = max_h // h, max_v // v
        dw, dh = -(-width * h // max_h), -(-height * v // max_v)
        padded = np.pad(plane.astype(np.float64), ((0, dh * fy - height), (0, dw * fx - width)), mode="edge")
        small = padded.reshape(dh, fy, dw, fx).mean(axis=(1, 3)).round()
        grid = np.pad(small, ((0, mcus_y * v * 8 - dh), (0, mcus_x * h * 8 - dw)), mode="edge") - 128
        coefs = {}
        for by in range(mcus_y * v):
            for bx in range(mcus_x * h):
                b = DCT @ grid[by * 8:by * 8 + 8, bx * 8:bx * 8 + 8] @ DCT.T
                coefs[bx, by] = np.round(b / quant[t]).astype(int).flatten()
        blocks.append((coefs, -(-dw // 8), -(-dh // 8)))

    def encode_block(bw, coef, t, pred):
        dc_codes, ac_codes = huffman_codes(*huff[t]), huffman_codes(*huff[0x10 | t])
        def put(codes, symbol, value, size):
            bw.write(*codes[symbol])
            if size:
                bw.write(value if value >= 0 else value + (1 << size) - 1, size)
        diff = coef[0] - pred
        size = int(abs(diff)).bit_length()
        put(dc_codes, size, diff, size)
        run = 0
        for k in range(1, 64):
            c = coef[zz[k]]
            if c == 0:
                run += 1
                continue
            while run > 15:
                put(ac_codes, 0xF0, 0, 0)
                run -= 16
            size = int(abs(c)).bit_length()
            put(ac_codes, (run << 4) | size, c, size)
            run = 0
        if run:
            put(ac_codes, 0x00, 0, 0)
        return coef[0]

    def scan(indices):
        bw, preds, rst = BitWriter(), [0] * len(comps), 0
        if len(indices) == 1:
            coefs, wb, hb = blocks[indices[0]]
            units = [[(indices[0], bx, by)] for by in range(hb) for bx in range(wb)]
        else:
            units = [[(i, mx * comps[i][1] + x, my * comps[i][2] + y) for i in indices
                      for y in range(comps[i][2]) for x in range(comps[i][1])]
                     for my in range(mcus_y) for mx in range(mcus_x)]
        for n, unit in enumerate(units):
            if restart and n and n % restart == 0:
                bw.flush()
                bw.out += bytes([0xFF, 0xD0 + rst])
                rst, preds = (rst + 1) & 7, [0] * len(comps)
            for i, bx, by in unit:
                preds[i] = encode_block(bw, blocks[i][0][bx, by], comps[i][3], preds[i])
        bw.flush()
        header = bytes([len(indices)]) + b"".join(bytes([comps[i][0], (comps[i][3] << 4) | comps[i][3]]) for i in indices)
        return segment(0xDA, header + bytes([0, 63, 0])) + bytes(bw.out)

    data = bytearray(b"\xFF\xD8")
    for marker, payload in apps:
        data += segment(marker, payload)
    for t in (0, 1):
        data += segment(0xDB, bytes([(precision << 4) | t]) + b"".join(
            struct.pack(">H" if precision else "B", int(quant[t].flatten()[zz[k]])) for k in range(64)))
    data += segment(sof, bytes([8]) + struct.pack(">HH", height, width) + bytes([len(comps)]) +
                    b"".join(bytes([c[0], (c[1] << 4) | c[2], c[3]]) for c in comps))
    if dht:
        for key in (0x00, 0x10, 0x01, 0x11):
            counts, values = huff[key]
            data += segment(0xC4, bytes([key] + counts + values))
    if restart:
        data += segment(0xDD, struct.pack(">H", restart))
    if interleaved:
        data += scan(list(range(len(comps))))
    else:
        for i in range(len(comps)):
            data += scan([i])
    data += b"\xFF\xD9"
    with open(out, "wb") as f:
        f.write(bytes(data))


def generate_jpeg_variants(img):
    small = img.resize((61, 45), Image.LANCZOS)  # odd size: partial MCUs everywhere
    rgb = np.asarray(small, dtype=np.float64)
    ycc = np.asarray(small.convert("YCbCr"), dtype=np.float64)
    y, cb, cr = ycc[..., 0], ycc[..., 1], ycc[..., 2]
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    k = np.tile(np.linspace(0, 255, 61), (45, 1))
    jfif = (0xE0, b"JFIF\0\x01\x01\x00\x00\x01\x00\x01\x00\x00")
    adobe = lambda transform: (0xEE, b"Adobe\0\x64\0\0\0\0" + bytes([transform]))
    ycc_ids = [(1, 2, 2, 0), (2, 1, 1, 1), (3, 1, 1, 1)]
    variants = [
        ("crafted_ycck", [y, cb, cr, k], [(1, 2, 2, 0), (2, 1, 1, 1), (3, 1, 1, 1), (4, 2, 2, 0)], [adobe(2)], {}),
        ("crafted_cmyk_no_adobe", [255 - r, 255 - g, 255 - b, k], [(1, 1, 1, 0), (2, 1, 1, 0), (3, 1, 1, 0), (4, 1, 1, 0)], [], {}),
        ("crafted_adobe_rgb", [r, g, b], [(1, 1, 1, 0), (2, 1, 1, 0), (3, 1, 1, 0)], [adobe(0)], {}),
        ("crafted_rgb_ids", [r, g, b], [(82, 1, 1, 0), (71, 1, 1, 0), (66, 1, 1, 0)], [], {}),
        ("crafted_luma_subsampled", [y, cb, cr], [(1, 1, 1, 0), (2, 2, 2, 1), (3, 2, 2, 1)], [jfif], {}),
        ("crafted_mixed_sampling", [y, cb, cr], [(1, 2, 2, 0), (2, 2, 1, 1), (3, 1, 2, 1)], [jfif], {}),
        ("crafted_sampling_4x2", [y, cb, cr], [(1, 4, 2, 0), (2, 1, 1, 1), (3, 1, 1, 1)], [jfif], {}),
        ("crafted_noninterleaved", [y, cb, cr], ycc_ids, [jfif], {"interleaved": False, "restart": 7}),
        ("crafted_no_dht", [y, cb, cr], ycc_ids, [jfif], {"dht": False}),
        ("crafted_dqt16", [y, cb, cr], ycc_ids, [jfif], {"quant_scale": 40, "sof": 0xC1}),
        ("crafted_gray_sampled", [y], [(1, 2, 2, 0)], [jfif], {}),
    ]
    for name, channels, comps, apps, kwargs in variants:
        write_jpeg(path("jpeg", name + ".jpg"), channels, comps, apps, **kwargs)

    # Tiny images: chroma rows of 1-2 samples use plain replication instead of fancy upsampling.
    for w, h, sub in ((1, 1, 2), (2, 3, 2), (5, 2, 2), (3, 7, 1)):
        small.resize((w, h)).save(path("jpeg", f"tiny_{w}x{h}_{('444', '422', '420')[sub]}.jpg"), quality=90, subsampling=sub)

    run("convert", os.path.join(SRC, "photo.png"), "-quality", "85", "-colorspace", "CMYK", "-interlace", "JPEG",
        path("jpeg", "cmyk_progressive.jpg"))
    run("convert", os.path.join(SRC, "photo.png"), "-quality", "85", "-units", "PixelsPerCentimeter", "-density", "28",
        path("jpeg", "dpcm.jpg"))
    img.save(path("jpeg", "exif_resolution.jpg"), quality=85, exif=b"Exif\0\0" + make_exif(resolution=(300, 300)))
    icc = bytes((i * 7 + i // 256) & 0xFF for i in range(150000))  # spans three APP2 chunks
    img.save(path("jpeg", "icc_multi.jpg"), quality=85, icc_profile=icc)
    buf = io.BytesIO()
    small.save(buf, "JPEG", quality=85)
    xmp = b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF/></x:xmpmeta>'
    data = buf.getvalue()
    with open(path("jpeg", "xmp.jpg"), "wb") as f:
        f.write(data[:2] + segment(0xE1, b"http://ns.adobe.com/xap/1.0/\0" + xmp) + data[2:])


# --------------------------------------------------------------------------- BMP

def write_bmp(out, width, height, bits, rows, palette=None, header_size=40, compression=0, masks=None,
              ppm=(2835, 2835), topdown=False, gap=0):
    """Writes a BMP from already packed rows (top row first; padded here unless RLE)."""
    if compression in (1, 2):
        data = rows
    else:
        stride = ((width * bits + 31) // 32) * 4
        ordered = rows if topdown else rows[::-1]
        data = b"".join(r + b"\0" * (stride - len(r)) for r in ordered)
    entry = 3 if header_size == 12 else 4
    pal = b"".join(bytes((b, g, r)) + (b"" if entry == 3 else b"\0") for r, g, b in (palette or []))
    if header_size == 12:
        hdr = struct.pack("<IHHHH", 12, width, height, 1, bits)
    else:
        hdr = struct.pack("<IiiHHIIiiII", header_size, width, -height if topdown else height, 1, bits, compression,
                          len(data), ppm[0], ppm[1], len(palette or []), 0)
        if masks is not None:
            hdr += struct.pack("<4I", *masks)
        hdr += b"\0" * (header_size - len(hdr))
    offset = 14 + len(hdr) + len(pal) + gap
    with open(out, "wb") as f:
        f.write(b"BM" + struct.pack("<IHHI", offset + len(data), 0, 0, offset) + hdr + pal + b"\xAA" * gap + data)


def palette_of(img, n):
    p = img.getpalette() + [0] * (3 * n)
    return [tuple(p[i * 3:i * 3 + 3]) for i in range(n)]


def pack_indices(img, bits):
    w, h = img.size
    px = list(img.getdata())
    rows = []
    for y in range(h):
        row = bytearray((w * bits + 7) // 8)
        for x in range(w):
            row[x * bits // 8] |= px[y * w + x] << (8 - bits - (x * bits) % 8)
        rows.append(bytes(row))
    return rows


def rle4_encode(img):
    """BI_RLE4 with encoded runs, even-length absolute runs, end-of-line and end-of-bitmap codes."""
    w, h = img.size
    px = list(img.getdata())
    out = bytearray()
    for y in range(h - 1, -1, -1):
        row = px[y * w:(y + 1) * w]
        x = 0
        literal = []

        def flush():
            while len(literal) >= 4:
                n = min(len(literal) & ~1, 254)
                chunk = literal[:n]
                del literal[:n]
                out.extend((0, n))
                body = bytes((chunk[i] << 4) | chunk[i + 1] for i in range(0, n, 2))
                out.extend(body + (b"\0" if len(body) & 1 else b""))
            for v in literal:
                out.extend((1, v << 4))
            literal.clear()

        while x < w:
            n = 1
            while x + n < w and row[x + n] == row[x] and n < 255:
                n += 1
            if n >= 3:
                flush()
                out.extend((n, (row[x] << 4) | row[x]))
                x += n
            else:
                literal.append(row[x])
                x += 1
        flush()
        out.extend((0, 0))
    out.extend((0, 1))
    return bytes(out)


def generate_bmp():
    photo = os.path.join(SRC, "photo.png")
    odd = os.path.join(SRC, "photo_odd.png")
    alpha = os.path.join(SRC, "photo_alpha.png")
    graphic = os.path.join(SRC, "graphic.png")
    conv = [
        ("im_rgb24", photo, ["BMP3:"]),
        ("im_rgb24_odd", odd, ["BMP3:"]),
        ("im_rgb24_v5", photo, ["BMP:"]),
        ("im_os2_rgb24", odd, ["BMP2:"]),
        ("im_os2_pal8", photo, ["-colors", "200", "BMP2:"]),
        ("im_pal8", photo, ["-colors", "200", "-compress", "None", "BMP3:"]),
        ("im_pal8_rle", photo, ["-colors", "200", "-compress", "RLE", "BMP3:"]),
        ("im_pal8_rle_odd", odd, ["-colors", "100", "-compress", "RLE", "BMP3:"]),
        ("im_pal4", graphic, ["-type", "Palette", "BMP3:"]),
        ("im_pal1", graphic, ["-monochrome", "BMP3:"]),
        ("im_rgba32_v5", alpha, ["BMP:"]),
        ("im_rgb565", photo, ["-define", "bmp:subtype=RGB565", "BMP:"]),
        ("im_rgb555", photo, ["-define", "bmp:subtype=RGB555", "BMP:"]),
        ("im_dpi", photo, ["-units", "PixelsPerInch", "-density", "300x150", "BMP3:"]),
    ]
    for name, src, args in conv:
        run("convert", src, *args[:-1], args[-1] + path("bmp", name + ".bmp"))

    # Pillow writer: 1-bit, grayscale palette, palette, 24-bit and 32-bit BI_RGB (read back as opaque BGRX).
    img = Image.open(photo).convert("RGB")
    img.convert("1").save(path("bmp", "pil_mono.bmp"))
    img.convert("L").save(path("bmp", "pil_gray.bmp"))
    img.quantize(64).save(path("bmp", "pil_pal.bmp"))
    img.save(path("bmp", "pil_rgb.bmp"), dpi=(72, 72))
    Image.open(alpha).save(path("bmp", "pil_rgba.bmp"))

    # Hand-built variants.
    rgb_odd = Image.open(odd).convert("RGB")
    w, h = rgb_odd.size
    bgr_rows = [rgb_odd.tobytes("raw", "BGR")[y * w * 3:(y + 1) * w * 3] for y in range(h)]
    write_bmp(path("bmp", "topdown_rgb24.bmp"), w, h, 24, bgr_rows, topdown=True)
    junk = bytes((x * 37 + 11) & 0xFF for x in range(w * h))
    bgrx = rgb_odd.tobytes("raw", "BGRX")
    bgrx = bytes(b if i % 4 != 3 else junk[i // 4] for i, b in enumerate(bgrx))
    write_bmp(path("bmp", "rgb32_bgrx_junk.bmp"), w, h, 32, [bgrx[y * w * 4:(y + 1) * w * 4] for y in range(h)])
    pal_img = rgb_odd.quantize(256)
    palette = palette_of(pal_img, 256)
    write_bmp(path("bmp", "topdown_pal8.bmp"), w, h, 8, pack_indices(pal_img, 8), palette, topdown=True)
    write_bmp(path("bmp", "pal8_gap.bmp"), w, h, 8, pack_indices(pal_img, 8), palette, gap=50)
    write_bmp(path("bmp", "os2v2_pal8.bmp"), w, h, 8, pack_indices(pal_img, 8), palette, header_size=64)
    g = Image.open(graphic).convert("RGB")
    g4 = g.quantize(16)
    pal16 = palette_of(g4, 16)
    write_bmp(path("bmp", "rle4.bmp"), g4.width, g4.height, 4, rle4_encode(g4), pal16, compression=2)
    g4small = rgb_odd.quantize(4)
    pal4 = palette_of(g4small, 4)
    write_bmp(path("bmp", "pal4_short_palette.bmp"), w, h, 4, pack_indices(g4small, 4), pal4)


# --------------------------------------------------------------------------- EXIF helper

def make_exif(orientation=None, resolution=None):
    """Minimal little-endian TIFF with IFD0: Orientation and/or X/YResolution (inches)."""
    entries = []
    if orientation is not None:
        entries.append((0x0112, 3, 1, struct.pack("<HH", orientation, 0)))
    rationals = b""
    if resolution is not None:
        count = len(entries) + 3
        base = 8 + 2 + 12 * count + 4
        for i, tag in enumerate((0x011A, 0x011B)):
            entries.append((tag, 5, 1, struct.pack("<I", base + 8 * i)))
            rationals += struct.pack("<II", resolution[i], 1)
        entries.append((0x0128, 3, 1, struct.pack("<HH", 2, 0)))
    out = io.BytesIO()
    out.write(b"II*\0" + struct.pack("<I", 8))
    out.write(struct.pack("<H", len(entries)))
    for tag, typ, count, value in entries:
        out.write(struct.pack("<HHI", tag, typ, count) + value)
    out.write(struct.pack("<I", 0))
    out.write(rationals)
    return out.getvalue()


def main():
    for tool in ("cwebp", "img2webp", "webpmux", "convert"):
        if shutil.which(tool) is None:
            sys.exit(f"missing tool: {tool}")
    for sub in ("webp", "png", "jpeg", "bmp"):
        shutil.rmtree(os.path.join(CORPUS, sub), ignore_errors=True)
    sources = generate_sources()
    generate_webp(sources)
    generate_animations()
    generate_png(sources)
    generate_jpeg()
    generate_bmp()
    count = sum(len(files) for _, _, files in os.walk(CORPUS))
    print(f"corpus: {count} files in {CORPUS}")


if __name__ == "__main__":
    main()
