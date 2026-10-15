#!/usr/bin/env python3
"""Generates the color-management corpus in tests/assets/corpus/color/ (0.6.2).

Writes synthetic ICC profiles (RGB matrix/TRC and Gray/TRC, v2 and v4, every supported curve type), the input
images, LittleCMS conversions of those inputs to sRGB as reference PNGs, PNG files with gAMA/cHRM/sRGB/iCCP chunks,
malformed and unsupported profiles, and color_reference.json describing the reference configuration.

LittleCMS is only the oracle: it runs through Pillow's ImageCms with INTENT_RELATIVE_COLORIMETRIC, no black-point
compensation and no flags, from the source profile to LittleCMS's built-in sRGB profile, in 8-bit RGB/L modes.
gAMA/cHRM cases are converted through an equivalent matrix/TRC profile built here.

  python3 -I tests/assets/color_reference.py              # regenerate the corpus
  python3 -I tests/assets/color_reference.py --calibrate  # compare LittleCMS with an exact double-precision model
"""
import io
import json
import os
import struct
import sys
import zlib

import numpy as np
from PIL import Image, ImageCms

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "corpus", "color")
D50 = (0.9642, 1.0, 0.8249)
BRADFORD = np.array([[0.8951, 0.2664, -0.1614], [-0.7502, 1.7135, 0.0367], [0.0389, -0.0685, 1.0296]])

# Tolerance fixed before the implementation (plan.md 0.6.2), from --calibrate: LittleCMS's optimized 8-bit
# matrix/TRC path stays within 1 of the exact model; its 16-bit Gray path (reversed sRGB curve) deviates by up to 2
# near black.
TOLERANCE = {"rgb_max_abs": 1, "gray_max_abs": 2, "max_mismatch_fraction": 0.07}


# ------------------------------------------------------------------ math

def xy_to_xyz(x, y):
    return np.array([x / y, 1.0, (1.0 - x - y) / y])


def rgb_to_xyz(primaries, white):
    m = np.column_stack([xy_to_xyz(*p) for p in primaries])
    s = np.linalg.solve(m, xy_to_xyz(*white))
    return m * s


def bradford(src_white_xyz, dst_white_xyz):
    s = BRADFORD @ src_white_xyz
    d = BRADFORD @ dst_white_xyz
    return np.linalg.inv(BRADFORD) @ np.diag(d / s) @ BRADFORD


def colorants_d50(primaries, white):
    """RGB->XYZ(D50) matrix of a matrix/TRC profile, adapted with Bradford from the native white."""
    m = rgb_to_xyz(primaries, white)
    return bradford(xy_to_xyz(*white), np.array(D50)) @ m


def s15(v):
    return int(round(v * 65536))


def quantized(m):
    return np.vectorize(lambda v: s15(v) / 65536.0)(m)


def srgb_decode(v):
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)


def srgb_encode(v):
    v = np.clip(v, 0.0, 1.0)
    return np.where(v <= 0.0031308, v * 12.92, 1.055 * v ** (1 / 2.4) - 0.055)


def para_eval(kind, p, x):
    g = p[0]
    if kind == 0:
        return x ** g
    if kind == 1:
        a, b = p[1:3]
        return np.where(x >= -b / a, np.maximum(a * x + b, 0) ** g, 0.0)
    if kind == 2:
        a, b, c = p[1:4]
        return np.where(x >= -b / a, np.maximum(a * x + b, 0) ** g + c, c)
    if kind == 3:
        a, b, c, d = p[1:5]
        return np.where(x >= d, np.maximum(a * x + b, 0) ** g, c * x)
    a, b, c, d, e, f = p[1:7]
    return np.where(x >= d, np.maximum(a * x + b, 0) ** g + e, c * x + f)


# ------------------------------------------------------------------ ICC writer

def tag_xyz(v):
    return b"XYZ " + b"\0" * 4 + b"".join(struct.pack(">i", s15(c)) for c in v)


