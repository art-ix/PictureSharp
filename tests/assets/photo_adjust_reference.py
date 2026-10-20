#!/usr/bin/env python3
"""Reference implementation of PictureSharp's photographic adjustments (docs/image-processing.md, "Photo
adjustments"), written from the documented formulas with numpy. Runs the jobs in <dir>/jobs.json and writes each
result to <dir>/<name>.rgba as tightly packed 8-bit RGBA, preceded by a line "<width> <height>\\n".

Each job: {"name", "input", "op", ...}; inputs are PNG files in <dir>.
  op "exposure":           "stops"
  op "white_balance":      "temperature", "tint"
  op "vibrance":           "amount"
  op "vignette":           "amount", "midpoint", "feather"
  op "shadows_highlights": "shadows", "highlights", "sigma"
  op "clarity":            "amount", "sigma"
  op "denoise":            "strength", "radius"

Arithmetic is float32 like the library, except box sums (float64); results may differ by 1 where a value lies on a
rounding boundary. Run with:  python3 -I tests/assets/photo_adjust_reference.py <dir>
"""
import json
import math
import os
import sys

import numpy as np
from PIL import Image

F = np.float32
LUMA = (F(0.2126), F(0.7152), F(0.0722))
ENCODE_STEPS = 65535


def srgb_decode_table():
    v = np.arange(256, dtype=np.float64) / 255.0
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4).astype(F)


def srgb_encode_table():
    v = np.arange(ENCODE_STEPS + 1, dtype=np.float64) / ENCODE_STEPS
    e = np.where(v <= 0.0031308, v * 12.92, 1.055 * v ** (1 / 2.4) - 0.055)
    return np.floor(e * 255 + 0.5).astype(np.uint8)  # round half away from zero (e >= 0)


DECODE = srgb_decode_table()
ENCODE = srgb_encode_table()


def encode(linear):
    linear = np.asarray(linear, dtype=F)
    index = np.where(linear > 0, np.where(linear < 1, np.floor(linear * F(ENCODE_STEPS) + F(0.5)), ENCODE_STEPS), 0)
    return ENCODE[np.nan_to_num(index).astype(np.int64)]


def to_byte(v):
    v = np.asarray(v, dtype=F)
    return np.where(v <= 0, 0, np.where(v >= 255, 255, np.floor(v + F(0.5)))).astype(np.uint8)


def exp2(x):
    return np.exp2(np.asarray(x, dtype=F)).astype(F)


def apply_gain(rgba, gain):
    out = rgba.copy()
    for c in range(3):
        out[..., c] = encode(DECODE[rgba[..., c]] * gain)
    return out


def exposure(rgba, stops):
    return apply_gain(rgba, exp2(stops))


def white_balance(rgba, temperature, tint):
    r, g, b = exp2(F(0.5) * F(temperature)), exp2(F(-0.5) * F(tint)), exp2(F(-0.5) * F(temperature))
    n = F(1) / (LUMA[0] * r + LUMA[1] * g + LUMA[2] * b)
    out = rgba.copy()
    for c, gain in enumerate((r * n, g * n, b * n)):
        out[..., c] = encode(DECODE[rgba[..., c]] * gain)
    return out


def vibrance(rgba, amount):
    amount = F(amount)
    rgb = rgba[..., :3].astype(F)
    mx, mn = rgb.max(axis=-1), rgb.min(axis=-1)
    with np.errstate(divide="ignore", invalid="ignore"):
        s = np.where(mx > 0, (mx - mn) / mx, F(0)).astype(F)
    gain = (F(1) + amount * (F(1) - s)) if amount >= 0 else np.full_like(s, F(1) + amount)
    y = LUMA[0] * rgb[..., 0] + LUMA[1] * rgb[..., 1] + LUMA[2] * rgb[..., 2]
    out = rgba.copy()
    changed = mx != mn
    for c in range(3):
        out[..., c] = np.where(changed, to_byte(y + gain * (rgb[..., c] - y)), rgba[..., c])
    return out


def vignette(rgba, amount, midpoint, feather):
    h, w = rgba.shape[:2]
    inner = F(midpoint)
    outer = inner + F(feather) * (F(1) - inner)
    inverse_radius = F(1) / (F(0.5) * F(math.sqrt(F(w) * F(w) + F(h) * F(h))))
    dy = np.arange(h, dtype=F)[:, None] - F((h - 1) * 0.5)
    dx = np.arange(w, dtype=F)[None, :] - F((w - 1) * 0.5)
    d = np.sqrt(dx * dx + dy * dy).astype(F) * inverse_radius
    if outer > inner:
        t = np.clip((d - inner) / (outer - inner), 0, 1).astype(F)
    else:
        t = (d >= inner).astype(F)
    gain = exp2(F(-2) * F(amount) * (t * t * (F(3) - F(2) * t)))
    out = apply_gain(rgba, gain)
    keep = t == 0
    out[keep] = rgba[keep]
    return out


