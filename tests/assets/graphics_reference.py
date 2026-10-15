#!/usr/bin/env python3
"""Generates the graphics-operations corpus in tests/assets/corpus/graphics/ (0.6.3).

Writes small inputs derived from tests/assets/corpus/src/, reference PNGs and graphics_reference.json for:
  - levels/gamma: exact double-precision 256-entry LUT, rounded half up (tolerance 0);
  - autocontrast/equalize: Pillow 10.2.0 ImageOps on the RGB channels, alpha re-attached (bit-exact);
  - histogram: R, G, B, A and BT.601 luma (Pillow "L") counts, all pixels or only alpha > 0;
  - trim bounds: alpha threshold (cross-checked with Pillow getbbox) or Chebyshev RGBA distance to a background
    (fully transparent pixels match a fully transparent background);
  - masks: A' = round(A * m / 255) with integer rounding (tolerance 0);
  - blend modes: W3C Compositing and Blending Level 1 separable modes, composited source-over in double
    precision, optionally in linear-light sRGB (tolerance 1 per 8-bit channel, alpha included).

  python3 -I tests/assets/graphics_reference.py              # regenerate the corpus
  python3 -I tests/assets/graphics_reference.py --calibrate  # compare the models with Pillow and ImageMagick
"""
import json
import os
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image, ImageChops, ImageOps

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "corpus", "src")
OUT = os.path.join(HERE, "corpus", "graphics")

# Tolerances fixed before the implementation (plan.md 0.6.3).
TOLERANCE = {"tone_max_abs": 0, "mask_max_abs": 0, "blend_max_abs": 1}


# ------------------------------------------------------------------ inputs

def load_src(name):
    return Image.open(os.path.join(SRC, name))


def make_inputs():
    """Small inputs keep the committed references compact."""
    photo = load_src("photo.png").convert("RGB").resize((96, 72), Image.Resampling.LANCZOS)
    photo_alpha = load_src("photo_alpha.png").convert("RGBA").resize((96, 72), Image.Resampling.LANCZOS)
    # Low contrast (60..180) so that autocontrast and equalize have something to stretch.
    arr = np.asarray(photo, dtype=np.float64)
    low = Image.fromarray((60 + arr * (120 / 255)).round().astype(np.uint8), "RGB")
    source = load_src("graphic_alpha.png").convert("RGBA").resize((64, 48), Image.Resampling.LANCZOS)
    # Mask: horizontal alpha ramp, vertical luminance ramp, so the two channels select different pixels.
    x = np.linspace(0, 255, 40)
    y = np.linspace(255, 0, 30)
    mask = np.zeros((30, 40, 4), np.uint8)
    mask[..., 0] = np.round(y)[:, None]
    mask[..., 1] = np.round(y)[:, None] // 2
    mask[..., 2] = 255 - np.round(y)[:, None]
    mask[..., 3] = np.round(x)[None, :]
    inputs = {
        "photo.png": photo,
        "photo_alpha.png": photo_alpha,
        "low_contrast.png": low,
        "source.png": source,
        "mask.png": Image.fromarray(mask, "RGBA"),
    }
    inputs.update(trim_inputs())
    return inputs


