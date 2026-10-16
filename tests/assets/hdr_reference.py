#!/usr/bin/env python3
"""Generates the HDR gain map corpus in tests/assets/corpus/hdr/ (0.6.5).

Writes synthetic JPEG files with an SDR base image, a gain map JPEG appended after the primary image and an MPF
directory pointing to it, with ISO 21496-1 metadata (APP2), Apple HDR Gain Map metadata (MakerNote tags 33/48 and the
map's XMP) or both, plus damaged and unsupported variants. For every valid file it writes the expected linear HDR
pixels (RGB float32, little-endian, row-major) of an independent double-precision model, and the expected tone-mapped
RGBA bytes, described by hdr_reference.json.

The model decodes the base and the map with Pillow (libjpeg-turbo, the same pixels as PictureSharp's decoder) and then
follows the formulas fixed in plan_hdr.md: ISO reconstruction as libultrahdr v2.0.2 (Shepard's inverse-distance
sampling, gamma, log2 gain bounds, offsets, headroom weight), the Apple formula (bilinear sampling at pixel centers,
Rec. 709 linearization, 1 + (headroom - 1) * gain) and the extended Reinhard tone mapping.

Since 0.6.9 it also writes tests/assets/corpus/hdr_edit/ and hdr_edit_reference.json: the expected gain map after
geometric edits (flip, 90° rotations, EXIF orientations, crop, resize, thumbnail and chains), under the conventions in
plan.md, with the reconstruction of each edited image and a fidelity measure against the original map (see edits()).

  python3 -I tests/assets/hdr_reference.py
"""
import io
import json
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageOps

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True
sys.path.insert(0, HERE)
import color_reference as cr  # noqa: E402  (ICC writer and color math shared with the 0.6.2 corpus)

OUT = os.path.join(HERE, "corpus", "hdr")

# Fixed before the implementation (plan_hdr.md section 5): reconstruction 1e-5 + 1e-5 * |reference|, tone mapping 1 level.
TOLERANCE = {"absolute": 1e-5, "relative": 1e-5, "tone_map_levels": 1}

# libultrahdr v2.0.2 comparisons (HdrUltraHdrOracleTests). "math": its exact functions (srgbInvOetf, sampleMap,
# applyGain) on PictureSharp's decoded pixels, held to TOLERANCE. "pipeline": ultrahdr_app's full decode (its own
# YUV->RGB conversion, 10-bit sRGB and gain LUTs, half-float output clamped to [0, 10000/203]); budget set from the
# first measurement on this corpus (max 0.0162, median 0.42 %, p99 2.9 %) with margin. Maps whose aspect ratio
# differs by more than 1% from the base are resized by libultrahdr and are excluded (iso_odd_scale).
ORACLE = {"pipeline_max_absolute": 0.025, "pipeline_median_relative": 0.006, "pipeline_p99_relative": 0.04,
          "lut_max_absolute": 0.006, "excluded": ["iso_odd_scale", "iso_alt_space"]}

ISO_URN = b"urn:iso:std:iso:ts:21496:-1\0"


# ------------------------------------------------------------------ JPEG assembly

def segment(marker, body):
    return bytes([0xFF, marker]) + struct.pack(">H", len(body) + 2) + body


def jpeg_bytes(image, quality=92, subsampling=0):
    buf = io.BytesIO()
    image.save(buf, "JPEG", quality=quality, subsampling=subsampling)
    return buf.getvalue()


def insert_after_soi(jpeg, segments):
    assert jpeg[:2] == b"\xff\xd8"
    return jpeg[:2] + b"".join(segments) + jpeg[2:]


def icc_segments(profile):
    return [segment(0xE2, b"ICC_PROFILE\0" + bytes([1, 1]) + profile)]


def xmp_segment(xml):
    return segment(0xE1, b"http://ns.adobe.com/xap/1.0/\0" + xml.encode("utf-8"))


def apple_xmp(version=131072, headroom=None):
    extra = f"<HDRGainMap:HDRGainMapHeadroom>{headroom}</HDRGainMap:HDRGainMapHeadroom>" if headroom is not None else ""
    return ('<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            '<rdf:Description rdf:about="" xmlns:HDRGainMap="http://ns.apple.com/HDRGainMap/1.0/" '
            'xmlns:apdi="http://ns.apple.com/pixeldatainfo/1.0/">'
            f"<HDRGainMap:HDRGainMapVersion>{version}</HDRGainMap:HDRGainMapVersion>{extra}"
            "<apdi:AuxiliaryImageType>urn:com:apple:photo:2020:aux:hdrgainmap</apdi:AuxiliaryImageType>"
            "</rdf:Description></rdf:RDF></x:xmpmeta>")


def ultrahdr_xmp():
    return ('<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            '<rdf:Description rdf:about="" xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/" hdrgm:Version="1.0" '
            'hdrgm:GainMapMax="2.0" hdrgm:HDRCapacityMax="2.0"/></rdf:RDF></x:xmpmeta>')


def rational(value, denominator=1_000_000, signed=True):
    n = int(round(value * denominator))
    return struct.pack(">i" if signed else ">I", n) + struct.pack(">I", denominator)


