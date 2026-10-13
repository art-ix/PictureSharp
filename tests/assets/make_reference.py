#!/usr/bin/env python3
"""Computes reference decodes of the test corpus into tests/assets/reference.json.

* static WebP  -> `dwebp -pam` (libwebp 1.6.0), cross-checked with Pillow
* animated WebP -> Pillow (WebPAnimDecoder: fully composited canvas frames)
* PNG           -> Pillow (8-bit) / ImageMagick (16-bit, scaled with round(v/257)); APNG via Pillow
* BMP           -> Pillow (ImageMagick scales 4/5-bit channels differently, so it is no oracle for BMP;
                   variants Pillow cannot read are covered by unit tests)
* JPEG          -> Pillow (libjpeg-turbo, islow IDCT, fancy upsampling); RGBA also saved as
                   PNG under reference/jpeg/ for tolerance comparisons

Each entry stores dimensions, flags and the SHA-256 of tightly packed RGBA pixels per frame.
Run with:  python3 -I tests/assets/make_reference.py
"""
import hashlib
import json
import os
import struct
import subprocess
import tempfile

import numpy as np
from PIL import Image, ImageSequence

ROOT = os.path.dirname(os.path.abspath(__file__))

# Match the C# oracle selection without changing the system installation.
WEBP_TOOLS = os.environ.get("RASTERSHARP_WEBP_TOOLS", os.path.join(ROOT, "..", "..", "reference", "libwebp-1.6.0-build"))
if os.path.isdir(WEBP_TOOLS):
    os.environ["PATH"] = os.path.abspath(WEBP_TOOLS) + os.pathsep + os.environ.get("PATH", "")
for tool in ['dwebp']:
    version = subprocess.check_output([tool, "-version"], text=True).splitlines()[0].strip().rsplit(" ", 1)[-1]
    if version != "1.6.0":
        raise RuntimeError(f"{tool} {version}: expected 1.6.0; run scripts/build-webp-reference.sh")

CORPUS = os.path.join(ROOT, "corpus")
REFDIR = os.path.join(ROOT, "reference")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read_pam(path):
    with open(path, "rb") as f:
        data = f.read()
    end = data.index(b"ENDHDR\n") + len(b"ENDHDR\n")
    header = dict(line.split(b" ", 1) for line in data[:end].split(b"\n")[1:-2])
    w, h, depth = int(header[b"WIDTH"]), int(header[b"HEIGHT"]), int(header[b"DEPTH"])
    assert depth == 4, path
    return w, h, data[end:end + w * h * 4]


def webp_chunks(data):
    """Yields (fourcc, payload) for top-level RIFF chunks and the chunks inside ANMF frames."""
    pos = 12
    while pos + 8 <= len(data):
        fourcc = data[pos:pos + 4].decode("latin1")
        size = struct.unpack_from("<I", data, pos + 4)[0]
        payload = data[pos + 8:pos + 8 + size]
        yield fourcc, payload
        if fourcc == "ANMF":
            inner = 16
            while inner + 8 <= len(payload):
                f2 = payload[inner:inner + 4].decode("latin1")
                s2 = struct.unpack_from("<I", payload, inner + 4)[0]
                yield f2, payload[inner + 8:inner + 8 + s2]
                inner += 8 + s2 + (s2 & 1)
        pos += 8 + size + (size & 1)


def webp_compression(data):
    kinds = {fourcc for fourcc, _ in webp_chunks(data) if fourcc in ("VP8 ", "VP8L")}
    if kinds == {"VP8 "}:
        return "Lossy"
    if kinds == {"VP8L"}:
        return "Lossless"
    return "Mixed"


def ref_webp(path, rel):
    with open(path, "rb") as f:
        data = f.read()
    im = Image.open(path)
    entry = {"format": "WebP", "width": im.width, "height": im.height,
             "hasAlpha": im.mode == "RGBA", "compression": webp_compression(data)}
    n = getattr(im, "n_frames", 1)
    if n > 1:
        frames = []
        for frame in ImageSequence.Iterator(im):
            rgba = frame.convert("RGBA").tobytes()
            frames.append({"sha256": sha(rgba), "duration": frame.info.get("duration", 0)})
        entry.update(animated=True, loop=im.info.get("loop", 0), frames=frames, decoder="pillow")
        bg = im.info.get("background")
        if bg is not None:
            # Pillow reports the ANIM background as an (R,G,B,A) tuple
            entry["background"] = list(bg) if isinstance(bg, tuple) else bg
    else:
        with tempfile.TemporaryDirectory() as tmp:
            pam = os.path.join(tmp, "out.pam")
            subprocess.run(["dwebp", "-quiet", "-pam", path, "-o", pam], check=True)
            w, h, rgba = read_pam(pam)
        assert (w, h) == (im.width, im.height), rel
        pil = im.convert("RGBA").tobytes()
        if pil != rgba:
            print(f"warning: Pillow and dwebp differ for {rel}")
        entry.update(animated=False, frames=[{"sha256": sha(rgba), "duration": 0}], decoder="dwebp")
    return entry


