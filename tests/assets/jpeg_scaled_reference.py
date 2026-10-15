#!/usr/bin/env python3
"""Writes jpeg_scaled_reference.json: SHA-256 of Pillow's RGBA decode of every corpus JPEG at 1/2, 1/4 and 1/8 scale.

Pillow's draft mode hands the scale to libjpeg-turbo (scale_num/scale_denom) with its default islow IDCT and fancy
upsampling, so this is libjpeg-turbo's reduced decode. draft() picks the scale from a requested size; the reference
forces each scale with the same tile/size bookkeeping as JpegImagePlugin.draft in Pillow 10.2.0.

With an argument, the raw RGBA of each decode is also written there as <file>.<scale>.rgba for measuring differences.
Run with:  python3 -I tests/assets/jpeg_scaled_reference.py [<dump dir>]
"""
import hashlib
import json
import os
import sys

import PIL
from PIL import Image, ImageFile, features

ImageFile.LOAD_TRUNCATED_IMAGES = True

HERE = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(HERE, "corpus", "jpeg")
SCALES = (2, 4, 8)


def decode_scaled(path, scale):
    """Port of JpegImagePlugin.draft with the scale fixed instead of derived from a size."""
    with Image.open(path) as im:
        d, e, o, a = im.tile[0]
        e = (e[0], e[1], (e[2] - e[0] + scale - 1) // scale + e[0], (e[3] - e[1] + scale - 1) // scale + e[1])
        im._size = ((im.size[0] + scale - 1) // scale, (im.size[1] + scale - 1) // scale)
        im.tile = [(d, e, o, a)]
        im.decoderconfig = (scale, 0)
        return im.size, im.convert("RGBA").tobytes()


def main():
    dump = sys.argv[1] if len(sys.argv) > 1 else None
    result = {}
    for name in sorted(os.listdir(CORPUS)):
        if not name.lower().endswith(".jpg"):
            continue
        entry = {}
        for scale in SCALES:
            (width, height), rgba = decode_scaled(os.path.join(CORPUS, name), scale)
            entry[str(scale)] = {"width": width, "height": height, "sha256": hashlib.sha256(rgba).hexdigest()}
            if dump:
                os.makedirs(dump, exist_ok=True)
                with open(os.path.join(dump, f"{name}.{scale}.rgba"), "wb") as f:
                    f.write(rgba)
        result["jpeg/" + name] = entry
    doc = {
        "generator": f"Pillow {PIL.__version__}, libjpeg-turbo {features.version('libjpeg_turbo')}",
        "files": result,
    }
    with open(os.path.join(HERE, "jpeg_scaled_reference.json"), "w") as f:
        json.dump(doc, f, indent=1)
        f.write("\n")


if __name__ == "__main__":
    main()