def trim_inputs():
    # Sprite on a transparent canvas with a faint (alpha 40) halo.
    sprite = np.zeros((40, 50, 4), np.uint8)
    sprite[12:30, 7:41] = (200, 30, 30, 40)
    sprite[15:27, 10:38] = (220, 40, 40, 255)
    # Product on white with JPEG-like noise near the background.
    rng = np.random.default_rng(63)
    product = np.full((36, 48, 4), 255, np.uint8)
    noise = rng.integers(0, 9, size=(36, 48, 3))
    product[..., :3] -= noise.astype(np.uint8)
    product[9:25, 14:33, :3] = (30, 90, 160)
    # Uniform image: nothing to trim.
    flat = np.full((10, 12, 4), (10, 200, 10, 255), np.uint8)
    empty = np.zeros((9, 7, 4), np.uint8)
    # Content touching the edges, so padding must be clamped to the canvas.
    edge = np.zeros((20, 20, 4), np.uint8)
    edge[0:6, 15:20] = (0, 0, 0, 255)
    edge[14:20, 0:3] = (255, 255, 255, 128)
    # Invisible colored pixels: alpha 0, so they never count as content.
    edge[8:10, 5:9] = (90, 10, 200, 0)
    return {
        "trim_sprite.png": Image.fromarray(sprite, "RGBA"),
        "trim_product.png": Image.fromarray(product, "RGBA"),
        "trim_flat.png": Image.fromarray(flat, "RGBA"),
        "trim_empty.png": Image.fromarray(empty, "RGBA"),
        "trim_edge.png": Image.fromarray(edge, "RGBA"),
    }


def rgba(im):
    return np.asarray(im.convert("RGBA"), dtype=np.uint8).copy()


# ------------------------------------------------------------------ tone

def levels_lut(in_black, in_white, gamma, out_black, out_white):
    """out = ob + (ow - ob) * clamp((x - ib) / (iw - ib), 0, 1) ^ (1 / gamma), rounded half up.

    gamma is a C# float: it is widened from float32 exactly as the implementation does.
    """
    g = float(np.float32(gamma))
    x = np.arange(256, dtype=np.float64)
    t = np.clip((x - in_black) / float(in_white - in_black), 0.0, 1.0)
    v = out_black + (out_white - out_black) * np.power(t, 1.0 / g)
    return np.floor(v + 0.5).astype(np.uint8)


CHANNELS = {"rgb": (0, 1, 2), "red": (0,), "green": (1,), "blue": (2,)}

LEVELS = {
    "levels_input": ("photo.png", 20, 230, 1.0, 0, 255, "rgb"),
    "levels_gamma": ("photo.png", 0, 255, 1.8, 0, 255, "rgb"),
    "levels_all": ("photo_alpha.png", 15, 240, 0.7, 30, 200, "rgb"),
    "levels_inverted_output": ("photo.png", 10, 245, 1.0, 255, 0, "rgb"),
    "levels_red_only": ("photo_alpha.png", 40, 200, 1.3, 0, 255, "red"),
    "levels_blue_only": ("photo.png", 0, 128, 1.0, 64, 255, "blue"),
    "gamma_brighten": ("photo_alpha.png", 0, 255, 2.2, 0, 255, "rgb"),
    "gamma_darken": ("photo.png", 0, 255, 0.5, 0, 255, "rgb"),
    "gamma_green_only": ("photo.png", 0, 255, 1.6, 0, 255, "green"),
}


def apply_levels(im, ib, iw, g, ob, ow, channels):
    a = rgba(im)
    lut = levels_lut(ib, iw, g, ob, ow)
    for c in CHANNELS[channels]:
        a[..., c] = lut[a[..., c]]
    return a


AUTOCONTRAST = {
    "autocontrast_plain": ("low_contrast.png", 0, 0, False),
    "autocontrast_cutoff": ("low_contrast.png", 2, 2, False),
    "autocontrast_asymmetric": ("photo.png", 1, 5, False),
    "autocontrast_fraction": ("photo_alpha.png", 0.5, 0.5, False),
    "autocontrast_preserve_tone": ("low_contrast.png", 1, 1, True),
}

EQUALIZE = {
    "equalize_low_contrast": "low_contrast.png",
    "equalize_photo": "photo.png",
    "equalize_alpha": "photo_alpha.png",
}


def on_rgb(im, op):
    """Pillow ImageOps supports L/RGB only: apply to the RGB channels and re-attach the original alpha."""
    a = rgba(im)
    rgb = Image.fromarray(a[..., :3], "RGB")
    a[..., :3] = np.asarray(op(rgb), dtype=np.uint8)
    return a