def iso_payload(p, version=0, flags_extra=0, common=False):
    """ISO 21496-1 metadata as libultrahdr writes it; p holds per-channel lists and log2 headrooms."""
    channels = len(p["min"])
    flags = (0x80 if channels == 3 else 0) | (0x40 if p.get("use_base", True) else 0) | (0x08 if common else 0) | flags_extra
    out = struct.pack(">HHB", version, 0, flags)
    if common:
        d = 1_000_000
        out += struct.pack(">III", d, int(round(p["base"] * d)), int(round(p["alt"] * d)))
        for c in range(channels):
            out += struct.pack(">iiIii", *(int(round(p[k][c] * d)) for k in ("min", "max", "gamma", "off_sdr", "off_hdr")))
        return out
    out += rational(p["base"], signed=False) + rational(p["alt"], signed=False)
    for c in range(channels):
        out += rational(p["min"][c]) + rational(p["max"][c]) + rational(p["gamma"][c], signed=False)
        out += rational(p["off_sdr"][c]) + rational(p["off_hdr"][c])
    return out


def quantized_iso(p, common=False):
    """The parameter values the file stores (rationals with denominator 10^6)."""
    q = lambda v: int(round(v * 1_000_000)) / 1_000_000  # noqa: E731
    return {k: ([q(x) for x in v] if isinstance(v, list) else (q(v) if isinstance(v, float) else v)) for k, v in p.items()}


def exif_block(orientation=None, maker=None):
    """Big-endian TIFF with IFD0 (orientation, EXIF pointer) and, for Apple, a MakerNote with tags 33 and 48."""
    entries0 = []
    if orientation is not None:
        entries0.append((0x0112, 3, 1, struct.pack(">HH", orientation, 0)))
    note = b""
    if maker is not None:
        values = b"".join(struct.pack(">ii", int(round(v * 1_000_000)), 1_000_000) for v in maker)
        ifd_size = 2 + 12 * 2 + 4
        value_offset = 14 + ifd_size
        note = b"Apple iOS\0" + struct.pack(">H", 1) + b"MM" + struct.pack(">H", 2)
        note += struct.pack(">HHII", 33, 10, 1, value_offset) + struct.pack(">HHII", 48, 10, 1, value_offset + 8)
        note += struct.pack(">I", 0) + values
        entries0.append((0x8769, 4, 1, None))
    ifd0_offset = 8
    ifd0_size = 2 + 12 * len(entries0) + 4
    exif_offset = ifd0_offset + ifd0_size
    exif_ifd_size = 2 + 12 + 4
    note_offset = exif_offset + exif_ifd_size
    out = b"MM\0*" + struct.pack(">I", ifd0_offset) + struct.pack(">H", len(entries0))
    for tag, typ, count, value in entries0:
        out += struct.pack(">HHI", tag, typ, count) + (value if value is not None else struct.pack(">I", exif_offset))
    out += struct.pack(">I", 0)
    if maker is not None:
        out += struct.pack(">H", 1) + struct.pack(">HHII", 0x927C, 7, len(note), note_offset) + struct.pack(">I", 0) + note
    return segment(0xE1, b"Exif\0\0" + out)


def mpf_segment(primary_size, aux_offset, aux_size):
    """MPF APP2 with two MP entries; offsets are relative to the TIFF header that follows "MPF\\0"."""
    entries_offset = 8 + 2 + 3 * 12 + 4
    body = b"MM\0*" + struct.pack(">I", 8) + struct.pack(">H", 3)
    body += struct.pack(">HHI", 0xB000, 7, 4) + b"0100"
    body += struct.pack(">HHII", 0xB001, 4, 1, 2)
    body += struct.pack(">HHII", 0xB002, 7, 32, entries_offset)
    body += struct.pack(">I", 0)
    body += struct.pack(">IIIHH", 0x030000, primary_size, 0, 0, 0)
    body += struct.pack(">IIIHH", 0x000000, aux_size, aux_offset, 0, 0)
    return segment(0xE2, b"MPF\0" + body)


def assemble(base_jpeg, map_jpeg, primary_segments, mpf=True):
    """Primary image (with an MPF directory placed first) followed by the map; MPF offsets fixed in a second pass."""
    if not mpf:
        return insert_after_soi(base_jpeg, primary_segments) + map_jpeg
    placeholder = mpf_segment(0, 0, 0)
    primary = insert_after_soi(base_jpeg, [placeholder] + primary_segments)
    tiff_start = 2 + 4 + 4  # SOI, APP2 marker and length, "MPF\0"
    real = mpf_segment(len(primary), len(primary) - tiff_start, len(map_jpeg))
    primary = insert_after_soi(base_jpeg, [real] + primary_segments)
    return primary + map_jpeg


# ------------------------------------------------------------------ content

def base_image(width, height, seed):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width]
    r = 255 * x / max(width - 1, 1)
    g = 255 * y / max(height - 1, 1)
    b = 128 + 100 * np.sin((x + 2 * y) / 3.0)
    img = np.stack([r, g, b], axis=-1)
    img[height // 3: height // 2, width // 4: width // 2] = [250, 30, 20]       # saturated patch (out of sRGB from P3)
    img[0:3, :] = 0                                                              # blacks
    img[-3:, :] = 255                                                            # whites
    img += rng.normal(0, 6, img.shape)
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), "RGB")


