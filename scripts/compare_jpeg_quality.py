#!/usr/bin/env python3
"""Compares PictureSharp JPEG quality with libjpeg-turbo (via Pillow) at equal file size.

usage: python3 -I scripts/compare_jpeg_quality.py <image> ... [--out DIR] [--tools-dir DIR]
       [--qualities 10 15 ... 95] [--max-side N] [--jobs N]

Both encoders write baseline 4:2:0 JPEG with standard Huffman tables at each quality.
References are converted to untagged 8-bit sRGB (ICC applied, EXIF orientation applied).
Both outputs are decoded by the same decoder (Pillow), so only the encoders differ.
Metrics: SSIMULACRA2 (higher is better) and Butteraugli max and 3-norm (lower is better),
from the libjxl tools `ssimulacra2` and `butteraugli_main` (PATH or --tools-dir).

Quality settings do not compare encoders, file sizes do. For each image the script builds
rate-distortion curves (bits per pixel vs score) and reports:
- the score difference at PictureSharp's file sizes, interpolated on the libjpeg-turbo curve
  in log(bpp) within the common size range;
- the BD-rate: mean byte difference at equal score over the common score range
  (negative = PictureSharp files are smaller for the same quality).
Results (default reference/jpeg-quality/, Git-ignored): results.csv (raw points) and summary.json.
Decoded rasters are removed; the encoded JPEGs stay for inspection.

The JPEG encoder is a libjpeg-turbo port: with the default settings the scan data is byte-identical,
so every difference is 0. The script is a guard for encoder changes that leave that path
(quantization, trellis, adaptive tables) and must show a gain at equal size, not at equal quality.
"""
import argparse
import concurrent.futures
import csv
import json
import math
import os
import pathlib
import shutil
import subprocess
import sys

import io

from PIL import Image, ImageCms, ImageOps, features

PROJECT_ROOT = pathlib.Path(__file__).resolve().parent.parent
CLI_PROJECT = PROJECT_ROOT / "samples" / "PictureSharp.Cli"
CLI_DLL = CLI_PROJECT / "bin" / "Release" / "net10.0" / "picturesharp-cli.dll"
DEFAULT_QUALITIES = list(range(10, 96, 5))
METRICS = {"ssimulacra2": +1, "butteraugli_max": -1, "butteraugli_3norm": -1}


def find_tool(name, tools_dir):
    if tools_dir:
        candidate = pathlib.Path(tools_dir) / name
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    found = shutil.which(name)
    if not found:
        sys.exit(f"{name} not found: put libjxl tools on PATH or pass --tools-dir")
    return found


def run(command):
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"{' '.join(map(str, command))} failed:\n{result.stderr or result.stdout}")
    return result.stdout


def prepare_reference(source, directory, max_side):
    image = ImageOps.exif_transpose(Image.open(source))
    icc = image.info.get("icc_profile")
    image = image.convert("RGB")
    if icc:
        # Both encoders get untagged sRGB, so the metrics compare pixels in one colour space.
        image = ImageCms.profileToProfile(image, ImageCms.ImageCmsProfile(io.BytesIO(icc)),
                                          ImageCms.createProfile("sRGB"), outputMode="RGB")
    image.info.pop("icc_profile", None)
    if max_side and max(image.size) > max_side:
        image.thumbnail((max_side, max_side), Image.Resampling.LANCZOS)
    path = directory / "reference.png"
    image.save(path)
    return path, image.size


def encode(encoder, reference, quality, directory):
    output = directory / f"{encoder}-q{quality:03}.jpg"
    if encoder == "picturesharp":
        run(["dotnet", str(CLI_DLL), "convert", str(reference), str(output), "-q", str(quality)])
    else:
        with Image.open(reference) as image:
            image.save(output, quality=quality, subsampling="4:2:0", optimize=False, progressive=False)
    return output


