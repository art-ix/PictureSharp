#!/usr/bin/env python3
"""Computes reference decodes of the test corpus into tests/assets/reference.json.

* static WebP  -> `dwebp -pam` (libwebp 1.6.0), cross-checked with Pillow
* animated WebP -> Pillow (WebPAnimDecoder: fully composited canvas frames)
* PNG           -> Pillow (8-bit) / ImageMagick (16-bit, scaled with round(v/257)); APNG via Pillow
* BMP           -> Pillow (ImageMagick scales 4/5-bit channels differently, so it is no oracle for BMP;
                   variants Pillow cannot read are covered by unit tests)
* PCX           -> Pillow; RGBA hashes and header DPI
* ICO           -> Pillow; selected variant here and all variants in ico_reference.json
* JPEG          -> Pillow (libjpeg-turbo, islow IDCT, fancy upsampling); RGBA also saved as
                   PNG under reference/jpeg/ for tolerance comparisons
* GIF           -> anim_dump (libwebp 1.6.0 anim_util.c: browser-style composition, transparent pixels as
                   (0,0,0,0)), cross-checked with gif2webp -> WebP and, for still images, Pillow. Files anim_dump
                   cannot read (no color table, indices past the table, garbage instead of a trailer) use Pillow.
                   Durations are the raw GCE delays; the loop count is gif2webp's number of plays.

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
for tool in ['dwebp', 'gif2webp']:
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


def ref_pcx(path, rel):
    with Image.open(path) as im:
        rgba = im.convert("RGBA").tobytes()
        entry = {"format": "PCX", "width": im.width, "height": im.height, "hasAlpha": False,
                 "animated": False, "frames": [{"sha256": sha(rgba), "duration": 0}], "decoder": "pillow"}
        dpi = im.info.get("dpi")
        if dpi and dpi[0] > 0 and dpi[1] > 0:
            entry["dpi"] = [float(dpi[0]), float(dpi[1])]
        return entry


def gif_structure(data):
    """Block structure as RasterSharp's GifFile parses it (for the metadata fields of the reference)."""
    width, height = struct.unpack_from("<HH", data, 6)
    pos, flags = 13, data[10]
    gct = None
    if flags & 0x80:
        n = 2 << (flags & 7)
        gct = [tuple(data[pos + 3 * i:pos + 3 * i + 3]) for i in range(n)]
        pos += 3 * n
    frames, loops, xmp, icc = [], None, None, None
    delay, dispose, transparent = 0, 0, -1
    while pos < len(data):
        block = data[pos]
        pos += 1
        if block == 0x21 and pos < len(data):
            label, first = data[pos], pos + 1
            pos += 1
            if label == 0xF9 and data[first] == 4:
                packed = data[first + 1]
                dispose = (packed >> 2) & 7 if (packed >> 2) & 7 in (2, 3) else 0
                delay = struct.unpack_from("<H", data, first + 2)[0]
                transparent = data[first + 4] if packed & 1 else -1
            elif label == 0xFF and data[first] == 11:
                ident, p = data[first + 1:first + 12], first + 12
                if ident in (b"NETSCAPE2.0", b"ANIMEXTS1.0") and loops is None and data[p] >= 3 and data[p + 1] == 1:
                    loops = struct.unpack_from("<H", data, p + 2)[0]
                elif ident == b"XMP DataXMP" and xmp is None:
                    start = p
                    while data[p]:
                        p += data[p] + 1
                    xmp = data[start:p - 257] if p - start > 257 else data[start:p]
                elif ident == b"ICCRGBG1012" and icc is None:
                    icc = b""
                    while data[p]:
                        icc += data[p + 1:p + 1 + data[p]]
                        p += data[p] + 1
            while data[pos]:
                pos += data[pos] + 1
            pos += 1
        elif block == 0x2C:
            x, y, w, h, f = struct.unpack_from("<HHHHB", data, pos)
            pos += 9
            if f & 0x80:
                pos += 3 * (2 << (f & 7))
            pos += 1
            while data[pos]:
                pos += data[pos] + 1
            pos += 1
            frames.append({"x": x, "y": y, "w": w, "h": h, "delay": delay, "dispose": dispose, "transparent": transparent})
            delay, dispose, transparent = 0, 0, -1
        else:
            break
    if width == 0 or height == 0:
        frames[0].update(x=0, y=0)
        width, height = frames[0]["w"], frames[0]["h"]
    return width, height, gct, data[11], frames, loops, xmp, icc


