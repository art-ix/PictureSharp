#!/usr/bin/env python3
"""Prints {relative path: {"pillow": sha256 | "error", "imagemagick": sha256 | "error"}} for every
BMP below a directory (used for files written by our encoder and for BMP Suite).

Hashes are taken over tightly packed 8-bit RGBA. Run with:  python3 -I tests/assets/bmp_reference.py <dir>
"""
import hashlib
import json
import os
import subprocess
import sys

from PIL import Image


def pillow(path):
    with Image.open(path) as im:
        return im.convert("RGBA").tobytes()


def imagemagick(path):
    return subprocess.run(["convert", path + "[0]", "-depth", "8", "RGBA:-"], check=True,
                          capture_output=True, timeout=60).stdout


def main():
    folder = sys.argv[1]
    result = {}
    for dirpath, _, files in os.walk(folder):
        for name in sorted(files):
            if not name.lower().endswith(".bmp"):
                continue
            path = os.path.join(dirpath, name)
            entry = {}
            for key, decode in (("pillow", pillow), ("imagemagick", imagemagick)):
                try:
                    entry[key] = hashlib.sha256(decode(path)).hexdigest()
                except Exception:  # noqa: BLE001 - any failure means "rejected"
                    entry[key] = "error"
            result[os.path.relpath(path, folder).replace(os.sep, "/")] = entry
    json.dump(dict(sorted(result.items())), sys.stdout, indent=1)


if __name__ == "__main__":
    main()