def png_info(path):
    with open(path, "rb") as f:
        data = f.read()
    ihdr = data[16:29]
    w, h, depth, ctype, _, _, interlace = struct.unpack(">IIBBBBB", ihdr)
    has_trns = b"tRNS" in data
    return w, h, depth, ctype, interlace, has_trns


def ref_png(path, rel):
    w, h, depth, ctype, interlace, has_trns = png_info(path)
    im = Image.open(path)
    entry = {"format": "PNG", "width": w, "height": h, "bitDepth": depth, "colorType": ctype,
             "interlaced": bool(interlace), "hasAlpha": ctype in (4, 6) or has_trns}
    n = getattr(im, "n_frames", 1)
    if n > 1:
        frames = []
        for frame in ImageSequence.Iterator(im):
            frames.append({"sha256": sha(frame.convert("RGBA").tobytes()), "duration": frame.info.get("duration", 0)})
        entry.update(animated=True, loop=im.info.get("loop", 0), frames=frames, decoder="pillow")
        return entry
    if depth == 16:
        raw = subprocess.run(["convert", path, "-depth", "16", "-endian", "MSB", "RGBA:-"], check=True, capture_output=True).stdout
        v = np.frombuffer(raw, dtype=">u2").astype(np.uint32)
        rgba = ((v + 128) // 257).astype(np.uint8).tobytes()
        decoder = "imagemagick16"
    else:
        rgba = im.convert("RGBA").tobytes()
        decoder = "pillow"
    entry.update(animated=False, frames=[{"sha256": sha(rgba), "duration": 0}], decoder=decoder)
    dpi = im.info.get("dpi")
    if dpi:
        entry["dpi"] = [round(dpi[0], 2), round(dpi[1], 2)]
    exif = im.getexif()
    if exif.get(0x112):
        entry["orientation"] = exif[0x112]
    return entry


def ref_jpeg(path, rel):
    im = Image.open(path)
    rgba = im.convert("RGBA")
    out = os.path.join(REFDIR, os.path.splitext(rel)[0] + ".png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    rgba.save(out)
    entry = {"format": "JPEG", "width": im.width, "height": im.height, "hasAlpha": False,
             "mode": im.mode, "progressive": bool(im.info.get("progressive") or im.info.get("progression")),
             "animated": False, "frames": [{"sha256": sha(rgba.tobytes()), "duration": 0}], "decoder": "pillow",
             "referencePng": os.path.relpath(out, ROOT)}
    exif = im.getexif()
    # Pillow reports 72 DPI when EXIF lacks a resolution; only densities stored in the file are references.
    dpi = im.info.get("dpi")
    if dpi and (im.info.get("jfif_unit") == 1 or 0x011A in exif):
        entry["dpi"] = [round(float(dpi[0]), 2), round(float(dpi[1]), 2)]
    if exif.get(0x112):
        entry["orientation"] = exif[0x112]
    if im.info.get("icc_profile"):
        entry["iccSha256"] = sha(im.info["icc_profile"])
    xmp_header = b"http://ns.adobe.com/xap/1.0/\0"
    for app, content in im.applist:
        if app == "APP1" and content.startswith(xmp_header):
            entry["xmpSha256"] = sha(content[len(xmp_header):])
            break
    return entry


def bmp_info(data):
    header_size = struct.unpack_from("<I", data, 14)[0]
    if header_size == 12:
        bits, compression = struct.unpack_from("<H", data, 24)[0], 0
    else:
        bits, compression = struct.unpack_from("<HI", data, 28)
    has_alpha = compression in (3, 6) and (header_size >= 56 or compression == 6) and struct.unpack_from("<I", data, 66)[0] != 0
    return header_size, bits, compression, has_alpha


def ref_bmp(path, rel):
    with open(path, "rb") as f:
        data = f.read()
    header_size, bits, compression, has_alpha = bmp_info(data)
    entry = {"format": "BMP", "bitDepth": bits, "compression": str(compression), "headerSize": header_size,
             "hasAlpha": has_alpha, "animated": False}
    im = Image.open(path)
    rgba = im.convert("RGBA").tobytes()
    dpi = im.info.get("dpi")
    if dpi and dpi[0] > 0:
        entry["dpi"] = [round(dpi[0], 2), round(dpi[1], 2)]
    entry.update(width=im.width, height=im.height, frames=[{"sha256": sha(rgba), "duration": 0}], decoder="pillow")
    return entry


def main():
    refs = {}
    for dirpath, _, files in os.walk(CORPUS):
        for name in sorted(files):
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, CORPUS)
            if rel.startswith("src" + os.sep):
                continue
            if name.endswith(".webp"):
                refs[rel] = ref_webp(path, rel)
            elif name.endswith(".png"):
                refs[rel] = ref_png(path, rel)
            elif name.endswith(".bmp"):
                refs[rel] = ref_bmp(path, rel)
            elif name.endswith(".jpg"):
                refs[rel] = ref_jpeg(path, rel)
    with open(os.path.join(ROOT, "reference.json"), "w") as f:
        json.dump(dict(sorted(refs.items())), f, indent=1)
    print(f"reference.json: {len(refs)} entries")


if __name__ == "__main__":
    main()