def measure(job, tools):
    encoder, reference, quality, directory, pixels = job
    jpeg = encode(encoder, reference, quality, directory)
    decoded = jpeg.with_suffix(".png")
    with Image.open(jpeg) as image:
        image.convert("RGB").save(decoded)
    try:
        ssim = float(run([tools["ssimulacra2"], str(reference), str(decoded)]).split()[-1])
        lines = run([tools["butteraugli_main"], str(reference), str(decoded)]).split("\n")
        butter_max = float(lines[0].strip())
        butter_norm = float(next(line for line in lines if line.startswith("3-norm:")).split(":")[1])
    finally:
        decoded.unlink(missing_ok=True)
    size = jpeg.stat().st_size
    return {"encoder": encoder, "quality": quality, "bytes": size, "bpp": size * 8 / pixels,
            "ssimulacra2": ssim, "butteraugli_max": butter_max, "butteraugli_3norm": butter_norm}


def interpolate(points, x):
    """Piecewise linear interpolation over points sorted by x; None outside the range."""
    for (x0, y0), (x1, y1) in zip(points, points[1:]):
        if x0 <= x <= x1:
            return y0 if x1 == x0 else y0 + (y1 - y0) * (x - x0) / (x1 - x0)
    return None


def monotonic(points, sign):
    """Keeps the points where the score improves with size (drops non-monotonic outliers)."""
    kept = []
    for x, y in sorted(points):
        if not kept or sign * (y - kept[-1][1]) > 0:
            kept.append((x, y))
    return kept


def compare(rows):
    by_encoder = {e: [r for r in rows if r["encoder"] == e] for e in ("picturesharp", "libjpeg-turbo")}
    summary = {}
    for metric, sign in METRICS.items():
        curves = {e: monotonic([(math.log(r["bpp"]), r[metric]) for r in rs], sign) for e, rs in by_encoder.items()}
        ours, theirs = curves["picturesharp"], curves["libjpeg-turbo"]

        deltas = []
        for x, y in ours:
            other = interpolate(theirs, x)
            if other is not None:
                deltas.append(y - other)

        # BD-rate: log(bpp) as a function of score, averaged over the common score range.
        inverse = {e: sorted((y, x) for x, y in c) for e, c in curves.items()}
        low = max(min(y for y, _ in inv) for inv in inverse.values())
        high = min(max(y for y, _ in inv) for inv in inverse.values())
        bd_rate = None
        if high > low:
            steps = 200
            diffs = []
            for i in range(steps + 1):
                score = low + (high - low) * i / steps
                a = interpolate(inverse["picturesharp"], score)
                b = interpolate(inverse["libjpeg-turbo"], score)
                if a is not None and b is not None:
                    diffs.append(a - b)
            if diffs:
                bd_rate = (math.exp(sum(diffs) / len(diffs)) - 1) * 100

        summary[metric] = {
            "mean_delta_at_equal_size": sum(deltas) / len(deltas) if deltas else None,
            "points_compared": len(deltas),
            "bd_rate_percent": bd_rate,
            "common_score_range": [low, high] if high > low else None,
        }
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("images", nargs="+", type=pathlib.Path)
    parser.add_argument("--out", type=pathlib.Path, default=PROJECT_ROOT / "reference" / "jpeg-quality")
    parser.add_argument("--tools-dir", help="directory with ssimulacra2 and butteraugli_main")
    parser.add_argument("--qualities", type=int, nargs="+", default=DEFAULT_QUALITIES)
    parser.add_argument("--max-side", type=int, default=0, help="downscale references so the longer side is at most N")
    parser.add_argument("--jobs", type=int, default=os.cpu_count() or 1)
    parser.add_argument("--no-build", action="store_true", help="use the existing Release CLI build")
    args = parser.parse_args()

    if not features.check_feature("libjpeg_turbo"):
        sys.exit("Pillow is not built with libjpeg-turbo")
    tools = {name: find_tool(name, args.tools_dir) for name in ("ssimulacra2", "butteraugli_main")}
    if not args.no_build:
        run(["dotnet", "build", str(CLI_PROJECT), "-c", "Release", "--nologo", "-v", "q"])
    if not CLI_DLL.is_file():
        sys.exit(f"{CLI_DLL} missing: build samples/PictureSharp.Cli in Release")

    args.out.mkdir(parents=True, exist_ok=True)
    all_rows, summaries = [], {}
    for source in args.images:
        directory = args.out / source.stem
        directory.mkdir(exist_ok=True)
        reference, (width, height) = prepare_reference(source, directory, args.max_side)
        jobs = [(encoder, reference, q, directory, width * height)
                for encoder in ("picturesharp", "libjpeg-turbo") for q in args.qualities]
        with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
            rows = list(pool.map(lambda job: measure(job, tools), jobs))
        for row in rows:
            row["image"] = source.name
        all_rows += rows
        summaries[source.name] = {"size": [width, height], **compare(rows)}
        print_image(source.name, width, height, rows, summaries[source.name])

    with open(args.out / "results.csv", "w", newline="") as file:
        writer = csv.DictWriter(file, ["image", "encoder", "quality", "bytes", "bpp", *METRICS])
        writer.writeheader()
        writer.writerows(sorted(all_rows, key=lambda r: (r["image"], r["encoder"], r["quality"])))
    overall = {}
    for metric in METRICS:
        values = [s[metric]["bd_rate_percent"] for s in summaries.values() if s[metric]["bd_rate_percent"] is not None]
        deltas = [s[metric]["mean_delta_at_equal_size"] for s in summaries.values()
                  if s[metric]["mean_delta_at_equal_size"] is not None]
        overall[metric] = {"mean_bd_rate_percent": sum(values) / len(values) if values else None,
                           "mean_delta_at_equal_size": sum(deltas) / len(deltas) if deltas else None}
    tool_versions = {"pillow": Image.__version__, "libjpeg": features.version("jpg")}
    with open(args.out / "summary.json", "w") as file:
        json.dump({"qualities": args.qualities, "max_side": args.max_side, "tools": tools,
                   "versions": tool_versions, "images": summaries, "overall": overall}, file, indent=2)

    print("\nOverall (mean over images):")
    for metric, values in overall.items():
        print(f"  {metric:18} BD-rate {fmt(values['mean_bd_rate_percent'], '+.1f')}%   "
              f"Δ at equal size {fmt(values['mean_delta_at_equal_size'], '+.3f')}")
    print(f"\nResults in {args.out}")