def autocontrast(im, low, high, preserve):
    cutoff = (low, high) if low != high else low
    return on_rgb(im, lambda rgb: ImageOps.autocontrast(rgb, cutoff=cutoff, preserve_tone=preserve))


# ------------------------------------------------------------------ histogram

def luma(a):
    """BT.601 luma, as Pillow's RGB -> L conversion and PictureSharp's PixelConverter.Luma."""
    r, g, b = (a[..., i].astype(np.int64) for i in range(3))
    return ((19595 * r + 38470 * g + 7471 * b + 32768) >> 16).astype(np.uint8)


def histogram(a, transparency):
    if transparency == "exclude":
        a = a[a[..., 3] > 0]
    else:
        a = a.reshape(-1, 4)
    channels = {name: a[..., i] for i, name in enumerate("rgba")}
    channels["luminance"] = luma(a)
    result = {k: np.bincount(v.ravel(), minlength=256).tolist() for k, v in channels.items()}
    result["pixel_count"] = int(a.shape[0])
    return result


HISTOGRAMS = {
    "histogram_photo": ("photo.png", "include"),
    "histogram_alpha_include": ("photo_alpha.png", "include"),
    "histogram_alpha_exclude": ("photo_alpha.png", "exclude"),
    "histogram_sprite_exclude": ("trim_sprite.png", "exclude"),
}


# ------------------------------------------------------------------ trim

def trim_bounds(a, mode, alpha_threshold=0, background=None, tolerance=0, padding=0):
    """[x, y, width, height] of the content, or None when nothing differs from the background."""
    if mode == "alpha":
        content = a[..., 3] > alpha_threshold
    else:
        bg = np.array(background if background is not None else a[0, 0], np.int64)
        content = np.abs(a.astype(np.int64) - bg).max(axis=-1) > tolerance
        # Fully transparent pixels match a fully transparent background whatever their color.
        if bg[3] == 0:
            content &= a[..., 3] != 0
    ys, xs = np.nonzero(content)
    if len(xs) == 0:
        return None
    h, w = content.shape
    x0 = max(0, xs.min() - padding)
    y0 = max(0, ys.min() - padding)
    x1 = min(w, xs.max() + 1 + padding)
    y1 = min(h, ys.max() + 1 + padding)
    return [int(x0), int(y0), int(x1 - x0), int(y1 - y0)]


TRIMS = {
    "trim_alpha": ("trim_sprite.png", {"mode": "alpha"}),
    "trim_alpha_threshold": ("trim_sprite.png", {"mode": "alpha", "alpha_threshold": 40}),
    "trim_alpha_padding": ("trim_sprite.png", {"mode": "alpha", "alpha_threshold": 40, "padding": 4}),
    "trim_background_exact": ("trim_product.png", {"mode": "background"}),
    "trim_background_tolerance": ("trim_product.png", {"mode": "background", "tolerance": 10}),
    "trim_background_explicit": ("trim_product.png", {"mode": "background", "background": [255, 255, 255, 255], "tolerance": 10}),
    "trim_flat": ("trim_flat.png", {"mode": "background"}),
    "trim_empty": ("trim_empty.png", {"mode": "alpha"}),
    "trim_edge_padding": ("trim_edge.png", {"mode": "alpha", "padding": 3}),
    "trim_edge_background": ("trim_edge.png", {"mode": "background", "padding": 1}),
    "trim_sprite_background": ("trim_sprite.png", {"mode": "background", "tolerance": 39}),
    "trim_sprite_explicit_red": ("trim_sprite.png", {"mode": "background", "background": [220, 40, 40, 255]}),
}


# ------------------------------------------------------------------ masks

