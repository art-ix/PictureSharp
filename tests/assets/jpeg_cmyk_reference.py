#!/usr/bin/env python3
"""Writes jpeg_cmyk_reference.json: SHA-256 of the RGBA of every four-component corpus JPEG read as plain CMYK.

Pillow reads every four-component JPEG with Adobe polarity (rawmode "CMYK;I", JpegImagePlugin in Pillow 10.2.0), so
the values libjpeg-turbo decodes (after its YCCK to CMYK conversion) are 255 minus Pillow's. Read as plain ink amounts
(JpegCmykStorage.Plain, as TIFF stores CMYK), those values are converted with Pillow's own cmyk2rgb.

Run with:  python3 -I tests/assets/jpeg_cmyk_reference.py
"""
import hashlib
import json
import os

import numpy as np
import PIL
from PIL import Image, features

HERE = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(HERE, "corpus", "jpeg")


def main():
    result = {}
    for name in sorted(os.listdir(CORPUS)):
        if not name.lower().endswith(".jpg"):
            continue
        with Image.open(os.path.join(CORPUS, name)) as im:
            if im.mode != "CMYK":
                continue
            stored = 255 - np.asarray(im)
        rgba = Image.fromarray(np.ascontiguousarray(stored), "CMYK").convert("RGBA")
        result["jpeg/" + name] = {"width": rgba.width, "height": rgba.height, "sha256": hashlib.sha256(rgba.tobytes()).hexdigest()}
    doc = {
        "generator": f"Pillow {PIL.__version__}, libjpeg-turbo {features.version('libjpeg_turbo')}",
        "files": result,
    }
    with open(os.path.join(HERE, "jpeg_cmyk_reference.json"), "w") as f:
        json.dump(doc, f, indent=1)
        f.write("\n")


if __name__ == "__main__":
    main()