def fmt(value, spec):
    return "n/a" if value is None else format(value, spec)


def print_image(name, width, height, rows, summary):
    print(f"\n{name} ({width}x{height})")
    print(f"  {'q':>3} | {'PictureSharp bpp':>16} {'ssim2':>7} {'BA max':>7} {'BA 3n':>6} | "
          f"{'libjpeg-turbo bpp':>17} {'ssim2':>7} {'BA max':>7} {'BA 3n':>6}")
    by_key = {(r["encoder"], r["quality"]): r for r in rows}
    for q in sorted({r["quality"] for r in rows}):
        a, b = by_key[("picturesharp", q)], by_key[("libjpeg-turbo", q)]
        print(f"  {q:>3} | {a['bpp']:>16.3f} {a['ssimulacra2']:>7.2f} {a['butteraugli_max']:>7.3f} "
              f"{a['butteraugli_3norm']:>6.3f} | {b['bpp']:>17.3f} {b['ssimulacra2']:>7.2f} "
              f"{b['butteraugli_max']:>7.3f} {b['butteraugli_3norm']:>6.3f}")
    for metric in METRICS:
        s = summary[metric]
        print(f"  {metric:18} BD-rate {fmt(s['bd_rate_percent'], '+.1f')}%   "
              f"Δ at equal size {fmt(s['mean_delta_at_equal_size'], '+.3f')} ({s['points_compared']} points)")


if __name__ == "__main__":
    main()
