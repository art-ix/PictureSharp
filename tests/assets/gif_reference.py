#!/usr/bin/env python3
"""Pillow references for the GIF encoder's color quantization: tests/assets/gif_quantize_reference.json.

For each opaque source and color count it stores the palette and SHA-256 hashes of the palette indices and of
the resulting RGBA pixels of
* Image.quantize(colors, method=MEDIANCUT)            (median cut, the default for RGB images)
* Image.quantize(colors, method=FASTOCTREE)           (fast octree)
* Image.quantize(palette=<median cut>, dither=FLOYDSTEINBERG)   (error diffusion against a fixed palette)

Run with:  python3 -I tests/assets/gif_reference.py
"""
import hashlib
import json
import os

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "corpus", "src")
SOURCES = ["photo", "photo_odd", "graphic", "wide", "thin", "odd_alpha", "many_colors"]
COLORS = [2, 3, 16, 64, 200, 255, 256]


def many_colors():
    """More than 65536 distinct colors, so median cut rescales its pixel hash (same formula in QuantizationTests)."""
    y, x = np.mgrid[0:300, 0:512].astype(np.int64)
    rgb = np.stack([x & 255, y & 255, ((x >> 8) * 128 + x * y) & 255], axis=-1).astype(np.uint8)
    return Image.fromarray(rgb, "RGB")


def load(name):
    if name == "many_colors":
        return many_colors()
    return Image.open(os.path.join(SRC, name + ".png")).convert("RGB")


def entry(q):
    palette = q.getpalette("RGB")
    return {"palette": list(palette), "sha256": hashlib.sha256(q.tobytes()).hexdigest(),
            "rgbaSha256": hashlib.sha256(q.convert("RGBA").tobytes()).hexdigest()}


def main():
    refs = {}
    for name in SOURCES:
        im = load(name)
        for colors in COLORS:
            median = im.quantize(colors, method=Image.Quantize.MEDIANCUT)
            refs[f"{name}/mediancut/{colors}"] = entry(median)
            refs[f"{name}/octree/{colors}"] = entry(im.quantize(colors, method=Image.Quantize.FASTOCTREE))
            refs[f"{name}/dither/{colors}"] = entry(im.quantize(palette=median, dither=Image.Dither.FLOYDSTEINBERG))
    with open(os.path.join(ROOT, "gif_quantize_reference.json"), "w") as f:
        json.dump(refs, f, indent=1)
    print(f"gif_quantize_reference.json: {len(refs)} entries")


if __name__ == "__main__":
    main()