def anim_dump(path):
    """Composited frames from anim_dump (RGBA bytes), or None when it cannot read the file."""
    with tempfile.TemporaryDirectory() as tmp:
        if subprocess.run(["anim_dump", "-folder", tmp, "-pam", path], capture_output=True).returncode != 0:
            return None
        return [read_pam(os.path.join(tmp, name))[2] for name in sorted(os.listdir(tmp))]


def canonical_rgba(frame):
    a = np.array(frame.convert("RGBA"))
    a[a[..., 3] == 0] = 0
    return a.tobytes()


# anim_util.c reads past the color table for these (undefined behavior); Pillow shows such indices as black.
GIF_PILLOW_ONLY = {"gif/hand_index_beyond_table.gif"}


def ref_gif(path, rel):
    with open(path, "rb") as f:
        data = f.read()
    width, height, gct, bg, frames, loops, xmp, icc = gif_structure(data)
    first = frames[0]
    has_alpha = (first["x"] > 0 or first["y"] > 0 or first["x"] + first["w"] < width or first["y"] + first["h"] < height
                 or any(f["transparent"] >= 0 or f["dispose"] for f in frames))
    entry = {"format": "GIF", "width": width, "height": height, "hasAlpha": has_alpha, "animated": len(frames) > 1}
    if len(frames) > 1:
        # gif2webp: no loop extension = play once; otherwise the repeat count plus the first play.
        entry["loop"] = 1 if loops is None else 0 if loops == 0 else min(loops + 1, 65535)
    if first["transparent"] >= 0 and bg == first["transparent"]:
        entry["background"] = [0, 0, 0, 0]
    elif gct is not None and bg < len(gct):
        entry["background"] = list(gct[bg]) + [255]
    else:
        entry["background"] = [255, 255, 255, 255]
    if xmp is not None:
        entry["xmpSha256"] = sha(xmp)
    if icc is not None:
        entry["iccSha256"] = sha(icc)

    pixels = None if rel in GIF_PILLOW_ONLY else anim_dump(path)
    decoder = "anim_dump"
    with tempfile.TemporaryDirectory() as tmp:
        webp = os.path.join(tmp, "out.webp")
        ok = subprocess.run(["gif2webp", "-quiet", "-mt", path, "-o", webp], capture_output=True).returncode == 0
        converted = anim_dump(webp) if ok else None
    if pixels is None:
        pixels = [canonical_rgba(f) for f in ImageSequence.Iterator(Image.open(path))]
        decoder = "pillow"
    elif converted is not None and converted != pixels:
        # anim_util.c restores "previous" from the frame drawn before a dispose-to-background frame was cleared;
        # gif2webp (and browsers) restore the disposed canvas.
        print(f"note: anim_dump and gif2webp differ for {rel}; using gif2webp")
        pixels, decoder = converted, "gif2webp"
    if len(frames) == 1 and Image.open(path).size == (width, height):   # Pillow grows a 0x0 screen to the frame extent
        pil = canonical_rgba(Image.open(path))
        if pil != pixels[0]:
            print(f"warning: Pillow differs for {rel}")
    assert len(pixels) == len(frames), rel
    entry.update(frames=[{"sha256": sha(p), "duration": f["delay"] * 10} for p, f in zip(pixels, frames)], decoder=decoder)
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
            elif name.endswith(".ico"):
                import io
                with Image.open(path) as im:
                    entry = im.ico.entry[0]
                    with open(path, "rb") as source:
                        source.seek(entry["offset"])
                        payload = source.read(entry["size"])
                    # Pillow's ICO wrapper drops palette PNG transparency; use its PNG decoder.
                    if payload.startswith(b"\x89PNG\r\n\x1a\n"):
                        with Image.open(io.BytesIO(payload)) as png:
                            pixels = png.convert("RGBA").tobytes()
                    else:
                        pixels = im.convert("RGBA").tobytes()
                    refs[rel] = {"format": "ICO", "width": im.width, "height": im.height,
                                 "hasAlpha": True, "animated": False, "decoder": "Pillow (embedded PNG for PNG entries)",
                                 "frames": [{"sha256": sha(pixels), "duration": 0}]}
            elif name.endswith(".pcx"):
                refs[rel] = ref_pcx(path, rel)
            elif name.endswith(".gif"):
                refs[rel] = ref_gif(path, rel)
    with open(os.path.join(ROOT, "reference.json"), "w") as f:
        json.dump(dict(sorted(refs.items())), f, indent=1)
    import runpy
    runpy.run_path(os.path.join(ROOT, "ico_corpus.py"))["references"]()
    print(f"reference.json: {len(refs)} entries")


if __name__ == "__main__":
    main()