def tag_curv(entries=None, gamma=None):
    if gamma is not None:
        return b"curv" + b"\0" * 4 + struct.pack(">IH", 1, int(round(gamma * 256))) + b"\0\0"
    if entries is None:
        return b"curv" + b"\0" * 4 + struct.pack(">I", 0)
    data = b"curv" + b"\0" * 4 + struct.pack(">I", len(entries)) + b"".join(struct.pack(">H", e) for e in entries)
    return data + b"\0" * (-len(data) % 4)


def tag_para(kind, params):
    data = b"para" + b"\0" * 4 + struct.pack(">HH", kind, 0) + b"".join(struct.pack(">i", s15(p)) for p in params)
    return data


def quantize_params(params):
    return [s15(p) / 65536.0 for p in params]


def tag_text(version, text):
    if version >= 4:
        utf16 = text.encode("utf-16-be")
        data = b"mluc" + b"\0" * 4 + struct.pack(">II", 1, 12) + b"enUS" + struct.pack(">II", len(utf16), 28) + utf16
    else:
        ascii_ = text.encode("ascii") + b"\0"
        data = b"desc" + b"\0" * 4 + struct.pack(">I", len(ascii_)) + ascii_ + struct.pack(">II", 0, 0) + b"\0\0\0" + b"\0" * 67
    return data + b"\0" * (-len(data) % 4)


def tag_cprt(version, text):
    if version >= 4:
        return tag_text(4, text)
    data = b"text" + b"\0" * 4 + text.encode("ascii") + b"\0"
    return data + b"\0" * (-len(data) % 4)


def build_profile(version, space, tags, cls=b"mntr", pcs=b"XYZ "):
    tags = [(b"desc", tag_text(version, "PictureSharp test profile")), (b"cprt", tag_cprt(version, "No copyright, use freely"))] + tags
    table_size = 4 + 12 * len(tags)
    offset = 128 + table_size
    entries, blobs = [], []
    for sig, data in tags:
        offset += -offset % 4
        entries.append(struct.pack(">4sII", sig, offset, len(data)))
        blobs.append((offset, data))
        offset += len(data)
    size = offset
    header = bytearray(128)
    struct.pack_into(">I", header, 0, size)
    struct.pack_into(">I", header, 8, 0x04300000 if version >= 4 else 0x02100000)
    header[12:16] = cls
    header[16:20] = space
    header[20:24] = pcs
    struct.pack_into(">6H", header, 24, 2026, 10, 15, 12, 0, 0)
    header[36:40] = b"acsp"
    struct.pack_into(">3i", header, 68, *(s15(c) for c in D50))
    out = bytearray(header) + struct.pack(">I", len(tags)) + b"".join(entries)
    for off, data in blobs:
        out += b"\0" * (off - len(out)) + data
    return bytes(out)


# ------------------------------------------------------------------ profile catalog

SRGB_PRIMARIES = [(0.64, 0.33), (0.30, 0.60), (0.15, 0.06)]
P3_PRIMARIES = [(0.680, 0.320), (0.265, 0.690), (0.150, 0.060)]
ADOBE_PRIMARIES = [(0.64, 0.33), (0.21, 0.71), (0.15, 0.06)]
REC2020_PRIMARIES = [(0.708, 0.292), (0.170, 0.797), (0.131, 0.046)]
PROPHOTO_PRIMARIES = [(0.7347, 0.2653), (0.1596, 0.8404), (0.0366, 0.0001)]
D65 = (0.3127, 0.3290)
D50_XY = (0.3457, 0.3585)
SRGB_PARA = [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045]


def bt709_decode(v):
    return np.where(v < 0.081, v / 4.5, ((v + 0.099) / 1.099) ** (1 / 0.45))


def table(fn, n):
    x = np.linspace(0.0, 1.0, n)
    return [int(round(min(max(y, 0.0), 1.0) * 65535)) for y in fn(x)]


