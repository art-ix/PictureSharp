#!/usr/bin/env python3
"""Synthetic files for Image.Identify colour and channel reporting (0.8.5) and their expectations.

Each case writes one small file under corpus/identify/ and states the expected ImageInfo members, taken from the
settings used to write it. `references()` cross-checks them with Pillow (mode, ICC profile) where Pillow reads the
file and writes identify_reference.json. Called from generate_corpus.py ("identify") and make_reference.py.
"""
import io
import json
import os
import struct
import subprocess
import tempfile
import zlib

ROOT = os.path.dirname(os.path.abspath(__file__))
DIRECTORY = os.path.join(ROOT, "corpus", "identify")


def expect(model, channels, bits, alpha="None", icc=False, cicp=None, compression="Lossless", resolution=None,
           frames=1, sample="UnsignedInteger"):
    return {"model": model, "channels": channels, "bits": bits, "sampleFormat": sample, "alpha": alpha, "icc": icc,
            "cicp": cicp, "compression": compression, "resolution": resolution, "frames": frames}


def f32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


# --------------------------------------------------------------------------- PNG

def chunk(kind, body):
    return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))


def png(w, h, depth, color, before=(), after=(), plte=None):
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    stride = (w * depth * channels + 7) // 8
    rows = b"".join(b"\0" + bytes((x * 37 + y * 11) & 0xFF for x in range(stride)) for y in range(h))
    if color == 3:  # indices within the palette
        rows = b"".join(b"\0" + bytes(stride) for _ in range(h))
    out = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, depth, color, 0, 0, 0))
    out += b"".join(chunk(k, b) for k, b in before)
    if plte is not None:
        out += chunk(b"PLTE", plte)
    out += chunk(b"IDAT", zlib.compress(rows)) + b"".join(chunk(k, b) for k, b in after) + chunk(b"IEND", b"")
    return out


def srgb_icc():
    from PIL import ImageCms
    return ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()


def iccp():
    return (b"iCCP", b"icc\0\0" + zlib.compress(srgb_icc()))


def cicp(p, t, m=0, full=1):
    return (b"cICP", bytes((p, t, m, full)))


PLTE16 = bytes(range(48))
PLTE256 = bytes(i % 256 for i in range(768))


def png_cases():
    phys = struct.pack(">IIB", 11811, 11811, 1)
    srgb = (b"sRGB", b"\0")
    return [
        ("gray1.png", lambda: png(9, 3, 1, 0), expect("Gray", 1, 1)),
        ("gray16.png", lambda: png(4, 3, 16, 0), expect("Gray", 1, 16)),
        ("gray_alpha16.png", lambda: png(4, 3, 16, 4), expect("Gray", 2, 16, "Straight")),
        ("rgb16.png", lambda: png(4, 3, 16, 2), expect("Rgb", 3, 16)),
        ("rgba8.png", lambda: png(4, 3, 8, 6), expect("Rgb", 4, 8, "Straight")),
        ("palette4.png", lambda: png(5, 3, 4, 3, plte=PLTE16), expect("Palette", 1, 4)),
        ("palette_trns.png", lambda: png_trns(4, 3, 8, 3, b"\0\x80", PLTE256), expect("Palette", 1, 8, "Straight")),
        ("gray_trns.png", lambda: png_trns(4, 3, 8, 0, b"\0\0"), expect("Gray", 1, 8, "Straight")),
        ("rgb_trns.png", lambda: png_trns(4, 3, 8, 2, bytes(6)), expect("Rgb", 3, 8, "Straight")),
        ("cicp_srgb.png", lambda: png(4, 3, 8, 2, [cicp(1, 13)]), expect("Rgb", 3, 8, cicp=[1, 13, 0, True])),
        ("cicp_pq.png", lambda: png(4, 3, 16, 2, [cicp(9, 16)]), expect("Rgb", 3, 16, cicp=[9, 16, 0, True])),
        ("cicp_hlg_narrow.png", lambda: png(4, 3, 16, 2, [cicp(9, 18, 0, 0)]), expect("Rgb", 3, 16, cicp=[9, 18, 0, False])),
        ("cicp_iccp.png", lambda: png(4, 3, 8, 2, [cicp(9, 16), iccp()]), expect("Rgb", 3, 8, icc=True, cicp=[9, 16, 0, True])),
        ("srgb.png", lambda: png(4, 3, 8, 2, [srgb]), expect("Rgb", 3, 8, cicp=[1, 13, 0, True])),
        ("srgb_iccp.png", lambda: png(4, 3, 8, 2, [srgb, iccp()]), expect("Rgb", 3, 8, icc=True)),
        ("gama_chrm.png", lambda: png(4, 3, 8, 2, [(b"gAMA", struct.pack(">I", 45455)),
                                                   (b"cHRM", struct.pack(">8I", 31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000))]),
         expect("Rgb", 3, 8)),
        ("cicp_matrix1.png", lambda: png(4, 3, 8, 2, [cicp(1, 13, 1)]), expect("Rgb", 3, 8)),
        ("cicp_after_idat.png", lambda: png(4, 3, 8, 2, after=[cicp(1, 13)]), expect("Rgb", 3, 8)),
        ("phys_300.png", lambda: png(4, 3, 8, 2, [(b"pHYs", phys)]), expect("Rgb", 3, 8, resolution=[f32(11811 * 0.0254)] * 2)),
        ("phys_unit0.png", lambda: png(4, 3, 8, 2, [(b"pHYs", struct.pack(">IIB", 2, 1, 0))]), expect("Rgb", 3, 8)),
    ]


