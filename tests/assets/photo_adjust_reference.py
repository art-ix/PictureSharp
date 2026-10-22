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
  op "curves":             "points" ([[x, y], ...]), "channels" (bit mask: 1 red, 2 green, 4 blue)
  op "white_balance_neutral": "neutral" ([r, g, b])
  op "auto_white_balance": "strength"
  op "remove_dust":        "size", "threshold"
  op "reduce_color_noise": "strength", "radius" (0 = automatic)
  op "analyze_negative":   "options" (see negative_options); writes <name>.json with the calibration, clipped
                           fractions and warnings, and the input pixels
  op "invert_negative":    "calibration" ({film_type, base, range, gamma}), "print" ({contrast, curve, exposure})
  op "develop_negative":   "options"; analysis (unless options.calibration), inversion, dust and color noise

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


def sign(v):
    return (v > 0) - (v < 0)


def curves(rgba, points, channels):
    """PCHIP (Fritsch-Butland derivatives, Moler's end conditions) in double precision, constant outside."""
    xs = [float(F(p[0])) for p in points]
    ys = [float(F(p[1])) for p in points]
    n = len(xs)
    m = [(ys[k + 1] - ys[k]) / (xs[k + 1] - xs[k]) for k in range(n - 1)]
    d = [0.0] * n
    if n == 2:
        d[0] = d[1] = m[0]
    else:
        for k in range(1, n - 1):
            h0, h1 = xs[k] - xs[k - 1], xs[k + 1] - xs[k]
            if sign(m[k - 1]) != sign(m[k]) or m[k - 1] == 0 or m[k] == 0:
                d[k] = 0.0
            else:
                w1, w2 = 2 * h1 + h0, h1 + 2 * h0
                d[k] = 1.0 / ((w1 / m[k - 1] + w2 / m[k]) / (w1 + w2))

        def edge(h0, h1, m0, m1):
            e = ((2 * h0 + h1) * m0 - h0 * m1) / (h0 + h1)
            if sign(e) != sign(m0):
                return 0.0
            return 3 * m0 if sign(m0) != sign(m1) and abs(e) > 3 * abs(m0) else e

        d[0] = edge(xs[1] - xs[0], xs[2] - xs[1], m[0], m[1])
        d[n - 1] = edge(xs[n - 1] - xs[n - 2], xs[n - 2] - xs[n - 3], m[n - 2], m[n - 3])

    table = np.zeros(256, dtype=np.uint8)
    segment = 0
    for x in range(256):
        if x <= xs[0]:
            value = ys[0]
        elif x >= xs[n - 1]:
            value = ys[n - 1]
        else:
            while x > xs[segment + 1]:
                segment += 1
            h = xs[segment + 1] - xs[segment]
            t = (x - xs[segment]) / h
            t2, t3 = t * t, t * t * t
            value = ((2 * t3 - 3 * t2 + 1) * ys[segment] + (t3 - 2 * t2 + t) * h * d[segment]
                     + (-2 * t3 + 3 * t2) * ys[segment + 1] + (t3 - t2) * h * d[segment + 1])
        table[x] = int(math.floor(min(max(value, 0.0), 255.0) + 0.5))
    out = rgba.copy()
    for c in range(3):
        if channels & (1 << c):
            out[..., c] = table[rgba[..., c]]
    return out


def gains(rgba, r, g, b):
    out = rgba.copy()
    for c, gain in enumerate((F(r), F(g), F(b))):
        out[..., c] = encode(DECODE[rgba[..., c]] * gain)
    return out


def white_balance_neutral(rgba, neutral):
    r, g, b = (DECODE[v] for v in neutral)
    luma = LUMA[0] * r + LUMA[1] * g + LUMA[2] * b
    return gains(rgba, luma / r, luma / g, luma / b)


def int_luma(rgb):
    rgb = rgb.astype(np.int64)
    return (2126 * rgb[..., 0] + 7152 * rgb[..., 1] + 722 * rgb[..., 2] + 5000) // 10000


def pow32(x, y):
    return np.exp2(F(y) * np.log2(F(x))).astype(F)