def rgb_profile(version, primaries, white, curve):
    m = colorants_d50(primaries, white)
    tags = [(b"wtpt", tag_xyz(D50))]
    if version >= 4:
        chad = bradford(xy_to_xyz(*white), np.array(D50))
        tags.append((b"chad", b"sf32" + b"\0" * 4 + b"".join(struct.pack(">i", s15(v)) for v in chad.flatten())))
    tags += [(b"rXYZ", tag_xyz(m[:, 0])), (b"gXYZ", tag_xyz(m[:, 1])), (b"bXYZ", tag_xyz(m[:, 2]))]
    tags += [(b"rTRC", curve), (b"gTRC", curve), (b"bTRC", curve)]
    return tags


PROFILES = {
    # name: (version, space, tags)
    "display_p3_v4_para3": (4, b"RGB ", rgb_profile(4, P3_PRIMARIES, D65, tag_para(3, SRGB_PARA))),
    "adobe_rgb_v2_gamma": (2, b"RGB ", rgb_profile(2, ADOBE_PRIMARIES, D65, tag_curv(gamma=563 / 256))),
    "prophoto_v2_gamma18": (2, b"RGB ", rgb_profile(2, PROPHOTO_PRIMARIES, D50_XY, tag_curv(gamma=1.8))),
    "rec2020_v4_table": (4, b"RGB ", rgb_profile(4, REC2020_PRIMARIES, D65, tag_curv(table(bt709_decode, 1024)))),
    "srgb_v2_table": (2, b"RGB ", rgb_profile(2, SRGB_PRIMARIES, D65, tag_curv(table(srgb_decode, 1024)))),
    "linear_srgb_v4_identity": (4, b"RGB ", rgb_profile(4, SRGB_PRIMARIES, D65, tag_curv())),
    "p3_v4_para0": (4, b"RGB ", rgb_profile(4, P3_PRIMARIES, D65, tag_para(0, [2.2]))),
    "adobe_v4_para1": (4, b"RGB ", rgb_profile(4, ADOBE_PRIMARIES, D65, tag_para(1, [2.2, 1.05, -0.05]))),
    "srgb_v4_para2": (4, b"RGB ", rgb_profile(4, SRGB_PRIMARIES, D65, tag_para(2, [2.0, 0.95, 0.05, 0.02]))),
    "rec2020_v4_para4": (4, b"RGB ", rgb_profile(4, REC2020_PRIMARIES, D65, tag_para(4, [2.4, 0.9, 0.08, 0.1, 0.05, 0.01, 0.02]))),
    "gray_v2_gamma22": (2, b"GRAY", [(b"wtpt", tag_xyz(D50)), (b"kTRC", tag_curv(gamma=2.2))]),
    "gray_v4_para3": (4, b"GRAY", [(b"wtpt", tag_xyz(D50)), (b"kTRC", tag_para(3, SRGB_PARA))]),
    "gray_v2_table": (2, b"GRAY", [(b"wtpt", tag_xyz(D50)), (b"kTRC", tag_curv(table(lambda x: x ** 1.6, 256)))]),
}


def curve_model(tag):
    """The exact transfer function the profile describes, from its quantized stored values."""
    sig = tag[:4]
    if sig == b"curv":
        count = struct.unpack(">I", tag[8:12])[0]
        if count == 0:
            return lambda x: x
        if count == 1:
            g = struct.unpack(">H", tag[12:14])[0] / 256.0
            return lambda x: x ** g
        values = np.array(struct.unpack(f">{count}H", tag[12:12 + 2 * count])) / 65535.0
        return lambda x: np.interp(x, np.linspace(0, 1, count), values)
    kind = struct.unpack(">H", tag[8:10])[0]
    n = [1, 3, 4, 5, 7][kind]
    params = [v / 65536.0 for v in struct.unpack(f">{n}i", tag[12:12 + 4 * n])]
    return lambda x: para_eval(kind, params, x)