def png_trns(w, h, depth, color, trns, plte=None):
    data = png(w, h, depth, color, plte=plte)
    idat = data.index(b"IDAT") - 4
    return data[:idat] + chunk(b"tRNS", trns) + data[idat:]


# --------------------------------------------------------------------------- JPEG

def jpeg_cases(g):
    import numpy as np
    from PIL import Image
    plane = np.tile(np.linspace(0, 255, 16), (16, 1))
    adobe = lambda transform: (0xEE, b"Adobe\0\x64\0\0\0\0" + bytes([transform]))
    rgb = Image.fromarray(np.dstack([plane, plane.T, 255 - plane]).astype(np.uint8), "RGB")

    def pil(image, **kwargs):
        buf = io.BytesIO()
        image.save(buf, "JPEG", quality=85, **kwargs)
        return buf.getvalue()

    def crafted(channels, comps, apps):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "x.jpg")
            g["write_jpeg"](out, channels, comps, apps)
            with open(out, "rb") as f:
                return f.read()

    icc = bytes((i * 7) & 0xFF for i in range(70000))  # two APP2 chunks

    def incomplete():
        data = pil(rgb, icc_profile=icc)
        first = data.index(b"ICC_PROFILE\0") - 4
        second = data.index(b"ICC_PROFILE\0", first + 8) - 4
        length = struct.unpack(">H", data[second + 2:second + 4])[0]
        return data[:second] + data[second + 2 + length:]

    return [
        ("gray.jpg", lambda: pil(rgb.convert("L")), expect("Gray", 1, 8, compression="Lossy")),
        ("ycbcr_300dpi.jpg", lambda: pil(rgb, dpi=(300, 300)), expect("Rgb", 3, 8, compression="Lossy", resolution=[300.0, 300.0])),
        ("adobe_rgb.jpg", lambda: crafted([plane] * 3, [(1, 1, 1, 0), (2, 1, 1, 0), (3, 1, 1, 0)], [adobe(0)]),
         expect("Rgb", 3, 8, compression="Lossy")),
        ("cmyk.jpg", lambda: pil(rgb.convert("CMYK")), expect("Cmyk", 4, 8, compression="Lossy")),
        ("ycck.jpg", lambda: crafted([plane] * 4, [(1, 1, 1, 0), (2, 1, 1, 1), (3, 1, 1, 1), (4, 1, 1, 0)], [adobe(2)]),
         expect("Cmyk", 4, 8, compression="Lossy")),
        ("cmyk_no_adobe.jpg", lambda: crafted([plane] * 4, [(1, 1, 1, 0), (2, 1, 1, 0), (3, 1, 1, 0), (4, 1, 1, 0)], []),
         expect("Cmyk", 4, 8, compression="Lossy")),
        ("icc_two_chunks.jpg", lambda: pil(rgb, icc_profile=icc), expect("Rgb", 3, 8, icc=True, compression="Lossy")),
        ("icc_incomplete.jpg", incomplete, expect("Rgb", 3, 8, icc=True, compression="Lossy")),
    ]


