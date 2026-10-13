#!/usr/bin/env python3
"""Runs Pillow image operations listed in <dir>/jobs.json and writes each result to <dir>/<name>.rgba
as tightly packed 8-bit RGBA, preceded by a line "<width> <height>\\n".

Each job: {"name", "input", "op", ...}; inputs are PNG files in <dir>.
  op "resize":    "width", "height", "filter" (nearest|bilinear|bicubic|lanczos), "mode" (stretch|fit|fill|pad)
  op "composite": "overlay" (PNG), "x", "y"  -> Image.alpha_composite with dest=(x, y)
  op "grayscale": RGBA -> LA -> RGBA
  op "exif_transpose": ImageOps.exif_transpose on the input as decoded by Pillow (any format)

Run with:  python3 -I tests/assets/processing_reference.py <dir>
"""
import json
import os
import sys

from PIL import Image, ImageOps

FILTERS = {
    "nearest": Image.Resampling.NEAREST,
    "bilinear": Image.Resampling.BILINEAR,
    "bicubic": Image.Resampling.BICUBIC,
    "lanczos": Image.Resampling.LANCZOS,
}


def load(folder, name):
    with Image.open(os.path.join(folder, name)) as im:
        return im.convert("RGBA")


def run(folder, job):
    op = job["op"]
    if op == "exif_transpose":
        with Image.open(os.path.join(folder, job["input"])) as im:
            return ImageOps.exif_transpose(im).convert("RGBA")
    im = load(folder, job["input"])
    if op == "resize":
        size = (job["width"], job["height"])
        method = FILTERS[job["filter"]]
        mode = job["mode"]
        if mode == "stretch":
            return im.resize(size, method)
        if mode == "fit":
            return ImageOps.contain(im, size, method)
        if mode == "fill":
            return ImageOps.fit(im, size, method)
        if mode == "pad":
            return ImageOps.pad(im, size, method, color=(0, 0, 0, 0))  # color=None leaves borders uninitialized
        raise ValueError(mode)
    if op == "composite":
        im.alpha_composite(load(folder, job["overlay"]), dest=(job["x"], job["y"]))
        return im
    if op == "grayscale":
        return im.convert("LA").convert("RGBA")
    raise ValueError(op)


def main():
    folder = sys.argv[1]
    with open(os.path.join(folder, "jobs.json"), encoding="utf-8") as f:
        jobs = json.load(f)
    for job in jobs:
        out = run(folder, job)
        with open(os.path.join(folder, job["name"] + ".rgba"), "wb") as f:
            f.write(f"{out.width} {out.height}\n".encode("ascii"))
            f.write(out.tobytes())
    print(f"{len(jobs)} jobs")


if __name__ == "__main__":
    main()