def apply_mask(a, m, x, y, channel, invert, outside):
    """A' = round(A * v / 255), RGB untouched. v is the mask's alpha or the BT.601 luma of its color."""
    a = a.copy()
    values = m[..., 3] if channel == "alpha" else luma(m)
    if invert:
        values = 255 - values
    h, w = a.shape[:2]
    factor = np.full((h, w), 255 if outside == "keep" else 0, np.int64)
    mh, mw = values.shape
    x0, y0, x1, y1 = max(0, x), max(0, y), min(w, x + mw), min(h, y + mh)
    if x0 < x1 and y0 < y1:
        factor[y0:y1, x0:x1] = values[y0 - y:y1 - y, x0 - x:x1 - x]
    alpha = a[..., 3].astype(np.int64)
    a[..., 3] = ((alpha * factor * 2 + 255) // 510).astype(np.uint8)
    return a


MASKS = {
    "mask_alpha": ("photo_alpha.png", 10, 8, "alpha", False, "keep"),
    "mask_luminance": ("photo_alpha.png", 30, 20, "luminance", False, "keep"),
    "mask_inverted": ("photo.png", 5, 5, "alpha", True, "keep"),
    "mask_outside_transparent": ("photo.png", 40, 30, "luminance", False, "transparent"),
    "mask_negative_position": ("photo_alpha.png", -15, -10, "alpha", False, "transparent"),
}


# ------------------------------------------------------------------ blend

def srgb_decode(v):
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)


def srgb_encode(v):
    v = np.clip(v, 0.0, 1.0)
    return np.where(v <= 0.0031308, v * 12.92, 1.055 * v ** (1 / 2.4) - 0.055)


def hard_light(cb, cs):
    return np.where(cs <= 0.5, cb * 2 * cs, screen(cb, 2 * cs - 1))


def screen(cb, cs):
    return cb + cs - cb * cs


def soft_light(cb, cs):
    d = np.where(cb <= 0.25, ((16 * cb - 12) * cb + 4) * cb, np.sqrt(cb))
    return np.where(cs <= 0.5, cb - (1 - 2 * cs) * cb * (1 - cb), cb + (2 * cs - 1) * (d - cb))


def color_dodge(cb, cs):
    with np.errstate(divide="ignore", invalid="ignore"):
        q = np.minimum(1.0, cb / (1 - cs))
    return np.where(cb == 0, 0.0, np.where(cs >= 1, 1.0, q))


def color_burn(cb, cs):
    with np.errstate(divide="ignore", invalid="ignore"):
        q = 1 - np.minimum(1.0, (1 - cb) / cs)
    return np.where(cb >= 1, 1.0, np.where(cs <= 0, 0.0, q))


# W3C Compositing and Blending Level 1, 10.1 (separable blend modes): B(Cb, Cs).
BLEND = {
    "multiply": lambda cb, cs: cb * cs,
    "screen": screen,
    "overlay": lambda cb, cs: hard_light(cs, cb),
    "darken": np.minimum,
    "lighten": np.maximum,
    "difference": lambda cb, cs: np.abs(cb - cs),
    "hard_light": hard_light,
    "soft_light": soft_light,
    "color_dodge": color_dodge,
    "color_burn": color_burn,
    "exclusion": lambda cb, cs: cb + cs - 2 * cb * cs,
}


def blend(dst, src, x, y, mode, opacity, linear):
    """Source-over of src at (x, y) with Cs' = (1 - ab) Cs + ab B(Cb, Cs) (W3C 5.8 / 9.1). Straight alpha.

    Transparent source pixels leave the destination untouched, as in the default DrawImage.
    """
    out = dst.copy()
    h, w = dst.shape[:2]
    sh, sw = src.shape[:2]
    x0, y0, x1, y1 = max(0, x), max(0, y), min(w, x + sw), min(h, y + sh)
    if x0 >= x1 or y0 >= y1:
        return out
    d = dst[y0:y1, x0:x1].astype(np.float64) / 255
    s = src[y0 - y:y1 - y, x0 - x:x1 - x].astype(np.float64) / 255
    cb, ab = d[..., :3], d[..., 3:4]
    cs, as_ = s[..., :3], s[..., 3:4] * float(np.float32(opacity))
    if linear:
        cb, cs = srgb_decode(cb), srgb_decode(cs)
    mixed = (1 - ab) * cs + ab * BLEND[mode](cb, cs)
    ao = as_ + ab * (1 - as_)
    with np.errstate(divide="ignore", invalid="ignore"):
        co = np.where(ao > 0, (as_ * mixed + ab * (1 - as_) * cb) / ao, 0.0)
    if linear:
        co = srgb_encode(co)
    region = np.concatenate([np.floor(co * 255 + 0.5), np.floor(ao * 255 + 0.5)], axis=-1).astype(np.uint8)
    keep = s[..., 3] == 0
    region[keep] = dst[y0:y1, x0:x1][keep]
    out[y0:y1, x0:x1] = region
    return out