# --------------------------------------------------------------------------- WebP

def webp_cases():
    from PIL import Image

    def frame(color, alpha):
        return Image.new("RGBA", (16, 12), color + (alpha,))

    def tool(*args, inputs):
        with tempfile.TemporaryDirectory() as tmp:
            names = []
            for i, image in enumerate(inputs):
                names.append(os.path.join(tmp, f"{i}.png"))
                image.save(names[-1])
            out = os.path.join(tmp, "out.webp")
            args = [a.format(*names, out=out, tmp=tmp) for a in args]
            with open(os.path.join(tmp, "icc.icc"), "wb") as f:
                f.write(srgb_icc())
            for command in args_split(args):
                subprocess.run(command, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            with open(out, "rb") as f:
                return f.read()

    rgb, rgba = frame((200, 40, 40), 255), frame((40, 200, 40), 128)
    return [
        ("lossy.webp", lambda: tool("cwebp", "-quiet", "-q", "80", "{0}", "-o", "{out}", inputs=[rgb.convert("RGB")]),
         expect("Rgb", 3, 8, compression="Lossy")),
        ("lossy_alpha.webp", lambda: tool("cwebp", "-quiet", "-q", "80", "{0}", "-o", "{out}", inputs=[rgba]),
         expect("Rgb", 4, 8, "Straight", compression="Lossy")),
        ("lossless_alpha.webp", lambda: tool("cwebp", "-quiet", "-lossless", "{0}", "-o", "{out}", inputs=[rgba]),
         expect("Rgb", 4, 8, "Straight")),
        ("icc.webp", lambda: tool("cwebp", "-quiet", "-q", "80", "{0}", "-o", "{tmp}/plain.webp", ";",
                                  "webpmux", "-set", "icc", "{tmp}/icc.icc", "{tmp}/plain.webp", "-o", "{out}",
                                  inputs=[rgb.convert("RGB")]),
         expect("Rgb", 3, 8, icc=True, compression="Lossy")),
        ("anim_mixed.webp", lambda: tool("img2webp", "-loop", "0", "-lossless", "{0}", "-lossy", "{1}", "-o", "{out}",
                                         inputs=[rgba, frame((40, 40, 200), 64)]),
         expect("Rgb", 4, 8, "Straight", compression="Mixed", frames=2)),
        ("anim_lossless.webp", lambda: tool("img2webp", "-loop", "0", "-lossless", "{0}", "{1}", "-o", "{out}",
                                            inputs=[rgba, frame((40, 40, 200), 64)]),
         expect("Rgb", 4, 8, "Straight", frames=2)),
    ]


def args_split(args):
    command = []
    for a in args:
        if a == ";":
            yield command
            command = []
        else:
            command.append(a)
    yield command


# --------------------------------------------------------------------------- GIF

def gif_cases(g):
    header, image, gce, app, sub = g["gif_header"], g["gif_image"], g["gif_gce"], g["gif_app"], g["gif_sub_blocks"]
    two = [(0, 0, 0), (255, 255, 255)]
    sixteen = [(i * 16, i * 8, 255 - i * 16) for i in range(16)]
    four = sixteen[:4]
    full = [(i, 255 - i, i // 2) for i in range(256)]
    px = lambda n, colors: [i % colors for i in range(n)]
    return [
        ("table2.gif", lambda: header(6, 4, two, 1) + image(0, 0, 6, 4, px(24, 2), 2) + b";", expect("Palette", 1, 1)),
        ("table16.gif", lambda: header(6, 4, sixteen, 4) + image(0, 0, 6, 4, px(24, 16), 4) + b";", expect("Palette", 1, 4)),
        ("global4_local256.gif", lambda: header(6, 4, four, 2) + image(0, 0, 6, 4, px(24, 4), 2) +
         image(0, 0, 6, 4, px(24, 256), 8, full, 8) + b";", expect("Palette", 1, 8, frames=2)),
        ("transparent.gif", lambda: header(6, 4, four, 2) + gce(0, 1) + image(0, 0, 6, 4, px(24, 4), 2) + b";",
         expect("Palette", 1, 2, "Straight")),
        ("transparent_frame2.gif", lambda: header(6, 4, four, 2) + gce(10) + image(0, 0, 6, 4, px(24, 4), 2) +
         gce(10, 2) + image(0, 0, 6, 4, px(24, 4), 2) + b";", expect("Palette", 1, 2, "Straight", frames=2)),
        ("icc.gif", lambda: header(6, 4, four, 2) + app(b"ICCRGBG1012", sub(srgb_icc())) + image(0, 0, 6, 4, px(24, 4), 2) + b";",
         expect("Palette", 1, 2, icc=True)),
    ]


# --------------------------------------------------------------------------- BMP

def bmp(width, height, bits, rows, palette=(), header_size=40, compression=0, masks=None, ppm=(2835, 2835), tail=b"",
        profile=b""):
    """A bottom-up BMP from packed top-down rows; `tail` follows the masks in V4/V5 headers, `profile` the pixels."""
    stride = ((width * bits + 31) // 32) * 4
    data = rows if compression in (1, 2) else b"".join(r + bytes(stride - len(r)) for r in rows[::-1])
    hdr = struct.pack("<IiiHHIIiiII", header_size, width, height, 1, bits, compression, len(data), ppm[0], ppm[1],
                      len(palette), 0)
    if masks is not None:
        hdr += struct.pack("<4I", *masks)
    hdr += tail
    hdr += bytes(header_size - len(hdr))
    pal = b"".join(bytes((b, gr, r, 0)) for r, gr, b in palette)
    offset = 14 + len(hdr) + len(pal)
    body = hdr + pal + data + profile
    return b"BM" + struct.pack("<IHHI", 14 + len(body), 0, 0, offset) + body


def v45(cstype, extra=b""):
    # bV4CSType, CIEXYZTRIPLE endpoints (36 bytes), three gamma values (12 bytes); V5 adds intent and the profile.
    return struct.pack("<I", cstype) + bytes(48) + extra


def bmp_cases():
    pal = lambda n: [(i * 255 // max(1, n - 1), 0, 255 - i * 255 // max(1, n - 1)) for i in range(n)]
    rows = lambda w, h, bpp: [bytes(((w * bpp + 7) // 8)) for _ in range(h)]
    rgb24 = [bytes((x * 40 + y * 60) % 256 for x in range(12)) for y in range(3)]

    def v5_profile(cstype, profile):
        width, height = 4, 3
        data_size = 3 * 12
        profile_offset = 124 + data_size  # from the start of the header, after the pixels
        extra = struct.pack("<IIII", 4, profile_offset, len(profile), 0)
        return bmp(width, height, 24, rgb24, header_size=124, masks=(0, 0, 0, 0), tail=v45(cstype, extra), profile=profile)

    rle8 = b"\x04\x01\x00\x00" * 3 + b"\x00\x01"
    return [
        ("palette1.bmp", lambda: bmp(9, 3, 1, rows(9, 3, 1), pal(2)), expect("Palette", 1, 1, resolution=[f32(2835 * 0.0254)] * 2)),
        ("palette4.bmp", lambda: bmp(5, 3, 4, rows(5, 3, 4), pal(16)), expect("Palette", 1, 4, resolution=[f32(2835 * 0.0254)] * 2)),
        ("rle8.bmp", lambda: bmp(4, 3, 8, rle8, pal(256), compression=1), expect("Palette", 1, 8, resolution=[f32(2835 * 0.0254)] * 2)),
        ("rgb555.bmp", lambda: bmp(4, 3, 16, rows(4, 3, 16)), expect("Rgb", 3, 5, resolution=[f32(2835 * 0.0254)] * 2)),
        ("rgb565.bmp", bmp565, expect("Rgb", 3, 6, resolution=[f32(2835 * 0.0254)] * 2)),
        ("rgb10a2.bmp", lambda: bmp(4, 3, 32, rows(4, 3, 32), header_size=56, compression=3,
                                    masks=(0x3FF00000, 0xFFC00, 0x3FF, 0xC0000000)),
         expect("Rgb", 4, 10, "Straight", resolution=[f32(2835 * 0.0254)] * 2)),
        ("rgb24.bmp", lambda: bmp(4, 3, 24, rgb24), expect("Rgb", 3, 8, resolution=[f32(2835 * 0.0254)] * 2)),
        ("rgb32.bmp", lambda: bmp(4, 3, 32, rows(4, 3, 32)), expect("Rgb", 3, 8, resolution=[f32(2835 * 0.0254)] * 2)),
        ("argb32.bmp", lambda: bmp(4, 3, 32, rows(4, 3, 32), header_size=56, compression=3,
                                   masks=(0xFF0000, 0xFF00, 0xFF, 0xFF000000)),
         expect("Rgb", 4, 8, "Straight", resolution=[f32(2835 * 0.0254)] * 2)),
        ("no_resolution.bmp", lambda: bmp(4, 3, 24, rgb24, ppm=(0, 0)), expect("Rgb", 3, 8)),
        ("v4_srgb.bmp", lambda: bmp(4, 3, 24, rgb24, header_size=108, masks=(0, 0, 0, 0), tail=v45(0x73524742)),
         expect("Rgb", 3, 8, cicp=[1, 13, 0, True], resolution=[f32(2835 * 0.0254)] * 2)),
        ("v4_windows_color_space.bmp", lambda: bmp(4, 3, 24, rgb24, header_size=108, masks=(0, 0, 0, 0),
                                                   tail=v45(0x57696E20)),
         expect("Rgb", 3, 8, resolution=[f32(2835 * 0.0254)] * 2)),
        ("v5_embedded_icc.bmp", lambda: v5_profile(0x4D424544, srgb_icc()),
         expect("Rgb", 3, 8, icc=True, resolution=[f32(2835 * 0.0254)] * 2)),
        ("v5_linked_profile.bmp", lambda: v5_profile(0x4C494E4B, b"C:\\profile.icc\0"),
         expect("Rgb", 3, 8, resolution=[f32(2835 * 0.0254)] * 2)),
        ("embedded_png.bmp", lambda: bmp_png(), expect("Rgb", 4, 8, "Straight")),
    ]


def bmp565():
    # BITMAPINFOHEADER with three BI_BITFIELDS masks after it (12 bytes), as Windows writes 5-6-5.
    data = bytes(8) * 3
    hdr = struct.pack("<IiiHHIIiiII", 40, 4, 3, 1, 16, 3, len(data), 2835, 2835, 0, 0) + struct.pack("<3I", 0xF800, 0x7E0, 0x1F)
    offset = 14 + len(hdr)
    return b"BM" + struct.pack("<IHHI", offset + len(data), 0, 0, offset) + hdr + data


def bmp_png():
    data = png(4, 3, 8, 6)
    hdr = struct.pack("<IiiHHIIiiII", 40, 4, 3, 1, 0, 5, len(data), 0, 0, 0, 0)
    offset = 14 + len(hdr)
    return b"BM" + struct.pack("<IHHI", offset + len(data), 0, 0, offset) + hdr + data


# --------------------------------------------------------------------------- ICO and PCX

def ico_cases(ico):
    return [
        ("dib4.ico", lambda: ico["wrap"]([ico["dib"](4)]), expect("Palette", 2, 4, "Straight")),
        ("dib8.ico", lambda: ico["wrap"]([ico["dib"](8)]), expect("Palette", 2, 8, "Straight")),
        ("dib24.ico", lambda: ico["wrap"]([ico["dib"](24)]), expect("Rgb", 4, 8, "Straight")),
        ("dib32.ico", lambda: ico["wrap"]([ico["dib"](32)]), expect("Rgb", 4, 8, "Straight")),
        ("png_gray.ico", lambda: ico["wrap"]([(4, 3, 32, png(4, 3, 8, 0))]), expect("Gray", 1, 8)),
    ]


def pcx(width, height, bits, planes, palette16=bytes(48), vga=None, dpi=(0, 0)):
    stride = ((width * bits + 7) // 8 + 1) & ~1
    header = struct.pack("<BBBBHHHHHH", 10, 5, 1, bits, 0, 0, width - 1, height - 1, dpi[0], dpi[1]) + palette16
    header += struct.pack("<BBHH", 0, planes, stride, 1) + bytes(58)
    body = bytearray()
    for y in range(height):
        for p in range(planes):
            for x in range(stride):
                body += bytes((0xC1, (x * 29 + y * 7 + p * 3) & 0xFF))
    if vga is not None:
        body += b"\x0c" + vga
    return header + bytes(body)


def pcx_cases():
    ega = bytes(i * 5 % 256 for i in range(48))
    return [
        ("mono.pcx", lambda: pcx(9, 3, 1, 1), expect("Palette", 1, 1)),
        ("bits2.pcx", lambda: pcx(5, 3, 2, 1, ega), expect("Palette", 1, 2)),
        ("bits4.pcx", lambda: pcx(5, 3, 4, 1, ega), expect("Palette", 1, 4)),
        ("planar4x1.pcx", lambda: pcx(9, 3, 1, 4, ega), expect("Palette", 1, 4)),
        ("palette8.pcx", lambda: pcx(4, 3, 8, 1, vga=PLTE256), expect("Palette", 1, 8)),
        ("rgb.pcx", lambda: pcx(4, 3, 8, 3), expect("Rgb", 3, 8)),
        ("rgba_300dpi.pcx", lambda: pcx(4, 3, 8, 4, dpi=(300, 300)), expect("Rgb", 4, 8, "Straight", resolution=[300.0, 300.0])),
    ]


# --------------------------------------------------------------------------- driver

def cases(g=None, ico=None):
    out = [("png/" + n, w, e) for n, w, e in png_cases()]
    out += [("webp/" + n, w, e) for n, w, e in webp_cases()]
    out += [("bmp/" + n, w, e) for n, w, e in bmp_cases()]
    out += [("pcx/" + n, w, e) for n, w, e in pcx_cases()]
    if g is not None:
        out += [("jpeg/" + n, w, e) for n, w, e in jpeg_cases(g)]
        out += [("gif/" + n, w, e) for n, w, e in gif_cases(g)]
    if ico is not None:
        out += [("ico/" + n, w, e) for n, w, e in ico_cases(ico)]
    return out


def all_cases(g):
    import runpy
    return cases(g, runpy.run_path(os.path.join(ROOT, "ico_corpus.py")))


def generate(g):
    """g: the globals of generate_corpus.py (JPEG and GIF writers)."""
    for name, write, _ in all_cases(g):
        target = os.path.join(DIRECTORY, name)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "wb") as f:
            f.write(write())


PILLOW_MODELS = {"1": "Gray", "L": "Gray", "LA": "Gray", "I": "Gray", "I;16": "Gray", "I;16B": "Gray", "P": "Palette",
                 "PA": "Palette", "RGB": "Rgb", "RGBA": "Rgb", "CMYK": "Cmyk"}


def references(g):
    """Cross-checks the expectations with Pillow and writes identify_reference.json."""
    from PIL import Image
    refs = {}
    for name, _, expected in all_cases(g):
        path = os.path.join(DIRECTORY, name)
        if name.startswith(("ico/", "bmp/embedded")):
            pillow = None  # Pillow converts ICO entries and does not read BI_PNG
        else:
            try:
                with Image.open(path) as im:
                    pillow = (im.mode, "icc_profile" in im.info and bool(im.info["icc_profile"]),
                              im.mode in ("RGBA", "LA", "PA") or "transparency" in im.info)
            except Exception:
                pillow = None
        if pillow is not None:
            mode, icc, alpha = pillow
            model = PILLOW_MODELS.get(mode)
            # Pillow 10.2 opens 16-bit grey + alpha PNG as RGBA, and 1-bit PCX as black and white although the decoder
            # uses the two header palette entries.
            quirk = (name, mode) in (("png/gray_alpha16.png", "RGBA"), ("pcx/mono.pcx", "1"))
            if model is not None and model != expected["model"] and not quirk:
                raise RuntimeError(f"{name}: Pillow mode {mode}, expected {expected['model']}")
            if icc and not expected["icc"]:
                raise RuntimeError(f"{name}: Pillow reads an ICC profile")
            if alpha and expected["alpha"] == "None":
                raise RuntimeError(f"{name}: Pillow reports alpha")
        refs[name] = dict(expected, pillowMode=pillow[0] if pillow else None)
    with open(os.path.join(ROOT, "identify_reference.json"), "w") as f:
        json.dump(dict(sorted(refs.items())), f, indent=1)
    print(f"identify_reference.json: {len(refs)} entries")