def map_image(width, height, channels, seed, smooth=False):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width]
    if smooth:  # a low-frequency field like real gain maps (edits measure fidelity on these)
        v = 128 + 90 * np.sin(2.4 * x / width + 0.5) * np.cos(1.9 * y / height - 0.3)
        planes = [v + d for d in ((0, -25, 25) if channels == 3 else (0,))]
        planes = [np.clip(np.round(p_), 0, 255).astype(np.uint8) for p_ in planes]
        return Image.fromarray(np.stack(planes, axis=-1), "RGB") if channels == 3 else Image.fromarray(planes[0], "L")
    v = 255 * (x + y) / max(width + height - 2, 1)
    v[height // 2:, width // 2:] = 255 - v[height // 2:, width // 2:]            # sharp edge
    v[:2, :2] = 0
    planes = [np.clip(v + rng.normal(0, 4, v.shape), 0, 255)]
    if channels == 3:
        planes = [np.clip(v + rng.normal(0, 4, v.shape) + d, 0, 255) for d in (0, -40, 40)]
        return Image.fromarray(np.stack(planes, axis=-1).astype(np.uint8), "RGB")
    return Image.fromarray(planes[0].astype(np.uint8), "L")


P3_ICC = cr.build_profile(4, b"RGB ", cr.rgb_profile(4, cr.P3_PRIMARIES, cr.D65, cr.tag_para(3, cr.SRGB_PARA)))
SRGB_ICC = cr.build_profile(4, b"RGB ", cr.rgb_profile(4, cr.SRGB_PRIMARIES, cr.D65, cr.tag_para(3, cr.SRGB_PARA)))
ADOBE_ICC = cr.build_profile(2, b"RGB ", cr.rgb_profile(2, cr.ADOBE_PRIMARIES, cr.D65, cr.tag_curv(gamma=563 / 256)))
SRGB_PARA_Q = cr.quantize_params(cr.SRGB_PARA)


# ------------------------------------------------------------------ model

def m_rgb_to_xyz(primaries):
    return cr.rgb_to_xyz(primaries, cr.D65)


M_SRGB = m_rgb_to_xyz(cr.SRGB_PRIMARIES)
M_P3 = m_rgb_to_xyz(cr.P3_PRIMARIES)
SPACES = {"srgb": M_SRGB, "p3": M_P3}


def convert(rgb, source, target):
    if source == target:
        return rgb
    m = np.linalg.inv(SPACES[target]) @ SPACES[source]
    return rgb @ m.T


def linearize(codes, profile):
    v = codes / 255.0
    if profile == "icc":  # para type 3 with the quantized stored parameters
        return cr.para_eval(3, SRGB_PARA_Q, v)
    return cr.srgb_decode(v)


def sample_idw(m, xm, ym):
    h, w = m.shape
    xl = np.minimum(np.floor(xm).astype(int), w - 1)
    yl = np.minimum(np.floor(ym).astype(int), h - 1)
    xu, yu = np.minimum(xl + 1, w - 1), np.minimum(yl + 1, h - 1)
    e = [m[yl, xl], m[yu, xl], m[yl, xu], m[yu, xu]]
    # Distances to the clamped neighbor positions, as libultrahdr's sampleMap.
    d = [np.hypot(xm - xl, ym - yl), np.hypot(xm - xl, ym - yu), np.hypot(xm - xu, ym - yl), np.hypot(xm - xu, ym - yu)]
    out = np.zeros_like(xm, dtype=np.float64)
    done = np.zeros_like(xm, dtype=bool)
    for ek, dk in zip(e, d):
        hit = (dk == 0) & ~done
        out[hit] = ek[hit]
        done |= hit
    with np.errstate(divide="ignore"):
        wts = [np.where(done, 0, 1 / np.where(dk == 0, 1, dk)) for dk in d]
    total = sum(wts)
    blend = sum(ek * wk for ek, wk in zip(e, wts)) / np.where(done, 1, total)
    return np.where(done, out, blend)


def sample_bilinear(m, x, y):
    h, w = m.shape
    x = np.clip(x, 0, w - 1)
    y = np.clip(y, 0, h - 1)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    x1, y1 = np.minimum(x0 + 1, w - 1), np.minimum(y0 + 1, h - 1)
    fx, fy = x - x0, y - y0
    top = m[y0, x0] + (m[y0, x1] - m[y0, x0]) * fx
    bottom = m[y1, x0] + (m[y1, x1] - m[y1, x0]) * fx
    return top + (bottom - top) * fy


def reconstruct(case, base_rgb, map_px, target_headroom=None, output_space=None, coords=None):
    """HDR in the stored geometry, in output_space (default: base space). coords, when given, holds the positions
    (x, y) in the stored base image, of size case["size"], at which each base pixel samples the map."""
    h, w, _ = base_rgb.shape
    space = case["base_space"]
    lin = linearize(base_rgb.astype(np.float64), "icc" if case.get("icc") else "srgb")
    mh, mw = map_px.shape[:2]
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    if coords is not None:
        x, y = coords
        w, h = case["size"]
    if case["model"] == "iso":
        p = case["iso_q"]
        mult = space if p.get("use_base", True) else case["alt_space"]
        lin = convert(lin, space, mult)
        weight = 1.0
        if target_headroom is not None:
            weight = min(max((np.log2(target_headroom) - p["base"]) / (p["alt"] - p["base"]), 0.0), 1.0)
        headroom = 2.0 ** (p["base"] + weight * (p["alt"] - p["base"]))
        scale_x = w / mw
        same_aspect = abs(w / h - mw / mh) / (w / h) <= 0.01
        xm, ym = x / scale_x, y / (scale_x if same_aspect else h / mh)
        out = np.empty_like(lin)
        for c in range(3):
            ch = c if len(p["min"]) == 3 else 0
            gain = sample_idw(map_px[..., ch] / 255.0, xm, ym)
            gamma = p["gamma"][ch]
            if gamma != 1.0:
                gain = gain ** (1.0 / gamma)
            log_boost = p["min"][ch] * (1 - gain) + p["max"][ch] * gain
            factor = 2.0 ** (log_boost * weight)
            out[..., c] = (lin[..., c] + p["off_sdr"][ch]) * factor - p["off_hdr"][ch]
        result_space = mult
    else:
        full = cr_apple_headroom(case)
        headroom = full if target_headroom is None else min(target_headroom, full)
        scale_x = w / mw
        scale_y = scale_x if abs(w / h - mw / mh) / (w / h) <= 0.01 else h / mh
        xm = (x + 0.5) / scale_x - 0.5
        ym = (y + 0.5) / scale_y - 0.5
        gain = cr.bt709_decode(sample_bilinear(map_px[..., 0] / 255.0, xm, ym))
        out = lin * (1 + (headroom - 1) * gain)[..., None]
        result_space = space
    out = convert(out, result_space, output_space or space)
    return out, headroom


def cr_apple_headroom(case):
    if case.get("maker") is None:
        return case["xmp_headroom"]
    m33, m48 = (int(round(v * 1_000_000)) / 1_000_000 for v in case["maker"])
    if m33 < 1.0:
        stops = -20.0 * m48 + 1.8 if m48 <= 0.01 else -0.101 * m48 + 1.601
    else:
        stops = -70.0 * m48 + 3.0 if m48 <= 0.01 else -0.303 * m48 + 2.303
    return 2.0 ** max(stops, 0.0)


def orient(img, orientation):
    """Applies an EXIF orientation to an H x W x C array (display = transform(stored))."""
    ops = {1: lambda a: a, 2: lambda a: a[:, ::-1], 3: lambda a: a[::-1, ::-1], 4: lambda a: a[::-1],
           5: lambda a: a.transpose(1, 0, 2), 6: lambda a: np.rot90(a, -1),
           7: lambda a: a[::-1, ::-1].transpose(1, 0, 2),
           8: lambda a: np.rot90(a, 1)}
    return ops[orientation](img)


def tone_map(hdr, space, headroom, exposure=0.0, white=None):
    rgb = convert(hdr, space, "srgb") * (2.0 ** exposure)
    w = white if white is not None else headroom
    lum = rgb @ np.array([0.2126, 0.7152, 0.0722])
    scale = np.where(lum > 0, (1 + lum / (w * w)) / (1 + np.where(lum > 0, lum, 0)), 0.0)
    rgb = np.clip(rgb * scale[..., None], 0, 1)
    enc = cr.srgb_encode(rgb)
    out = np.empty(hdr.shape[:2] + (4,), dtype=np.uint8)
    out[..., :3] = np.floor(enc * 255 + 0.5).astype(np.uint8)
    out[..., 3] = 255
    return out


# ------------------------------------------------------------------ cases

ISO_MONO = {"base": 0.0, "alt": 2.0, "min": [-0.5], "max": [2.0], "gamma": [1.0], "off_sdr": [1 / 64], "off_hdr": [1 / 64]}
ISO_GAMMA = {"base": 0.0, "alt": 1.75, "min": [-0.164], "max": [1.75], "gamma": [0.82], "off_sdr": [1e-5], "off_hdr": [1e-5]}
ISO_RGB = {"base": 0.0, "alt": 2.5, "min": [-0.25, 0.0, -1.0], "max": [2.5, 2.0, 1.5], "gamma": [1.0, 0.9, 1.2],
           "off_sdr": [0.01, 0.02, 0.0], "off_hdr": [0.015, 0.0, 0.005]}

CASES = [
    # name, base size, map size, map channels, model, extras
    dict(name="iso_mono_srgb", size=(48, 32), map=(24, 16), channels=1, model="iso", iso=ISO_MONO),
    dict(name="iso_gamma_p3", size=(48, 32), map=(24, 16), channels=1, model="iso", iso=ISO_GAMMA, icc=P3_ICC),
    dict(name="iso_rgb_p3", size=(40, 30), map=(20, 15), channels=3, model="iso", iso=ISO_RGB, icc=P3_ICC),
    dict(name="iso_alt_space", size=(48, 32), map=(24, 16), channels=1, model="iso", iso=dict(ISO_MONO, use_base=False),
         icc=P3_ICC, map_icc=SRGB_ICC, alt_space="srgb"),
    dict(name="iso_common_denominator", size=(48, 32), map=(12, 8), channels=1, model="iso", iso=ISO_GAMMA, common=True),
    dict(name="iso_odd_scale", size=(45, 31), map=(17, 13), channels=1, model="iso", iso=ISO_MONO, icc=P3_ICC),
    dict(name="iso_fractional_scale", size=(100, 67), map=(40, 27), channels=1, model="iso", iso=ISO_GAMMA, icc=P3_ICC),
    dict(name="iso_orientation6", size=(48, 32), map=(24, 16), channels=1, model="iso", iso=ISO_GAMMA, icc=P3_ICC, orientation=6),
    dict(name="iso_orientation7", size=(46, 30), map=(23, 15), channels=1, model="iso", iso=ISO_MONO, orientation=7),
    dict(name="apple_makernote", size=(48, 32), map=(24, 16), channels=1, model="apple", maker=(1.01, 1.0547851), icc=P3_ICC),
    dict(name="apple_xmp_headroom", size=(40, 24), map=(20, 12), channels=1, model="apple", xmp_headroom=3.5),
    dict(name="iso_and_apple", size=(48, 32), map=(24, 16), channels=1, model="iso", iso=ISO_GAMMA, icc=P3_ICC,
         maker=(1.01, 0.009737), apple_too=True),
]


def build(case, iso_version=0, iso_flags=0, mpf=True, primary_iso=True, map_xmp=None, break_iso=None):
    w, h = case["size"]
    seed = sum(map(ord, case["name"]))
    base = jpeg_bytes(base_image(w, h, seed))
    mw, mh = case["map"]
    map_jpeg = jpeg_bytes(map_image(mw, mh, case["channels"], seed + 1, case.get("smooth", False)), quality=90)
    map_segments = []
    if case["model"] == "iso" or case.get("apple_too"):
        if case["model"] == "iso":
            payload = iso_payload(case["iso"], version=iso_version, flags_extra=iso_flags, common=case.get("common", False))
            if break_iso is not None:
                payload = break_iso(payload)
            map_segments.append(segment(0xE2, ISO_URN + payload))
    if case["model"] == "apple" or case.get("apple_too"):
        map_segments.append(xmp_segment(apple_xmp(headroom=case.get("xmp_headroom"))))
    if map_xmp is not None:
        map_segments = [xmp_segment(map_xmp)]
    if case.get("map_icc"):
        map_segments += icc_segments(case["map_icc"])
    map_jpeg = insert_after_soi(map_jpeg, map_segments)
    primary = []
    if case.get("orientation") or case.get("maker"):
        primary.append(exif_block(case.get("orientation"), case.get("maker")))
    if case["model"] == "iso" and primary_iso:
        primary.append(segment(0xE2, ISO_URN + struct.pack(">HH", iso_version, 0)))
    if case.get("icc"):
        primary += icc_segments(case["icc"])
    return assemble(base, map_jpeg, primary, mpf=mpf)


def decode_parts(data):
    base = np.asarray(Image.open(io.BytesIO(data)).convert("RGB"))
    start = data.rfind(b"\xff\xd8\xff")
    m = Image.open(io.BytesIO(data[start:]))
    map_px = np.asarray(m.convert("RGB")) if m.mode != "L" else np.asarray(m)[..., None]
    return base, map_px


def write_f32(path, rgb):
    np.ascontiguousarray(rgb, dtype="<f4").tofile(path)


# ------------------------------------------------------------------ geometric edits (0.6.9)
#
# Conventions fixed in plan.md (0.6.9, "Konwencje geometrii mapy"): the map covers the whole base canvas; map pixel i
# spans base pixels [i * W / mw, (i + 1) * W / mw). Flips, 90° rotations and EXIF orientations are the same array
# operations on the map. Every other edit resamples the map's 8-bit codes with Pillow's resampler (PictureSharp's
# Resizer is its port) from the edit's source box converted to map coordinates; when that box is integral and the
# output has its size, the result is a plain slice. Map sizes after an edit: max(1, floor((2 W' mw + W) / (2 W))).

EDIT_OUT = os.path.join(HERE, "corpus", "hdr_edit")

FILTERS = {"Nearest": Image.Resampling.NEAREST, "Bilinear": Image.Resampling.BILINEAR,
           "Bicubic": Image.Resampling.BICUBIC, "Lanczos3": Image.Resampling.LANCZOS}

# Edited map codes against this model: 0 for array operations and slices, at most 1 per channel when resampled.
# "reconstruction": ToHdr of the edited image against its *.edited.f32 (the model on the same base and map).
# "fidelity": ToHdr of the edited image against *.ideal.f32, the same edited base with the gain sampled from the
# original map at each pixel's exact source position (errors as HdrUltraHdrOracleTests: relative =
# |d| / max(|expected|, 0.01)). It measures only what the map edit loses: array flips of an ISO map shift the gain by
# (1 - 1/scale) map pixels under libultrahdr's corner-aligned sampling (Apple's centered sampling stays exact), and
# resampling smooths it. Checked on the smooth_* sources, whose low-frequency maps resemble real ones; the budget is
# the first measurement (max 0.539, median 0.066, p99 0.170, all on smooth_iso_quarter_rgb: a 16 px wide map) with
# 50% margin. The synthetic noisy maps are checked by the map and reconstruction tolerances only.
EDIT_TOLERANCE = {"map_exact": 0, "map_resampled": 1, "reconstruction": TOLERANCE,
                  "fidelity": {"max_absolute": 0.8, "median_relative": 0.1, "p99_relative": 0.26}}


def map_size(new_base, base, size):
    return max(1, (2 * new_base * size + base) // (2 * base))


def resample(a, size, filt, box):
    """Pillow resize of each channel of an H x W x C array (uint8 or float) from a source box (x0, y0, x1, y1)."""
    planes = []
    for c in range(a.shape[2]):
        mode = "L" if a.dtype == np.uint8 else "F"
        im = Image.fromarray(np.ascontiguousarray(a[..., c] if mode == "L" else a[..., c].astype(np.float32)), mode)
        planes.append(np.asarray(im.resize(size, FILTERS[filt], box=tuple(float(v) for v in box))))
    out = np.stack(planes, axis=-1)
    return out if a.dtype == np.uint8 else out.astype(np.float64)


def map_region(m, base_w, base_h, box, size, filt):
    """The map of a base region: box in base pixels (float32 values, as Resizer.FillBox returns), size in map pixels."""
    mh, mw = m.shape[:2]
    scale = (mw / base_w, mh / base_h)
    mbox = [float(np.float64(np.float32(v)) * scale[i % 2]) for i, v in enumerate(box)]
    integral = all(float(v).is_integer() for v in mbox)
    if integral and size == (int(mbox[2] - mbox[0]), int(mbox[3] - mbox[1])):
        x0, y0, x1, y1 = (int(v) for v in mbox)
        return m[y0:y1, x0:x1], True
    return resample(m, size, filt, mbox), False


def fit_box(w, h, width, height):
    """Resizer.FillBox (Pillow ImageOps.fit, bleed 0, centered) as float32."""
    live, out = w / h, width / height
    if live == out:
        cw, ch = w, h
    elif live >= out:
        cw, ch = out * h, h
    else:
        cw, ch = w, w / out
    left, top = (w - cw) * 0.5, (h - ch) * 0.5
    return tuple(float(np.float32(v)) for v in (left, top, left + cw, top + ch))


def affine(m):
    return np.array([[m[0], m[1], m[2]], [m[3], m[4], m[5]], [0.0, 0.0, 1.0]])


def apply_edit(state, op):
    """One edit on {base, map, to_stored}; to_stored maps a current pixel position (x, y, 1) to its position in the
    stored base image. Returns whether the map stayed bit-exact (no resampling)."""
    base, m = state["base"], state["map"]
    h, w = base.shape[:2]
    kind = op["op"]
    if kind == "flip":
        horizontal = op["mode"] == "Horizontal"
        f = (lambda a: a[:, ::-1]) if horizontal else (lambda a: a[::-1])
        step = affine([-1, 0, w - 1, 0, 1, 0] if horizontal else [1, 0, 0, 0, -1, h - 1])
        state.update(base=f(base), map=f(m), to_stored=state["to_stored"] @ step)
        return True
    if kind == "rotate":
        degrees = op["degrees"]
        k = {90: -1, 180: 2, 270: 1}[degrees]  # clockwise
        step = affine({90: [0, 1, 0, -1, 0, h - 1], 180: [-1, 0, w - 1, 0, -1, h - 1],
                       270: [0, -1, w - 1, 1, 0, 0]}[degrees])
        state.update(base=np.rot90(base, k), map=np.rot90(m, k), to_stored=state["to_stored"] @ step)
        return True
    if kind == "crop":
        x, y, cw, ch = op["x"], op["y"], op["width"], op["height"]
        size = (map_size(cw, w, m.shape[1]), map_size(ch, h, m.shape[0]))
        new_map, exact = map_region(m, w, h, (x, y, x + cw, y + ch), size, "Bilinear")
        state.update(base=base[y:y + ch, x:x + cw], map=new_map, to_stored=state["to_stored"] @ affine([1, 0, x, 0, 1, y]))
        return exact
    if kind in ("resize", "thumbnail"):
        if kind == "thumbnail":
            out = ImageOps.contain(Image.new("L", (w, h)), (op["max_width"], op["max_height"])).size
            box = (0.0, 0.0, float(w), float(h))
        elif op.get("mode", "Stretch") == "Crop":
            out = (op["width"], op["height"])
            box = fit_box(w, h, *out)
        else:
            out = (op["width"], op["height"])
            box = (0.0, 0.0, float(w), float(h))
        filt = op["filter"]
        size = (map_size(out[0], w, m.shape[1]), map_size(out[1], h, m.shape[0]))
        new_map, exact = map_region(m, w, h, box, size, filt)
        # Pixel centers: x = x0 + (x' + 0.5) * (x1 - x0) / width - 0.5.
        sx, sy = (box[2] - box[0]) / out[0], (box[3] - box[1]) / out[1]
        step = affine([sx, 0, box[0] + 0.5 * sx - 0.5, 0, sy, box[1] + 0.5 * sy - 0.5])
        state.update(base=resample(base, out, filt, box), map=new_map, to_stored=state["to_stored"] @ step)
        return exact
    raise ValueError(kind)


def stored_positions(state, stored_size):
    """Positions in the stored base image of every current pixel, clamped to the image."""
    h, w = state["base"].shape[:2]
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    t = state["to_stored"]
    xs = t[0, 0] * x + t[0, 1] * y + t[0, 2]
    ys = t[1, 0] * x + t[1, 1] * y + t[1, 2]
    return np.clip(xs, 0, stored_size[0] - 1), np.clip(ys, 0, stored_size[1] - 1)


def orientation_affine(orientation, stored_size):
    """display position -> stored position for an EXIF orientation, fitted from orient() on index planes."""
    sw, sh = stored_size
    y, x = np.mgrid[0:sh, 0:sw].astype(np.float64)
    idx = orient(np.stack([x, y], axis=-1), orientation)
    o, dx, dy = idx[0, 0], idx[0, 1] - idx[0, 0], idx[1, 0] - idx[0, 0]
    t = affine([dx[0], dy[0], o[0], dx[1], dy[1], o[1]])
    yy, xx = np.mgrid[0:idx.shape[0], 0:idx.shape[1]]
    assert np.array_equal(t[0, 0] * xx + t[0, 1] * yy + t[0, 2], idx[..., 0])
    assert np.array_equal(t[1, 0] * xx + t[1, 1] * yy + t[1, 2], idx[..., 1])
    return t


def edit_ops(w, h, mw):
    """Named edit sequences for a base of w x h with a map mw pixels wide."""
    ops = {
        "flip_h": [dict(op="flip", mode="Horizontal")],
        "flip_v": [dict(op="flip", mode="Vertical")],
        "rotate90": [dict(op="rotate", degrees=90)],
        "rotate180": [dict(op="rotate", degrees=180)],
        "rotate270": [dict(op="rotate", degrees=270)],
        "crop_unaligned": [dict(op="crop", x=w // 7 + 1, y=h // 5 + 1, width=w // 2 + 1, height=h // 2 + 1)],
        "resize_half_lanczos": [dict(op="resize", width=w // 2, height=h // 2, filter="Lanczos3")],
        "resize_aspect_bicubic": [dict(op="resize", width=w * 5 // 7, height=h * 2 // 3, filter="Bicubic")],
        "resize_up_bilinear": [dict(op="resize", width=w * 3 // 2, height=h * 3 // 2, filter="Bilinear")],
        "resize_third_nearest": [dict(op="resize", width=w // 3, height=h // 3, filter="Nearest")],
        "resize_crop_mode": [dict(op="resize", width=w // 2, height=w // 2, filter="Lanczos3", mode="Crop")],
        "thumbnail": [dict(op="thumbnail", max_width=16, max_height=16, filter="Lanczos3")],
    }
    # The smallest positive offset on the map grid in both axes, when the scale allows one inside the image.
    sx = w / mw
    k = next((k for k in range(1, mw) if (k * w) % mw == 0 and k * w // mw < w // 2), None)
    if k is not None:
        step = k * w // mw
        ops["crop_aligned"] = [dict(op="crop", x=step, y=0, width=w - 2 * step, height=h)] if sx != int(sx) else \
            [dict(op="crop", x=step, y=int(sx), width=w // 2 // int(sx) * int(sx), height=h - 2 * int(sx))]
    ops["chain"] = ops["crop_unaligned"] + [dict(op="rotate", degrees=90)] + \
        [dict(op="resize", width=(h // 2 + 1) * 2 // 3, height=(w // 2 + 1) * 2 // 3, filter="Lanczos3")] + \
        [dict(op="flip", mode="Horizontal")]
    return ops


ORIENT_OPS = ("none", "flip_h", "rotate90", "crop_unaligned", "resize_half_lanczos", "thumbnail")
FLOAT_SOURCES = ("smooth_iso_half", "smooth_iso_quarter_rgb", "smooth_apple_half", "iso_rgb_p3", "iso_full_scale")


def fidelity(actual, expected):
    d = np.abs(actual - expected)
    rel = d / np.maximum(np.abs(expected), 1e-2)
    return {"max_absolute": float(d.max()), "median_relative": float(np.median(rel)),
            "p99_relative": float(np.percentile(rel, 99))}


def edits():
    os.makedirs(EDIT_OUT, exist_ok=True)
    for f in os.listdir(EDIT_OUT):
        os.remove(os.path.join(EDIT_OUT, f))
    by_name = {c["name"]: c for c in CASES}
    sources = {n: (by_name[n], os.path.join(OUT, n + ".jpg"), False) for n in
               ("iso_mono_srgb", "iso_rgb_p3", "iso_common_denominator", "iso_fractional_scale", "iso_odd_scale",
                "apple_makernote", "iso_and_apple", "iso_orientation6")}
    extra = [dict(name="iso_full_scale", size=(32, 24), map=(32, 24), channels=1, model="iso", iso=ISO_MONO),
             dict(name="iso_floor_quarter", size=(47, 33), map=(11, 8), channels=3, model="iso", iso=ISO_RGB, icc=P3_ICC)]
    extra += [dict(name="smooth_iso_half", size=(48, 32), map=(24, 16), channels=1, model="iso", iso=ISO_GAMMA,
                   icc=P3_ICC, smooth=True),
              dict(name="smooth_iso_quarter_rgb", size=(64, 48), map=(16, 12), channels=3, model="iso", iso=ISO_RGB,
                   smooth=True),
              dict(name="smooth_apple_half", size=(48, 32), map=(24, 16), channels=1, model="apple",
                   maker=(1.01, 1.0547851), icc=P3_ICC, smooth=True)]
    extra += [dict(name=f"iso_orient{o}", size=(46, 30), map=(23, 15), channels=1, model="iso", iso=ISO_GAMMA,
                   icc=P3_ICC, orientation=o) for o in range(1, 9)]
    for case in extra:
        path = os.path.join(EDIT_OUT, case["name"] + ".jpg")
        with open(path, "wb") as fh:
            fh.write(build(case))
        sources[case["name"]] = (case, path, case["name"].startswith("iso_orient"))

    reference = {"tolerance": EDIT_TOLERANCE, "sources": [], "edits": []}
    worst = {}
    total_floats = 0
    for name, (case, path, orient_matrix) in sources.items():
        with open(path, "rb") as fh:
            data = fh.read()
        case = dict(case)
        case["base_space"] = "p3" if case.get("icc") is P3_ICC else "srgb"
        case["icc"] = case.get("icc") is not None
        if case["model"] == "iso":
            case["iso_q"] = quantized_iso(case["iso"])
        stored_base, stored_map = decode_parts(data)
        orientation = case.get("orientation", 1)
        _, headroom = reconstruct(case, stored_base, stored_map)
        stored_size = (stored_base.shape[1], stored_base.shape[0])
        start = {"base": orient(stored_base, orientation), "map": orient(stored_map, orientation),
                 "to_stored": orientation_affine(orientation, stored_size)}
        h, w = start["base"].shape[:2]
        reference["sources"].append({"name": name, "file": os.path.relpath(path, os.path.join(HERE, "corpus")),
                                     "orientation": orientation, "width": w, "height": h,
                                     "map_width": stored_map.shape[1], "map_height": stored_map.shape[0],
                                     "channels": stored_map.shape[2], "headroom": headroom})
        ops = edit_ops(w, h, start["map"].shape[1])
        ops["none"] = []
        names = ORIENT_OPS if orient_matrix else [k for k in ops if k != "none"]
        for op_name in names:
            state = dict(start)
            exact = all([apply_edit(state, op) for op in ops[op_name]])
            entry_name = f"{name}.{op_name}"
            m = np.ascontiguousarray(state["map"])
            m.tofile(os.path.join(EDIT_OUT, entry_name + ".map"))
            edited_base = np.ascontiguousarray(state["base"])
            edited, _ = reconstruct(case, edited_base, m)
            ideal, _ = reconstruct(case, edited_base, stored_map, coords=stored_positions(state, stored_size))
            stats = fidelity(edited, ideal)
            key = ("smooth_" if case.get("smooth") else "synthetic_") + ("exact" if exact else "resampled")
            worst[key] = {k: max(v, worst.get(key, {}).get(k, 0.0)) for k, v in stats.items()}
            entry = {"name": entry_name, "source": name, "ops": ops[op_name], "width": state["base"].shape[1],
                     "height": state["base"].shape[0], "map_width": m.shape[1], "map_height": m.shape[0],
                     "map_exact": exact, "fidelity": stats}
            if name in FLOAT_SOURCES:
                write_f32(os.path.join(EDIT_OUT, entry_name + ".edited.f32"), edited)
                write_f32(os.path.join(EDIT_OUT, entry_name + ".ideal.f32"), ideal)
                entry["floats"] = True
                total_floats += 2 * edited.size * 4
            reference["edits"].append(entry)
    reference["measured_fidelity"] = worst
    budget = EDIT_TOLERANCE["fidelity"]
    for e in reference["edits"]:
        if e["source"].startswith("smooth_"):
            assert all(e["fidelity"][k] <= budget[k] for k in budget), e["name"]
    with open(os.path.join(HERE, "hdr_edit_reference.json"), "w") as fh:
        json.dump(reference, fh, indent=1)
        fh.write("\n")
    print(f"{len(reference['edits'])} edits of {len(sources)} sources in {EDIT_OUT}; floats {total_floats} bytes")
    print(json.dumps(worst, indent=1))


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))
    reference = {"tolerance": TOLERANCE, "oracle": ORACLE, "cases": [], "invalid": []}
    for case in CASES:
        data = build(case)
        name = case["name"]
        with open(os.path.join(OUT, name + ".jpg"), "wb") as fh:
            fh.write(data)
        case = dict(case)
        case["base_space"] = "p3" if case.get("icc") is P3_ICC else "srgb"
        case["icc"] = case.get("icc") is not None
        if case["model"] == "iso":
            case["iso_q"] = quantized_iso(case["iso"])
        base, map_px = decode_parts(data)
        orientation = case.get("orientation", 1)
        variants = {}
        for variant, target, out_space in (("full", None, None), ("srgb_t2", 2.0, "srgb")):
            hdr, headroom = reconstruct(case, base, map_px, target, out_space)
            hdr = orient(hdr, orientation)
            write_f32(os.path.join(OUT, f"{name}.{variant}.f32"), hdr)
            variants[variant] = {"headroom": headroom, "space": out_space or case["base_space"],
                                 "max": float(hdr.max()), "min": float(hdr.min())}
            if variant == "full":
                tm = tone_map(hdr, case["base_space"], headroom)
                tm.tofile(os.path.join(OUT, f"{name}.tonemap.rgba"))
                tm2 = tone_map(hdr, case["base_space"], headroom, exposure=-0.5, white=1.5)
                tm2.tofile(os.path.join(OUT, f"{name}.tonemap_ev.rgba"))
        oh, ow = orient(base, orientation).shape[:2]
        reference["cases"].append({
            "name": name, "model": case["model"], "width": ow, "height": oh, "orientation": orientation,
            "map_width": case["map"][0], "map_height": case["map"][1], "channels": case["channels"],
            "detected": ["Iso21496"] * (case["model"] == "iso") + ["Apple"] * (case["model"] == "apple" or bool(case.get("apple_too"))),
            "variants": variants,
        })

    # Damaged and unsupported files: expected HdrInfo state and the exception ToHdr throws.
    good = CASES[0]
    APPLE = next(c for c in CASES if c["name"] == "apple_makernote")
    invalid = {
        "iso_min_version1": (build(good, iso_version=1), "unsupported"),
        "iso_backward": (build(good, iso_flags=0x04), "unsupported"),
        "iso_zero_denominator": (build(good, break_iso=lambda p: p[:9] + b"\0\0\0\0" + p[13:]), "corrupt"),
        "iso_truncated": (build(good, break_iso=lambda p: p[:20]), "corrupt"),
        "iso_negative_offset": (build(dict(good, iso=dict(ISO_MONO, off_hdr=[-0.01]))), "corrupt"),
        "iso_version_only": (build(good, break_iso=lambda p: p[:4]), "corrupt"),
        "ultrahdr_xmp_only": (build(good, map_xmp=ultrahdr_xmp(), primary_iso=False), "unsupported"),
        "apple_unknown_version": (build(dict(APPLE), map_xmp=apple_xmp(version=0x40000)), "unsupported"),
        "apple_no_headroom": (build(dict(APPLE, maker=None)), "corrupt"),
        "base_adobe_rgb": (build(dict(good, icc=ADOBE_ICC)), "unsupported"),
        "iso_no_mpf": (build(good, mpf=False), "corrupt"),
    }
    data = build(good)
    mpf_at = data.index(b"MPF\0")
    bad = bytearray(data)
    struct.pack_into(">I", bad, mpf_at + 4 + 8 + 2 + 36 + 4 + 16 + 8, len(data) * 2)  # aux offset beyond the file
    invalid["mpf_offset_outside"] = (bytes(bad), "corrupt")
    plain_mpo = assemble(jpeg_bytes(base_image(32, 16, 1)), jpeg_bytes(base_image(32, 16, 2)), [])
    for name, (blob, kind) in invalid.items():
        with open(os.path.join(OUT, name + ".jpg"), "wb") as fh:
            fh.write(blob)
        reference["invalid"].append({"name": name, "failure": kind})
    with open(os.path.join(OUT, "plain_mpo.jpg"), "wb") as fh:
        fh.write(plain_mpo)
    with open(os.path.join(HERE, "hdr_reference.json"), "w") as fh:
        json.dump(reference, fh, indent=1)
        fh.write("\n")
    print(f"{len(reference['cases'])} valid, {len(reference['invalid'])} invalid files in {OUT}")
    edits()


if __name__ == "__main__":
    main()