def blend_cases():
    cases = {}
    for mode in BLEND:
        cases[f"blend_{mode}"] = ("photo_alpha.png", "source.png", 20, 12, mode, 1.0, False)
        cases[f"blend_{mode}_opaque"] = ("photo.png", "source.png", -8, 30, mode, 0.6, False)
    for mode in ("multiply", "screen", "overlay", "soft_light"):
        cases[f"blend_{mode}_linear"] = ("photo_alpha.png", "source.png", 40, -6, mode, 0.8, True)
    return cases


# ------------------------------------------------------------------ main

def save(a, name):
    Image.fromarray(a, "RGBA").save(os.path.join(OUT, name), optimize=True)


def main():
    os.makedirs(OUT, exist_ok=True)
    inputs = make_inputs()
    for name, im in inputs.items():
        im.save(os.path.join(OUT, name), optimize=True)
    arr = {name: rgba(im) for name, im in inputs.items()}

    tone = {}
    for name, (src, ib, iw, g, ob, ow, ch) in LEVELS.items():
        save(apply_levels(inputs[src], ib, iw, g, ob, ow, ch), f"{name}.ref.png")
        tone[name] = {"op": "gamma" if name.startswith("gamma") else "levels", "input": src, "in_black": ib, "in_white": iw,
                      "gamma": g, "out_black": ob, "out_white": ow, "channels": ch, "reference": f"{name}.ref.png"}
    for name, (src, lo, hi, preserve) in AUTOCONTRAST.items():
        save(autocontrast(inputs[src], lo, hi, preserve), f"{name}.ref.png")
        tone[name] = {"op": "autocontrast", "input": src, "cutoff_low": lo, "cutoff_high": hi, "preserve_tone": preserve,
                      "reference": f"{name}.ref.png"}
    for name, src in EQUALIZE.items():
        save(on_rgb(inputs[src], ImageOps.equalize), f"{name}.ref.png")
        tone[name] = {"op": "equalize", "input": src, "reference": f"{name}.ref.png"}

    histograms = {name: {"input": src, "transparency": t, **histogram(arr[src], t)} for name, (src, t) in HISTOGRAMS.items()}

    trims = {}
    for name, (src, opts) in TRIMS.items():
        trims[name] = {"input": src, **opts, "bounds": trim_bounds(arr[src], **opts)}

    masks = {}
    for name, (src, x, y, ch, inv, outside) in MASKS.items():
        save(apply_mask(arr[src], arr["mask.png"], x, y, ch, inv, outside), f"{name}.ref.png")
        masks[name] = {"input": src, "mask": "mask.png", "x": x, "y": y, "channel": ch, "invert": inv, "outside": outside,
                       "reference": f"{name}.ref.png"}

    blends = {}
    for name, (dst, src, x, y, mode, opacity, linear) in blend_cases().items():
        save(blend(arr[dst], arr[src], x, y, mode, opacity, linear), f"{name}.ref.png")
        blends[name] = {"input": dst, "source": src, "x": x, "y": y, "mode": mode, "opacity": opacity, "linear_light": linear,
                        "reference": f"{name}.ref.png"}

    manifest = {
        "description": "PictureSharp 0.6.3 graphics operations; generated by tests/assets/graphics_reference.py",
        "tolerance": TOLERANCE,
        "tone": {
            "levels_formula": "ob + (ow - ob) * clamp((x - ib) / (iw - ib), 0, 1) ^ (1 / gamma), double, floor(v + 0.5)",
            "oracle": "levels/gamma: exact LUT; autocontrast/equalize: Pillow 10.2.0 ImageOps on RGB, alpha unchanged",
            "cases": tone,
        },
        "histogram": {"luminance": "BT.601 (19595 R + 38470 G + 7471 B + 32768) >> 16", "cases": histograms},
        "trim": {"background_distance": "max |channel difference| over RGBA; default background is the top-left pixel; "
                                        "alpha-0 pixels match an alpha-0 background",
                 "cases": trims},
        "mask": {"formula": "A' = (A * v * 2 + 255) / 510 (integer); v = mask alpha or BT.601 luma", "cases": masks},
        "blend": {"oracle": "W3C Compositing and Blending Level 1 separable modes, source-over, double precision, "
                            "straight alpha, floor(v * 255 + 0.5); linear_light decodes/encodes sRGB around the blend",
                  "cases": blends},
    }
    with open(os.path.join(OUT, "graphics_reference.json"), "w") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"wrote {len(os.listdir(OUT))} files to {OUT}")