def exact_convert(name, rgb):
    """Double-precision model of a relative colorimetric matrix/TRC conversion to sRGB, rounded to 8 bits."""
    version, space, tags = PROFILES[name]
    tagmap = dict(tags)
    srgb_m = colorants_d50(SRGB_PRIMARIES, D65)
    x = rgb.astype(np.float64) / 255.0
    if space == b"GRAY":
        y = curve_model(tagmap[b"kTRC"])(x)
        return np.rint(srgb_encode(y) * 255.0)
    lin = np.stack([curve_model(tagmap[t])(x[..., i]) for i, t in enumerate([b"rTRC", b"gTRC", b"bTRC"])], axis=-1)
    m = np.column_stack([np.array([struct.unpack(">i", tagmap[t][8 + 4 * k:12 + 4 * k])[0] / 65536.0 for k in range(3)])
                         for t in [b"rXYZ", b"gXYZ", b"bXYZ"]])
    xyz = lin @ m.T
    out = xyz @ np.linalg.inv(srgb_m).T
    return np.rint(srgb_encode(out) * 255.0)


# ------------------------------------------------------------------ images

def input_rgb():
    a = np.zeros((20, 256, 3), np.uint8)
    for y in range(16):
        for x in range(256):
            a[y, x] = ((x % 16) * 17, (x // 16) * 17, y * 17)
    ramp = np.arange(256, dtype=np.uint8)
    a[16] = np.stack([ramp] * 3, -1)
    a[17, :, 0] = ramp
    a[18, :, 1] = ramp
    a[19, :, 2] = ramp
    return a


def input_gray():
    return np.arange(256, dtype=np.uint8).reshape(1, 256)


def lcms_convert(profile_bytes, image, mode):
    src = ImageCms.ImageCmsProfile(io.BytesIO(profile_bytes))
    dst = ImageCms.createProfile("sRGB")
    t = ImageCms.buildTransform(src, dst, mode, "RGB", renderingIntent=ImageCms.Intent.RELATIVE_COLORIMETRIC, flags=0)
    return ImageCms.applyTransform(image, t)


# ------------------------------------------------------------------ PNG writer

def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def png(rgb, before=(), after=()):
    h, w, _ = rgb.shape
    raw = b"".join(b"\0" + rgb[y].tobytes() for y in range(h))
    out = b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    out += b"".join(png_chunk(k, d) for k, d in before)
    out += png_chunk(b"IDAT", zlib.compress(raw, 9))
    out += b"".join(png_chunk(k, d) for k, d in after)
    return out + png_chunk(b"IEND", b"")


def gama(g):
    return b"gAMA", struct.pack(">I", int(round(g * 100000)))


def chrm(white, primaries):
    values = [white[0], white[1]] + [c for p in primaries for c in p]
    return b"cHRM", struct.pack(">8I", *(int(round(v * 100000)) for v in values))


def iccp(profile):
    return b"iCCP", b"test\0\0" + zlib.compress(profile)


def png_color_profile(gamma, white, primaries):
    """The matrix/TRC profile equivalent to gAMA (file gamma, decoding exponent 1/gamma) and cHRM."""
    tags = rgb_profile(2, primaries, white, tag_para(0, [1 / gamma]))
    return build_profile(2, b"RGB ", tags)


# PNG color-chunk cases: name -> (chunks before IDAT, chunks after IDAT, effective description or None for sRGB)
P3_CHRM_VALUES = (D65, P3_PRIMARIES)
PNG_CASES = {
    "png_gama_only": ([gama(1 / 1.8)], [], (1 / 1.8, D65, SRGB_PRIMARIES)),
    "png_gama_chrm": ([gama(1 / 2.2), chrm(*P3_CHRM_VALUES)], [], (1 / 2.2, D65, P3_PRIMARIES)),
    "png_chrm_only": ([chrm(D65, ADOBE_PRIMARIES)], [], ("srgb-curve", D65, ADOBE_PRIMARIES)),
    "png_srgb_overrides_gama": ([(b"sRGB", b"\x00"), gama(1 / 1.8), chrm(*P3_CHRM_VALUES)], [], None),
    "png_iccp_overrides_all": ([gama(1 / 1.8), chrm(D65, ADOBE_PRIMARIES), iccp(b"PROFILE")], [], "display_p3_v4_para3"),
    "png_gama_after_idat_ignored": ([], [gama(1 / 1.8)], None),
    "png_gama_zero_ignored": ([(b"gAMA", struct.pack(">I", 0))], [], None),
}


def corrupt_profiles(good):
    p = bytearray(good)
    yield "truncated", bytes(p[:100])
    bad = bytearray(p)
    struct.pack_into(">I", bad, 0, 64)
    yield "size_too_small", bytes(bad)
    bad = bytearray(p)
    bad[36:40] = b"xxxx"
    yield "bad_signature", bytes(bad)
    bad = bytearray(p)
    struct.pack_into(">I", bad, 128, 100000)
    yield "tag_count_overflow", bytes(bad)
    bad = bytearray(p)
    struct.pack_into(">I", bad, 128 + 4 + 4, len(p) - 4)
    yield "tag_offset_outside", bytes(bad)


def unsupported_profiles():
    rgb = PROFILES["display_p3_v4_para3"]
    yield "cmyk", build_profile(4, b"CMYK", [(b"wtpt", tag_xyz(D50))], cls=b"prtr")
    yield "lab_pcs", build_profile(4, b"RGB ", rgb[2], pcs=b"Lab ")
    yield "device_link", build_profile(4, b"RGB ", rgb[2], cls=b"link")
    yield "lut_only", build_profile(4, b"RGB ", [(b"wtpt", tag_xyz(D50)), (b"A2B0", b"mAB " + b"\0" * 28)])
    yield "para_type5", build_profile(4, b"GRAY", [(b"wtpt", tag_xyz(D50)), (b"kTRC", b"para" + b"\0" * 4 + struct.pack(">HH", 5, 0) + b"\0" * 28)])
    yield "unknown_curve_type", build_profile(4, b"GRAY", [(b"wtpt", tag_xyz(D50)), (b"kTRC", b"sf32" + b"\0" * 12)])


# ------------------------------------------------------------------ linear light

FILTERS = {"lanczos": Image.Resampling.LANCZOS, "bicubic": Image.Resampling.BICUBIC, "bilinear": Image.Resampling.BILINEAR,
           "box": Image.Resampling.BOX, "hamming": Image.Resampling.HAMMING}

# name: (source in corpus/src, width, height, filter)
LINEAR_RESIZES = {
    "photo_alpha_down": ("photo_alpha.png", 67, 53, "lanczos"),
    "photo_alpha_up": ("photo_alpha.png", 311, 243, "bicubic"),
    "graphic_alpha_down": ("graphic_alpha.png", 40, 30, "bilinear"),
    "odd_alpha_box": ("odd_alpha.png", 30, 77, "box"),
    "photo_hamming": ("photo.png", 100, 75, "hamming"),
    "checker_half": ("checker", 8, 8, "box"),
}

# name: (destination, source, x, y, opacity)
LINEAR_COMPOSITES = {
    "graphic_over_photo": ("photo.png", "graphic_alpha.png", 30, 20, 1.0),
    "odd_over_photo_alpha": ("photo_alpha.png", "odd_alpha.png", -10, 70, 0.6),
}


def checker():
    """Black and white one-pixel checks: averaging in linear light gives 50% light, i.e. sRGB 188, not 128."""
    y, x = np.mgrid[0:16, 0:16]
    v = np.where((x + y) % 2 == 0, 255, 0).astype(np.uint8)
    return Image.fromarray(np.stack([v, v, v, np.full_like(v, 255)], -1), "RGBA")


def to_linear_premultiplied(im):
    a = np.asarray(im.convert("RGBA"), np.float64) / 255.0
    alpha = a[..., 3]
    return srgb_decode(a[..., :3]) * alpha[..., None], alpha


def from_linear_premultiplied(color, alpha):
    alpha8 = np.where(alpha >= 1, 255, np.where(alpha > 0, np.floor(alpha * 255 + 0.5), 0))
    safe = np.where(alpha > 0, alpha, 1.0)[..., None]
    rgb = np.floor(srgb_encode(color / safe) * 255 + 0.5)
    rgb = np.where(alpha8[..., None] == 0, 0, rgb)
    return Image.fromarray(np.concatenate([rgb, alpha8[..., None]], -1).astype(np.uint8), "RGBA")


def linear_resize(im, size, filter_):
    color, alpha = to_linear_premultiplied(im)
    resample = FILTERS[filter_]
    chans = [np.asarray(Image.fromarray(color[..., i].astype(np.float32), "F").resize(size, resample), np.float64) for i in range(3)]
    a = np.asarray(Image.fromarray(alpha.astype(np.float32), "F").resize(size, resample), np.float64)
    return from_linear_premultiplied(np.stack(chans, -1), a)


def linear_composite(dst, src, x, y, opacity):
    d_color, d_alpha = to_linear_premultiplied(dst)
    s_color, s_alpha = to_linear_premultiplied(src)
    out = np.asarray(dst.convert("RGBA")).copy()
    h, w = d_alpha.shape
    sh, sw = s_alpha.shape
    for yy in range(max(0, y), min(h, y + sh)):
        for xx in range(max(0, x), min(w, x + sw)):
            sa = s_alpha[yy - y, xx - x]
            if sa == 0:
                continue
            sc = s_color[yy - y, xx - x] * opacity
            sa *= opacity
            c = sc + d_color[yy, xx] * (1 - sa)
            a = sa + d_alpha[yy, xx] * (1 - sa)
            px = np.asarray(from_linear_premultiplied(c.reshape(1, 1, 3), np.array([[a]])))[0, 0]
            out[yy, xx] = px
    return Image.fromarray(out, "RGBA")


def load_src(name):
    if name == "checker":
        return checker()
    return Image.open(os.path.join(os.path.dirname(OUT), "src", name))


# ------------------------------------------------------------------ main

def calibration():
    """LittleCMS against the exact model: name -> (max abs difference, fraction of differing channels)."""
    rgb = input_rgb()
    gray = input_gray()
    result = {}
    for name, (version, space, tags) in PROFILES.items():
        data = build_profile(version, space, tags)
        if space == b"GRAY":
            got = np.asarray(lcms_convert(data, Image.fromarray(gray, "L"), "L"), np.int32)
            want = np.stack([exact_convert(name, gray)] * 3, -1)
        else:
            got = np.asarray(lcms_convert(data, Image.fromarray(rgb, "RGB"), "RGB"), np.int32)
            want = exact_convert(name, rgb)
        diff = np.abs(got - want)
        result[name] = {"max_abs": int(diff.max()), "mismatch_fraction": round(np.count_nonzero(diff) / diff.size, 4)}
    return result


def calibrate():
    print(f"LittleCMS {ImageCms.core.littlecms_version}")
    for name, r in calibration().items():
        print(f"{name:28s} max {r['max_abs']}  mismatched {r['mismatch_fraction']:.4f}")


def main():
    if "--calibrate" in sys.argv:
        calibrate()
        return
    os.makedirs(OUT, exist_ok=True)
    rgb = input_rgb()
    gray = input_gray()
    Image.fromarray(rgb, "RGB").save(os.path.join(OUT, "input_rgb.png"))
    Image.fromarray(gray, "L").save(os.path.join(OUT, "input_gray.png"))
    cases = {}
    for name, (version, space, tags) in PROFILES.items():
        data = build_profile(version, space, tags)
        with open(os.path.join(OUT, f"{name}.icc"), "wb") as f:
            f.write(data)
        image, mode = (Image.fromarray(gray, "L"), "L") if space == b"GRAY" else (Image.fromarray(rgb, "RGB"), "RGB")
        lcms_convert(data, image, mode).save(os.path.join(OUT, f"{name}.ref.png"))
        cases[name] = {"profile": f"{name}.icc", "input": "input_gray.png" if space == b"GRAY" else "input_rgb.png",
                       "reference": f"{name}.ref.png", "version": version}

    p3 = build_profile(*PROFILES["display_p3_v4_para3"])
    png_cases = {}
    for name, (before, after, effective) in PNG_CASES.items():
        before = [iccp(p3) if k == b"iCCP" else (k, d) for k, d in before]
        with open(os.path.join(OUT, f"{name}.png"), "wb") as f:
            f.write(png(rgb, before, after))
        if effective is None:
            reference = None
        elif isinstance(effective, str):
            reference = f"{effective}.ref.png"
        else:
            gamma_value, white, primaries = effective
            if gamma_value == "srgb-curve":
                tags = rgb_profile(2, primaries, white, tag_para(3, SRGB_PARA))
                profile = build_profile(2, b"RGB ", tags)
            else:
                profile = png_color_profile(gamma_value, white, primaries)
            reference = f"{name}.ref.png"
            lcms_convert(profile, Image.fromarray(rgb, "RGB"), "RGB").save(os.path.join(OUT, reference))
        png_cases[name] = {"file": f"{name}.png", "reference": reference}

    invalid = {}
    for name, data in corrupt_profiles(p3):
        invalid[name] = {"file": f"corrupt_{name}.icc", "expected": "ImageFormatException"}
        with open(os.path.join(OUT, f"corrupt_{name}.icc"), "wb") as f:
            f.write(data)
    for name, data in unsupported_profiles():
        invalid[name] = {"file": f"unsupported_{name}.icc", "expected": "NotSupportedException"}
        with open(os.path.join(OUT, f"unsupported_{name}.icc"), "wb") as f:
            f.write(data)

    linear = {}
    for name, (source, w, h, filter_) in LINEAR_RESIZES.items():
        if source == "checker":
            checker().save(os.path.join(OUT, "checker.png"))
        linear_resize(load_src(source), (w, h), filter_).save(os.path.join(OUT, f"linear_{name}.png"))
        linear[name] = {"op": "resize", "input": source, "width": w, "height": h, "filter": filter_, "reference": f"linear_{name}.png"}
    for name, (dst, src, x, y, opacity) in LINEAR_COMPOSITES.items():
        linear_composite(load_src(dst), load_src(src), x, y, opacity).save(os.path.join(OUT, f"linear_{name}.png"))
        linear[name] = {"op": "composite", "input": dst, "overlay": src, "x": x, "y": y, "opacity": opacity, "reference": f"linear_{name}.png"}

    manifest = {
        "oracle": {
            "engine": f"LittleCMS {ImageCms.core.littlecms_version} via Pillow {Image.__version__} ImageCms",
            "destination": "ImageCms.createProfile('sRGB') (LittleCMS built-in sRGB, D65 adapted to D50 with Bradford)",
            "intent": "relative colorimetric",
            "black_point_compensation": False,
            "flags": 0,
            "modes": "8-bit RGB -> RGB, 8-bit L -> RGB",
        },
        "tolerance": TOLERANCE,
        "calibration_against_exact_model": calibration(),
        "profiles": cases,
        "png": png_cases,
        "invalid": invalid,
        "linear_light": {
            "oracle": "Pillow 'F' mode resampling (32-bit float) of premultiplied linear-light channels; "
                      "source-over in double precision",
            "max_abs": 1,
            "cases": linear,
        },
    }
    with open(os.path.join(OUT, "color_reference.json"), "w") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
