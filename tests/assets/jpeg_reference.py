#!/usr/bin/env python3
"""Prints {relative path: sha256 | "error"} of Pillow's RGBA decode for every JPEG below a directory
(used for local sample photos and for files built by the tests).

With a second argument, the raw RGBA of each decode is also written there as <relative path>.rgba so that
differences can be measured. --scale N decodes at 1/N (2, 4 or 8) like jpeg_scaled_reference.py does.
Run with:  python3 -I tests/assets/jpeg_reference.py [--scale N] <dir> [<dump dir>]
"""
import hashlib
import json
import os
import sys

from PIL import Image, ImageFile

# Truncated files decode like libjpeg does (missing data comes out as uniform gray) instead of failing.
ImageFile.LOAD_TRUNCATED_IMAGES = True


def force_scale(im, scale):
    """JpegImagePlugin.draft of Pillow 10.2.0 with the scale fixed instead of derived from a size."""
    d, e, o, a = im.tile[0]
    e = (e[0], e[1], (e[2] - e[0] + scale - 1) // scale + e[0], (e[3] - e[1] + scale - 1) // scale + e[1])
    im._size = ((im.size[0] + scale - 1) // scale, (im.size[1] + scale - 1) // scale)
    im.tile = [(d, e, o, a)]
    im.decoderconfig = (scale, 0)


def main():
    args = sys.argv[1:]
    scale = 1
    if args[:1] == ["--scale"]:
        scale = int(args[1])
        args = args[2:]
    folder = args[0]
    dump = args[1] if len(args) > 1 else None
    result = {}
    for dirpath, _, files in os.walk(folder):
        for name in sorted(files):
            if not name.lower().endswith((".jpg", ".jpeg")):
                continue
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, folder).replace(os.sep, "/")
            try:
                with Image.open(path) as im:
                    if scale > 1:
                        force_scale(im, scale)
                    rgba = im.convert("RGBA").tobytes()
            except Exception:  # noqa: BLE001 - any failure means "rejected"
                result[rel] = "error"
                continue
            result[rel] = hashlib.sha256(rgba).hexdigest()
            if dump:
                out = os.path.join(dump, rel + ".rgba")
                os.makedirs(os.path.dirname(out), exist_ok=True)
                with open(out, "wb") as f:
                    f.write(rgba)
    json.dump(dict(sorted(result.items())), sys.stdout, indent=1)


if __name__ == "__main__":
    main()