def box(plane, radius):
    """Mean over (2r+1)^2 windows clipped to the plane: horizontal pass stored as float32, then vertical."""
    if radius == 0:
        return plane.copy()

    def mean_axis(a, axis):
        n = a.shape[axis]
        c = np.cumsum(a.astype(np.float64), axis=axis)
        c = np.concatenate([np.zeros_like(np.take(c, [0], axis=axis)), c], axis=axis)
        idx = np.arange(n)
        lo, hi = np.maximum(0, idx - radius), np.minimum(n - 1, idx + radius)
        s = np.take(c, hi + 1, axis=axis) - np.take(c, lo, axis=axis)
        count = (hi - lo + 1).astype(np.float64)
        shape = [1, 1]
        shape[axis] = n
        return (s / count.reshape(shape)).astype(F)

    return mean_axis(mean_axis(plane, 1), 0)


def box_radii(sigma, n=3):
    variance = 12.0 * float(F(sigma)) * float(F(sigma))
    lower = int(math.floor(math.sqrt(variance / n + 1)))
    if lower % 2 == 0:
        lower -= 1
    count = int(math.floor((variance - n * lower * lower - 4 * n * lower - 3 * n) / (-4 * lower - 4) + 0.5))
    return [((lower if i < count else lower + 2) - 1) // 2 for i in range(n)]


def gaussian(plane, sigma):
    for r in box_radii(sigma):
        plane = box(plane, r)
    return plane


def luma_plane(rgba):
    rgb = rgba[..., :3].astype(F)
    return ((LUMA[0] * rgb[..., 0] + LUMA[1] * rgb[..., 1] + LUMA[2] * rgb[..., 2]) / F(255)).astype(F)


def shadows_highlights(rgba, shadows, highlights, sigma):
    b = gaussian(luma_plane(rgba), sigma)
    dark = F(1) - b
    stops = F(1.5) * (F(shadows) * dark * dark + F(highlights) * b * b)
    return apply_gain(rgba, exp2(stops))


def clarity(rgba, amount, sigma):
    p = luma_plane(rgba)
    b = gaussian(p, sigma)
    delta = F(255) * F(amount) * (p - b) * (F(4) * p * (F(1) - p))
    out = rgba.copy()
    for c in range(3):
        out[..., c] = to_byte(rgba[..., c].astype(F) + delta)
    return out


def denoise(rgba, strength, radius):
    eps = F(0.05) * F(strength) * (F(0.05) * F(strength))
    alpha = rgba[..., 3]
    out = rgba.copy()
    for c in range(3):
        i = (rgba[..., c].astype(np.int64) * alpha).astype(F) / F(65025)
        mean = box(i, radius)
        square = box((i * i).astype(F), radius)
        variance = np.maximum(F(0), square - mean * mean)
        a = (variance / (variance + eps)).astype(F)
        b = (mean * (F(1) - a)).astype(F)
        q = box(a, radius) * i + box(b, radius)
        with np.errstate(divide="ignore", invalid="ignore"):
            value = to_byte(q * F(65025) / alpha.astype(F))
        out[..., c] = np.where(alpha == 0, rgba[..., c], value)
    return out


def run(job, rgba):
    op = job["op"]
    if op == "exposure":
        return exposure(rgba, job["stops"])
    if op == "white_balance":
        return white_balance(rgba, job["temperature"], job["tint"])
    if op == "vibrance":
        return vibrance(rgba, job["amount"])
    if op == "vignette":
        return vignette(rgba, job["amount"], job["midpoint"], job["feather"])
    if op == "shadows_highlights":
        return shadows_highlights(rgba, job["shadows"], job["highlights"], job["sigma"])
    if op == "clarity":
        return clarity(rgba, job["amount"], job["sigma"])
    if op == "denoise":
        return denoise(rgba, job["strength"], job["radius"])
    raise ValueError(op)


def main():
    directory = sys.argv[1]
    with open(os.path.join(directory, "jobs.json")) as f:
        jobs = json.load(f)
    for job in jobs:
        with Image.open(os.path.join(directory, job["input"])) as im:
            rgba = np.asarray(im.convert("RGBA"), dtype=np.uint8).copy()
        result = run(job, rgba)
        h, w = result.shape[:2]
        with open(os.path.join(directory, job["name"] + ".rgba"), "wb") as f:
            f.write(f"{w} {h}\n".encode("ascii"))
            f.write(np.ascontiguousarray(result, dtype=np.uint8).tobytes())


if __name__ == "__main__":
    main()