def auto_white_balance(rgba, strength):
    rgb = rgba[..., :3]
    candidate = (rgba[..., 3] != 0) & np.all((rgb >= 3) & (rgb <= 252), axis=-1)
    luma = int_luma(rgb)
    counts = np.bincount(luma[candidate], minlength=256)
    total = int(counts.sum())
    if total == 0:
        return rgba.copy()
    cumulative = np.cumsum(counts)
    low = int(np.argmax(cumulative * 10 >= total))
    high = int(np.argmax(cumulative * 10 >= total * 9))
    selected = candidate & (luma >= low) & (luma <= high)
    mean = []
    for c in range(3):
        hist = np.bincount(rgb[..., c][selected], minlength=256)
        n = int(hist.sum())
        acc = 0.0
        for v in range(256):
            acc += int(hist[v]) * float(DECODE[v])
        mean.append(F(acc / n))
    y = LUMA[0] * mean[0] + LUMA[1] * mean[1] + LUMA[2] * mean[2]
    r, g, b = (pow32(y / mean[c], strength) for c in range(3))
    normalize = y / (LUMA[0] * r * mean[0] + LUMA[1] * g * mean[1] + LUMA[2] * b * mean[2])
    return gains(rgba, r * normalize, g * normalize, b * normalize)


def lower_median(values):
    values = np.sort(np.asarray(values).ravel())
    return int(values[(len(values) - 1) // 2])


def remove_dust(rgba, size, threshold):
    h, w = rgba.shape[:2]
    luma = int_luma(rgba[..., :3])
    tile = 32
    floor = F(threshold) * F(255)
    limits = np.zeros(((h + tile - 1) // tile, (w + tile - 1) // tile), dtype=F)
    for ty in range(limits.shape[0]):
        for tx in range(limits.shape[1]):
            block = luma[ty * tile:(ty + 1) * tile, tx * tile:(tx + 1) * tile]
            m = lower_median(block)
            mad = lower_median(np.abs(block - m))
            limits[ty, tx] = max(floor, F(3) * (F(1.4826) * F(mad)))

    def limit(x, y):
        return limits[y // tile, x // tile]

    directions = ((1, 0), (0, 1), (1, 1), (1, -1))
    mask = np.zeros((h, w), dtype=np.uint8)
    for y in range(h):
        for x in range(w):
            value = int(luma[y, x])
            low, high = F(value) - limit(x, y), F(value) + limit(x, y)
            bright = dark = True
            for dx, dy in directions:
                seg = [int(luma[y + k * dy, x + k * dx]) for k in range(-size, size + 1)
                       if 0 <= x + k * dx < w and 0 <= y + k * dy < h]
                rank = (len(seg) - 1) // 2
                bright &= sum(F(v) < low for v in seg) >= rank + 1
                dark &= sum(F(v) > high for v in seg) >= len(seg) - rank
                if not bright and not dark:
                    break
            mask[y, x] = 1 if bright else 2 if dark else 0

    def window(a, x, y):
        return a[max(0, y - size):y + size + 1, max(0, x - size):x + size + 1]

    for _ in range(size):
        nxt = mask.copy()
        added = 0
        for y in range(h):
            for x in range(w):
                if mask[y, x] != 0:
                    continue
                near = mask[max(0, y - 1):y + 2, max(0, x - 1):x + 2] & 3
                specks = int(np.bitwise_or.reduce(near.ravel(), initial=0))
                if specks == 0:
                    continue
                deviation = F(int(luma[y, x]) - lower_median(window(luma, x, y)))
                half = F(0.5) * limit(x, y)
                if specks & 1 and deviation > half:
                    nxt[y, x] = 1
                    added += 1
                elif specks & 2 and -deviation > half:
                    nxt[y, x] = 2
                    added += 1
                else:
                    nxt[y, x] = 4
        mask = nxt
        if added == 0:
            break

    out = rgba.copy()
    speck = (mask & 3) != 0
    for y, x in zip(*np.nonzero(speck)):
        keep = ~window(speck, x, y)
        if not keep.any():
            continue
        win = window(rgba, x, y)
        for c in range(3):
            out[y, x, c] = lower_median(win[..., c][keep])
    return out


def reduce_color_noise(rgba, strength, radius):
    h, w = rgba.shape[:2]
    if radius == 0:
        radius = max(2, int(F(0.01) * F(min(w, h)) + F(0.5)))
    eps = F(0.01) * F(0.01)
    alpha = rgba[..., 3].astype(np.int64)
    prem = [((rgba[..., c].astype(np.int64) * alpha).astype(F) / F(65025)).astype(F) for c in range(3)]
    y = (LUMA[0] * prem[0] + LUMA[1] * prem[1] + LUMA[2] * prem[2]).astype(F)
    mean_i = box(y, radius)
    var_i = np.maximum(F(0), box((y * y).astype(F), radius) - mean_i * mean_i).astype(F)
    strength = F(strength)

    def filtered(c):
        p = (prem[c] - y).astype(F)
        mean_p = box(p, radius)
        mean_ip = box((y * p).astype(F), radius)
        a = ((mean_ip - mean_i * mean_p) / (var_i + eps)).astype(F)
        b = (mean_p - a * mean_i).astype(F)
        q = (box(a, radius) * y + box(b, radius)).astype(F)
        return (p + strength * (q - p)).astype(F)

    cb, cr = filtered(2), filtered(0)
    r = y + cr
    b = y + cb
    g = (y - LUMA[0] * r - LUMA[2] * b) / LUMA[1]
    out = rgba.copy()
    with np.errstate(divide="ignore", invalid="ignore"):
        scale = F(65025) / rgba[..., 3].astype(F)
        for c, v in enumerate((r, g, b)):
            out[..., c] = np.where(alpha == 0, rgba[..., c], to_byte((v * scale).astype(F)))
    return out


def encode_exact(linear):
    v = min(max(linear, 0.0), 1.0)
    return v * 12.92 if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055


def negative_values(rgba, gray):
    if gray:
        y = int_luma(rgba[..., :3])
        return np.stack([y, y, y], axis=-1)
    return rgba[..., :3].astype(np.int64)


def nearest_rank(hist, percentile):
    n = int(hist.sum())
    rank = min(max(math.ceil(float(percentile) / 100.0 * n), 1), max(1, n))
    return int(np.argmax(np.cumsum(hist) >= rank)) if n > 0 else 255


def density(base, v):
    return math.log10(base / max(float(DECODE[v]), 1.0 / 1024))


def analyze_negative(rgba, options):
    h, w = rgba.shape[:2]
    gray = options.get("film_type", 0) == 1
    x0, y0, rw, rh = options.get("region") or (0, 0, w, h)
    step = max(1, math.ceil(math.sqrt(rw * rh / 1_000_000)))
    sample = rgba[y0:y0 + rh:step, x0:x0 + rw:step]
    opaque = sample[..., 3] != 0
    values = negative_values(sample, gray)[opaque]
    clipped = (values <= 2) | (values >= 253)
    usable = ~clipped.any(axis=1)
    used = values[usable] if usable.any() else values
    hists = [np.bincount(used[:, c], minlength=256) for c in range(3)]
    if options.get("film_base_region"):
        bx, by, bw, bh = options["film_base_region"]
        region = rgba[by:by + bh, bx:bx + bw]
        bvalues = negative_values(region, gray)[region[..., 3] != 0]
        base = [float(DECODE[nearest_rank(np.bincount(bvalues[:, c], minlength=256), F(50))]) for c in range(3)]
    else:
        base = [float(DECODE[nearest_rank(hists[c], F(99.7))]) for c in range(3)]
    high = F(options.get("highlight_percentile", 99.7))
    balance = float(F(options.get("midtone_balance", 0.6)))
    rng, med = [], []
    for c in range(3):
        base[c] = min(max(base[c], float(DECODE[1])), 1.0)
        r = min(max(density(base[c], nearest_rank(hists[c], F(100) - high)), 0.05), 6.0)
        rng.append(r)
        med.append(min(max(density(base[c], nearest_rank(hists[c], F(50))) / r, 0.01), 0.99))
    target = math.exp((math.log(med[0]) + math.log(med[1]) + math.log(med[2])) / 3)
    gamma = [min(max((math.log(target) / math.log(med[c])) ** balance, 0.2), 5.0) for c in range(3)]
    n = int(opaque.sum())
    share = F(0) if n == 0 else F(1) / F(n)
    fractions = [float(F(int(clipped[:, c].sum())) * share) for c in range(3)]
    warnings = (1 if max(fractions) > float(F(0.01)) else 0) | (0 if options.get("film_base_region") else 2) \
        | (4 if min(rng) < float(F(0.3)) else 0)
    calibration = {"film_type": options.get("film_type", 0), "base": [float(F(b)) for b in base],
                   "range": [float(F(r)) for r in rng], "gamma": [float(F(g)) for g in gamma]}
    return {"calibration": calibration, "clipped": fractions, "warnings": warnings}


def invert_negative(rgba, calibration, print_options):
    out = rgba.copy()
    if calibration.get("film_type", 0) == 1:
        y = int_luma(rgba[..., :3]).astype(np.uint8)
        for c in range(3):
            out[..., c] = y
    contrast = float(F(print_options.get("contrast", 1.6)))
    curve = float(F(print_options.get("curve", 0.3)))
    gain = 2.0 ** float(F(print_options.get("exposure", 0)))
    for c in range(3):
        base, rng, gamma = (float(F(calibration[k][c])) for k in ("base", "range", "gamma"))
        table = np.zeros(256, dtype=np.uint8)
        for v in range(256):
            t = min(max(density(base, v) / rng, 0.0), 1.05)
            s = encode_exact(10.0 ** (contrast * (t ** gamma - 1)) * gain)
            s += curve * s * (1 - s) * (2 * s - 1)
            table[v] = int(math.floor(min(max(s, 0.0), 1.0) * 255 + 0.5))
        out[..., c] = table[out[..., c]]
    return out


def develop_negative(rgba, options):
    calibration = options.get("calibration") or analyze_negative(rgba, options)["calibration"]
    out = invert_negative(rgba, calibration, options.get("print", {}))
    h, w = rgba.shape[:2]
    dust = options.get("dust_size")
    if dust is None:
        dust = max(2, int(F(0.005) * F(min(w, h)) + F(0.5)))
    if dust > 0:
        out = remove_dust(out, dust, 0.1)
    strength = options.get("color_noise", 1.0)
    if calibration.get("film_type", 0) == 0 and strength > 0:
        out = reduce_color_noise(out, strength, 0)
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
    if op == "curves":
        return curves(rgba, job["points"], job["channels"])
    if op == "white_balance_neutral":
        return white_balance_neutral(rgba, job["neutral"])
    if op == "auto_white_balance":
        return auto_white_balance(rgba, job["strength"])
    if op == "remove_dust":
        return remove_dust(rgba, job["size"], job["threshold"])
    if op == "reduce_color_noise":
        return reduce_color_noise(rgba, job["strength"], job["radius"])
    if op == "analyze_negative":
        job["result"] = analyze_negative(rgba, job["options"])
        return rgba
    if op == "invert_negative":
        return invert_negative(rgba, job["calibration"], job["print"])
    if op == "develop_negative":
        return develop_negative(rgba, job["options"])
    raise ValueError(op)


def main():
    directory = sys.argv[1]
    with open(os.path.join(directory, "jobs.json")) as f:
        jobs = json.load(f)
    for job in jobs:
        with Image.open(os.path.join(directory, job["input"])) as im:
            rgba = np.asarray(im.convert("RGBA"), dtype=np.uint8).copy()
        result = run(job, rgba)
        if "result" in job:
            with open(os.path.join(directory, job["name"] + ".json"), "w") as f:
                json.dump(job["result"], f)
        h, w = result.shape[:2]
        with open(os.path.join(directory, job["name"] + ".rgba"), "wb") as f:
            f.write(f"{w} {h}\n".encode("ascii"))
            f.write(np.ascontiguousarray(result, dtype=np.uint8).tobytes())


if __name__ == "__main__":
    main()