# ------------------------------------------------------------------ calibration

def calibrate():
    """Independent cross-checks of the models; prints the largest differences."""
    inputs = make_inputs()
    arr = {name: rgba(im) for name, im in inputs.items()}

    # Opaque blends against Pillow ImageChops (8-bit integer arithmetic). Pillow's soft_light is not the W3C formula.
    dst = arr["photo.png"]
    src = arr["low_contrast.png"]
    chops = {"multiply": ImageChops.multiply, "screen": ImageChops.screen, "overlay": ImageChops.overlay,
             "darken": ImageChops.darker, "lighten": ImageChops.lighter, "difference": ImageChops.difference,
             "hard_light": ImageChops.hard_light}
    for mode, fn in chops.items():
        ours = blend(dst, src, 0, 0, mode, 1.0, False)[..., :3]
        pil = np.asarray(fn(Image.fromarray(dst[..., :3]), Image.fromarray(src[..., :3])), dtype=np.int64)
        print(f"blend {mode:11s} vs Pillow ImageChops: max {np.abs(ours.astype(np.int64) - pil).max()}")

    # Alpha trim against Pillow getbbox (RGBA images use only the alpha band).
    for name in ("trim_sprite.png", "trim_edge.png", "trim_empty.png"):
        box = inputs[name].getbbox()
        ours = trim_bounds(arr[name], "alpha")
        pil = None if box is None else [box[0], box[1], box[2] - box[0], box[3] - box[1]]
        print(f"trim {name}: ours {ours}, Pillow getbbox {pil}")

    # Levels against ImageMagick 6 -level (percent arguments, Q16 internally).
    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, "in.png")
        inputs["photo.png"].save(path)
        for name, (src, ib, iw, g, ob, ow, ch) in LEVELS.items():
            if src != "photo.png" or ch != "rgb":
                continue
            out = os.path.join(tmp, f"{name}.png")
            args = ["convert", path, "-level", f"{ib / 2.55}%,{iw / 2.55}%,{g}"]
            if (ob, ow) != (0, 255):
                args += ["+level", f"{ob / 2.55}%,{ow / 2.55}%"]
            try:
                subprocess.run(args + ["-depth", "8", out], check=True)
            except (OSError, subprocess.CalledProcessError) as e:
                print(f"levels {name}: ImageMagick unavailable ({e})")
                continue
            ours = apply_levels(inputs[src], ib, iw, g, ob, ow, ch)[..., :3].astype(np.int64)
            im = np.asarray(Image.open(out).convert("RGB"), dtype=np.int64)
            print(f"levels {name:24s} vs ImageMagick: max {np.abs(ours - im).max()}")


if __name__ == "__main__":
    if "--calibrate" in sys.argv:
        calibrate()
    else:
        main()
