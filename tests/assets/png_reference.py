#!/usr/bin/env python3
"""Prints {file: sha256-of-RGBA | "error"} for every PNG in a directory (used for PngSuite).

Same decoding rules as make_reference.py: Pillow for <= 8 bits per channel, ImageMagick with
round(v/257) for 16-bit files. Run with:  python3 -I tests/assets/png_reference.py <dir>
"""
import hashlib
import json
import os
import struct
import subprocess
import sys

import numpy as np
from PIL import Image


def decode(path):
    with open(path, "rb") as f:
        data = f.read()
    depth = data[24]
    im = Image.open(path)
    im.load()
    if depth == 16:
        raw = subprocess.run(["convert", path, "-depth", "16", "-endian", "MSB", "RGBA:-"],
                             check=True, capture_output=True).stdout
        v = np.frombuffer(raw, dtype=">u2").astype(np.uint32)
        return ((v + 128) // 257).astype(np.uint8).tobytes()
    rgba = im.convert("RGBA")
    ctype = data[25]
    if ctype == 0 and depth < 8 and "transparency" in im.info:
        # Pillow compares the tRNS value against the *scaled* 8-bit sample, so low-bit-depth
        # grayscale transparency is lost. The PNG spec (and libpng) compare raw samples.
        key = im.info["transparency"] * 255 // ((1 << depth) - 1)
        a = np.array(rgba)
        a[np.array(im) == key, 3] = 0
        rgba = Image.fromarray(a, "RGBA")
    return rgba.tobytes()


def main():
    folder = sys.argv[1]
    result = {}
    for name in sorted(os.listdir(folder)):
        if not name.endswith(".png"):
            continue
        try:
            result[name] = hashlib.sha256(decode(os.path.join(folder, name))).hexdigest()
        except Exception:  # noqa: BLE001 - any failure means "rejected"
            result[name] = "error"
    json.dump(result, sys.stdout, indent=1)


if __name__ == "__main__":
    main()
