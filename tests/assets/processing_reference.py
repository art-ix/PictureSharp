#!/usr/bin/env python3
"""Runs Pillow image operations listed in <dir>/jobs.json and writes each result to <dir>/<name>.rgba
as tightly packed 8-bit RGBA, preceded by a line "<width> <height>\\n".

Each job: {"name", "input", "op", ...}; inputs are PNG files in <dir>.
  op "resize":    "width", "height", "filter" (nearest|bilinear|bicubic|lanczos), "mode" (stretch|fit|fill|pad),
                  optional "centering" [x, y] for fill/pad
  op "composite": "overlay" (PNG), "x", "y"  -> Image.alpha_composite with dest=(x, y)
  op "background": "color" [r, g, b, a]     -> Image.alpha_composite(solid color, input)
  op "grayscale": RGBA -> LA -> RGBA
  op "enhance":   "kind" (brightness|contrast|color), "factor" -> ImageEnhance.<Kind>(im).enhance(factor)
  op "filter":    "kind" (box|gaussian|unsharp|sharpen), "radius", "percent", "threshold"
                  -> run on the premultiplied image: im.convert("RGBa").filter(...).convert("RGBA")
  op "rotate":    "angle" (counter-clockwise), "filter" (nearest|bilinear|bicubic), "expand", optional "fillcolor"
  op "sepia":     the 0.393/0.769/0.189 matrix in thousandths, rounded half up (PictureSharp's definition)
  op "exif_transpose": ImageOps.exif_transpose on the input as decoded by Pillow (any format)

Run with:  python3 -I tests/assets/processing_reference.py <dir>
"""
import json
import os
import sys

from PIL import Image, ImageEnhance, ImageFilter, ImageOps

FILTERS = {
    "nearest": Image.Resampling.NEAREST,
    "bilinear": Image.Resampling.BILINEAR,
    "bicubic": Image.Resampling.BICUBIC,
    "lanczos": Image.Resampling.LANCZOS,
    "box": Image.Resampling.BOX,
    "hamming": Image.Resampling.HAMMING,
}

ENHANCERS = {"brightness": ImageEnhance.Brightness, "contrast": ImageEnhance.Contrast, "color": ImageEnhance.Color}


def make_filter(job):
    kind = job["kind"]
    if kind == "box":
        return ImageFilter.BoxBlur(job["radius"])
    if kind == "gaussian":
        return ImageFilter.GaussianBlur(job["radius"])
    if kind == "unsharp":
        return ImageFilter.UnsharpMask(job["radius"], job["percent"], job["threshold"])
    if kind == "sharpen":
        return ImageFilter.SHARPEN
    raise ValueError(kind)


def sepia(im):
    data = bytearray(im.tobytes())
    for i in range(0, len(data), 4):
        r, g, b = data[i], data[i + 1], data[i + 2]
        data[i] = min(255, (393 * r + 769 * g + 189 * b + 500) // 1000)
        data[i + 1] = min(255, (349 * r + 686 * g + 168 * b + 500) // 1000)
        data[i + 2] = min(255, (272 * r + 534 * g + 131 * b + 500) // 1000)
    return Image.frombytes("RGBA", im.size, bytes(data))


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
        centering = tuple(job.get("centering", (0.5, 0.5)))
        if mode == "stretch":
            return im.resize(size, method)
        if mode == "fit":
            return ImageOps.contain(im, size, method)
        if mode == "fill":
            return ImageOps.fit(im, size, method, centering=centering)
        if mode == "pad":
            # color=None leaves borders uninitialized
            return ImageOps.pad(im, size, method, color=(0, 0, 0, 0), centering=centering)
        raise ValueError(mode)
    if op == "composite":
        im.alpha_composite(load(folder, job["overlay"]), dest=(job["x"], job["y"]))
        return im
    if op == "background":
        return Image.alpha_composite(Image.new("RGBA", im.size, tuple(job["color"])), im)
    if op == "grayscale":
        return im.convert("LA").convert("RGBA")
    if op == "enhance":
        return ENHANCERS[job["kind"]](im).enhance(job["factor"])
    if op == "filter":
        return im.convert("RGBa").filter(make_filter(job)).convert("RGBA")
    if op == "rotate":
        fill = job.get("fillcolor")
        return im.rotate(job["angle"], FILTERS[job["filter"]], expand=job["expand"],
                         fillcolor=tuple(fill) if fill else None)
    if op == "sepia":
        return sepia(im)
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
