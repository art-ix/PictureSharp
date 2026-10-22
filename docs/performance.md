# Performance

Measurements use an Intel i5-12500H (Linux x64, .NET 10) unless a section says otherwise, and an Orange Pi 5 (RK3588)
for ARM64. Each section records the release named in its title. Raw data lives in [measurements/](measurements/).

## How to compare

Compare the same encoded bytes and options with the previous release on the same CPU, runtime and GC configuration.
Record the median, spread and allocated bytes of the complete operation; microbenchmarks explain a change but do not
prove an end-to-end gain. Measure serially, repeat any apparent regression, and check the scalar and SSE2 fallbacks
(`DOTNET_EnableHWIntrinsic=0`, `DOTNET_EnableAVX2=0`) separately from AVX2. ARM64 needs real hardware. Keep private
photographs and raw benchmark artifacts out of Git.

## Running the benchmarks

Run from the repository root. BenchmarkDotNet writes reports to `BenchmarkDotNet.Artifacts/` and needs a unique
benchmark project below the working directory, so run it from a checkout without nested worktrees (`.claude/worktrees`).
For production-style tiered-JIT numbers, omit `DOTNET_TieredCompilation=0` and use longer warmups.

```bash
# Predictors: one invocation per iteration, so reconstructed pixels are never reused as residuals.
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*LosslessPredictorBenchmarks*' --launchCount 1 --warmupCount 5 \
  --iterationCount 12 --invocationCount 1 --unrollFactor 1

# Lossless WebP decoding of the seven corpus inputs, plus optional local files.
PICTURESHARP_BENCHMARK_WEBP_FILES='/absolute/photo-2.8mp.webp:/absolute/photo-12mp.webp' \
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*LosslessDecodeBenchmarks*' --launchCount 1 --warmupCount 3 \
  --iterationCount 8 --iterationTime 100

# Lossless WebP encoding at quality 75.
PICTURESHARP_BENCHMARK_ENCODE_FILES='/absolute/photo.png' \
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*LosslessEncodeBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 8

# PNG, JPEG and lossy WebP encode/decode and Lanczos halving.
PICTURESHARP_BENCHMARK_CODEC_FILES='/absolute/photo.png' \
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*CodecBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 8

# Format matrix and animations (about 50 minutes on the i5-12500H).
PICTURESHARP_BENCHMARK_MATRIX_FILES='/absolute/kodim23.png:/absolute/photo-12mp.png' \
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*MatrixBenchmarks*' '*AnimationBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 9

# PNG deflate levels 1, 6 and 9 on one thread and on all of them, on large images built from the corpus.
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*PngCompressionBenchmarks*' --launchCount 1 --warmupCount 2 --iterationCount 6

# JPEG decoding of fixed files and encoding of fixed sources, on one thread and by default.
PICTURESHARP_BENCHMARK_JPEG_FILES='/absolute/photo.jpg:/absolute/progressive.jpg' \
PICTURESHARP_BENCHMARK_JPEG_SOURCES='/absolute/photo.png' \
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*JpegDecodeBenchmarks*' '*JpegEncodeBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 9

# Lossy WebP encode (q75, m4) and decode from memory, one thread and default, measured like the native libwebp
# reference program (median of 21 calls, 7 above 4 MP); PICTURESHARP_WEBP_PROBE_OUT=<dir> also writes the bytes.
taskset -c 0-7 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- webp-probe /tmp/webp.json /absolute/kodim23.png

# 80-case regression matrix without BenchmarkDotNet (seven samples of at least 120 ms per case).
taskset -c 0-7 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- matrix-probe /tmp/picturesharp-matrix.json
```

`*_FILES` variables take paths separated by the platform's path separator (`:` on Linux/macOS, `;` on Windows). Reuse
the same encoded files for before/after comparisons instead of re-encoding them for each version.

## Measurements

Newest first:

| Section | Covers |
| --- | --- |
| [Film negatives and cleanup (0.7.11)](#film-negatives-and-cleanup-0711) | Curves, neutral and automatic white balance, dust and color-noise removal on 12 MP; developing negatives |
| [Photo adjustments (0.7.7)](#photo-adjustments-077) | Exposure, white balance, vibrance, vignette, shadows/highlights, clarity and denoise on 12 MP |
| [WebP lossy and pipeline optimization (0.7.5)](#webp-lossy-and-pipeline-optimization-075) | Lossy WebP on one thread and by default against 0.7.4 and libwebp; the load → orient → sRGB → resize → WebP path |
| [JPEG optimization (0.7.4)](#jpeg-optimization-074) | JPEG decoding and encoding on one thread and by default, against 0.7.3 and Pillow |
| [Own deflate versus System.IO.Compression (0.7.2)](#own-deflate-versus-systemiocompression-072) | Why PNG keeps its own deflate/inflate instead of `ZLibStream` |
| [Integration measurements (0.7.0)](#integration-measurements-070) | Complete user paths on 12 MP inputs: time, allocations, peak memory, cancellation latency |
| [Editing and saving HDR photos (0.6.9)](#editing-and-saving-hdr-photos-069) | Resizing, cropping and saving JPEG photos with a gain map |
| [SIMD for HDR and graphics (0.6.7)](#simd-for-hdr-and-graphics-067) | `ToHdr`, `ToneMap`, blend modes, masks and trimming on x64 and ARM64 |
| [Graphics operations (0.6.3)](#graphics-operations-063) | Levels, histograms, trimming, masks and blend modes |
| [Color management and linear light (0.6.2)](#color-management-and-linear-light-062) | ICC conversion to sRGB and linear-light resizing |
| [Metadata without image recompression (0.6.1 implementation)](#metadata-without-image-recompression-061-implementation) | `ReadMetadata` and `EditMetadata` |
| [Sequential animation memory (0.6.0)](#sequential-animation-memory-060) | Frame readers and writers with 10–1000 frames |
| [Layered architecture refactoring (0.5.5)](#layered-architecture-refactoring-055) | Regression check of the layered refactoring |
| [JPEG thumbnails in 0.5.4](#jpeg-thumbnails-in-054) | Reduced JPEG decoding with `TargetSize` |
| [Limits and cancellation in 0.5.3](#limits-and-cancellation-in-053) | Cost of the limits and cancellation checks |
| [ICO implementation for 0.5.1](#ico-implementation-for-051) | ICO reading and writing |
| [Format matrix and memory in 0.5.0](#format-matrix-and-memory-in-050) | All formats and resize against libwebp, Pillow and others; allocations |
| [ARM64 in 0.4.8](#arm64-in-048) | SIMD on ARM64 (RK3588) |
| [Hot paths in 0.4.6](#hot-paths-in-046) | Deflate, PNG, JPEG, resampling and checksums |
| [VP8L encoding in 0.4.5](#vp8l-encoding-in-045) | WebP lossless encoding |
| [VP8L decoding in 0.4.4](#vp8l-decoding-in-044) | WebP lossless decoding |
| [VP8L predictors in 0.4.3](#vp8l-predictors-in-043) | WebP lossless predictors |

Regression checks against the previous release use the 80-case `matrix-probe` matrix in ABBA order on CPUs 0–7; cases
above +5% are rechecked alternating in separate AssemblyLoadContexts (15 samples of at least 180 ms). These are
lightweight probes, not full BenchmarkDotNet studies.

## Film negatives and cleanup (0.7.11)

`photo-probe` as in [Photo adjustments (0.7.7)](#photo-adjustments-077): 4000 × 3000, copy-on-write `Clone`, median of
seven samples, Intel Core i5-12500H, linux-x64, CPUs 0–7 or one thread on CPU 0. The pixels are identical at both
(`ProcessingDeterminismTests`); every row allocates only the 45.8 MiB result.

| Operation | 1 thread | 8 threads |
| --- | ---: | ---: |
| `Curves` (4 points) | 13.2 ms | 11.2 ms |
| `WhiteBalance(Rgba32)` | 12.1 ms | 10.6 ms |
| `AutoWhiteBalance()` | 61.4 ms | 60.1 ms |
| `RemoveDust(6)` | 967.2 ms | 406.5 ms |
| `ReduceColorNoise()`, radius 30 px | 1361.0 ms | 271.1 ms |

Curves and both white balances are 3 × 256 lookup tables; `AutoWhiteBalance` counts its histograms on one thread.
`RemoveDust` counts instead of sorting the four segments through each pixel and examines the neighbourhood only
around specks. `ReduceColorNoise` runs eight box passes over float planes. Raw results:
[8 threads](measurements/photo-adjust-0.7.11-probe-x64.json),
[1 thread](measurements/photo-adjust-0.7.11-probe-x64-1thread.json).

`InvertNegative` is a 3 × 256 table like `Curves`. `AnalyzeNegative` samples about one million pixels: 11–22 ms on
the 2–5 MP test negatives. `DevelopNegative` on a 5 MP photographed negative (2780 × 1790) takes about 0.3 s, mostly
dust and color-noise removal.

## Photo adjustments (0.7.7)

`photo-probe` (`PhotoAdjustBenchmarks`): 4000 × 3000 `photo_alpha.png`, each operation on a copy-on-write `Clone`,
median of seven samples, Intel Core i5-12500H, linux-x64. "8 threads" runs on CPUs 0–7, "1 thread" with
`DOTNET_PROCESSOR_COUNT=1` on CPU 0. Both give identical pixels (`ProcessingDeterminismTests`). Every row allocates the
45.8 MiB result; planes for the blurs and the guided filter come from `ArrayPool`.

| Operation | 1 thread | 8 threads |
| --- | ---: | ---: |
| `CopyOnly` (control) | 2.4 ms | 2.4 ms |
| `Exposure(0.7)` | 12.1 ms | 12.1 ms |
| `WhiteBalance(0.4, −0.2)` | 12.1 ms | 12.0 ms |
| `Vibrance(0.5)` | 130.6 ms | 36.0 ms |
| `Vignette(0.6)` | 122.8 ms | 34.3 ms |
| `ShadowsHighlights(0.5, −0.5)`, σ = 60 px | 520.0 ms | 98.5 ms |
| `Clarity(0.5)`, σ = 60 px | 435.1 ms | 82.8 ms |
| `Denoise(0.5)`, radius 2 | 1580.0 ms | 297.5 ms |

Exposure and white balance are 3 × 256 lookup tables. The others are scalar and split into bands of rows (columns in
the vertical box pass). No SIMD yet: the box passes keep double sums, and the per-pixel cost is dominated by
`DeterministicMath.Exp2` and the sRGB tables. Raw results: [8 threads](measurements/photo-adjust-0.7.7-probe-x64.json),
[1 thread](measurements/photo-adjust-0.7.7-probe-x64-1thread.json).

```bash
taskset -c 0-7 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- photo-probe photo.json
DOTNET_PROCESSOR_COUNT=1 taskset -c 0 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- photo-probe photo-1.json
```

## WebP lossy and pipeline optimization (0.7.5)

0.7.5 speeds up lossy WebP and the common processing path without changing results: encoded bytes (identical to
`cwebp` 1.6.0 as before), decoded pixels and processing results equal 0.7.4 with AVX2, without AVX2 and without hardware
intrinsics. About 2,800 outputs were compared after every step: encoder options (methods, qualities, presets, filters,
segments, partitions, alpha, target size and PSNR, threads), the decoder corpus and libwebp test data including
truncated and bit-flipped files, and pipeline operations (decoding with orientation and ICC conversion, `ConvertToSrgb`
for every corpus profile, linear-light resizing with five filters, linear `DrawImage`).

A profile and a micro-benchmark of the encoder primitives against the same functions in `libwebp.a` set the order of
the work. The 0.7.4 vector code computed in 32-bit lanes with `pmulld`, where libwebp uses 16-bit `pmaddwd`/`pmulhw`:
predictions were 5–9×, `FTransform` 2.4×, `SSE4x4` 2.8×, `ITransform` 1.9× and quantization 1.6× slower than libwebp.

- **Encoder DSP:** ports of `enc_sse2.c`/`enc_sse41.c` in 16-bit lanes through the shared `Vector128` helpers (also
  ARM64): `FTransform` and `FTransform2` (two blocks), `CollectHistogram`, `SSE4x4`, `TTransform`/`Disto` (with the
  weights transposed, so any weights give the scalar result), `QuantizeBlock` (exact for every `short` input with the
  encoder's matrices) and `ITransform` (16-bit when every coefficient is within ±2048, which keeps all intermediate values
  in range; otherwise the 32-bit transform). Byte inputs keep every 16-bit intermediate in range, so these equal the C code.
- **Predictions:** 4×4 modes are assembled from the 3-tap and 2-tap averages of the edge, computed once; 8×8 and 16×16
  predictions are filled with vectors. Vector `IsFlat` and `Last`.
- **Mode decision and tokens:** invariants hoisted from the `PickIntra4` loop; the token buffer is one pooled array that
  doubles instead of 8192-entry pages (a 12 MP image needed about 1,200 pages, most of them allocated anew on every encode
  because the shared pool keeps few arrays per size); tokens are emitted with the coder state in locals.
- **Decoder:** coefficient parsing (`GetCoeffs`) keeps the arithmetic decoder's state in locals, so value and bit count
  stay in registers (C compilers achieve this through strict aliasing), and reads the probabilities by reference;
  reconstruction copies blocks and rotates the left samples without per-call overhead.
- **Pipeline:** quarter-turn rotations fill the output in 32×32 tiles (the previous loop wrote one pixel per output row);
  `ConvertToSrgb` converts bands of 64 K pixels in parallel, and in place while an image is decoded with
  `ColorConversion = ToSrgb` (the image is not visible to the caller yet; the public operation still commits new buffers
  together); linear-light resizing works in bands of 32 output rows, each converting and resampling horizontally only the
  source rows it needs, instead of three image-sized `Vector4` buffers.
- **Withdrawn:** a 16-bit inverse transform in the decoder (3% slower there), unchecked `ResidualCost` and an inline
  refill of the bit reader (no gain).

`webp-probe` against the C program linked with `libwebp.a` 1.6.0 (q75 m4 `WebPEncode`, `WebPDecodeRGBA`, median of 21
calls, 7 for 12 MP), i5-12500H, CPUs 0–7. The 12 MP input is a local 4032×3024 photo saved as PNG; both libraries write
the same bytes for every input:

| Input | Operation | Threads | 0.7.4 (ms) | 0.7.5 (ms) | Change | libwebp 1T (ms) | 0.7.5 / libwebp |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| photo 256×192 | encode | 1 | 6.99 | 4.87 | −30% | 3.43 | 1.42× |
| | decode | 1 | 0.653 | 0.597 | −9% | 0.376 | 1.59× |
| graphic 200×150 | encode | 1 | 5.85 | 4.60 | −21% | 2.93 | 1.57× |
| | decode | 1 | 0.696 | 0.642 | −8% | 0.485 | 1.32× |
| kodim23 768×512 | encode | 1 | 45.6 | 31.3 | −31% | 22.9 | 1.36× |
| | | default | 20.1 | 15.0 | −25% | | |
| | decode | 1 | 3.49 | 3.18 | −9% | 2.01 | 1.59× |
| | | default | 2.75 | 2.24 | −18% | | |
| 12 MP photo | encode | 1 | 1677 | 1208 | −28% | 787 | 1.54× |
| | | default | 572 | 414 | −28% | | |
| | decode | 1 | 141.8 | 129.9 | −8% | 100.3 | 1.29× |
| | | default | 107.7 | 96.2 | −11% | | |

Encoding allocates 4.7 MB instead of 30 MB for the 12 MP photo. `webp-probe` now warms up for at least 1 s: with 50
short calls only, decode paths that an input reaches first could still run unoptimized (the JIT promotes methods after
a 100 ms pause in new compilations), which made small decodes look 2–4× slower in some runs and inputs.

The goals of this release were 1.5× libwebp for single-threaded encoding and 1.3× for decoding. Encoding meets it on
Kodak and the 256×192 photo (1.36×, 1.42×) and misses it slightly on the 12 MP photo and the graphic (1.54×, 1.57×),
where coefficient cost estimation (`ResidualCost`, 15%) and the remaining per-mode work of `PickIntra4` dominate.
Decoding meets it on the 12 MP photo (1.29×) and is at 1.32× on the graphic; the photographic small and Kodak inputs stay
at 1.6×, where prediction, reconstruction and the loop filter outweigh coefficient parsing.

The integration path of 0.7.0 (`integration-probe`, 12 MP JPEG with EXIF orientation 6 and a Display P3 profile):

| Operation | 0.7.4 | 0.7.5 | Change |
| --- | ---: | ---: | ---: |
| `load-orient-srgb-resize-webp` time | 355 ms | 210–215 ms | −40% |
| allocated | 153.4 MiB | 106.1 MiB | −31% |
| cancellation latency (median, max) | 0.3 ms, 0.4 ms | 0.2 ms, 0.7 ms | |

Within it, loading with orientation went from 137 to 55 ms (the rotation from about 93 to 12 ms) and the ICC conversion
from 61 to about 10 ms. `thumbnail-256` allocates 3.4 MiB instead of 6.2 MiB (conversion in place); the other
operations of the probe stay within run-to-run variation. Linear-light halving of the 12 MP photo takes
15–16 ms instead of 21 ms (Lanczos3 on encoded values: 15.5 ms) and no longer leaves about 340 MB of float buffers in the
shared pool.

`matrix-probe` (corpus inputs, the 1600×1200 graphic and `kodim23`; best of two 0.7.4 and three 0.7.5 sweeps): lossy WebP
−6% to −31%; allocations unchanged. Cases above +5% were operations this release did not touch (BMP and PNG decoding of
short inputs) plus the default-thread decode of the 256×192 photo, whose minima in alternating runs are lower in 0.7.5
(0.59–0.63 against 0.66–0.79 ms).

Fixed on the way: multi-threaded lossy encoding (method 3 and above) updated the per-segment maximum DC edge without
synchronization; a lost update lowered the loop-filter strength, so about 1% of method 6 encodes of `kodim23` differed
from `cwebp`. The update is now an atomic maximum and 600 of 600 encodes match.

Raw data: [webp-0.7.5-x64.csv](measurements/webp-0.7.5-x64.csv), [matrix-0.7.5-x64.csv](measurements/matrix-0.7.5-x64.csv),
[integration-0.7.5-x64-before.json](measurements/integration-0.7.5-x64-before.json),
[-after](measurements/integration-0.7.5-x64-after.json).

## JPEG optimization (0.7.4)

0.7.4 speeds up JPEG without changing results: decoded pixels and encoded bytes are identical to 0.7.3 with AVX2,
without AVX2 and without hardware intrinsics, including truncated and corrupt files (about 5,000 decodes and encodes
compared with the 0.7.3 code). A stage profile came first and set the order of the work; it differed from the plan:

| Stage (1920×1440 q75 4:2:0, one thread) | 0.7.3 (ms) | 0.7.4 (ms) |
| --- | ---: | ---: |
| Decoding: Huffman entropy | ~6.3 | ~4.6 |
| Decoding: inverse DCT | ~2.9 | ~1.7 |
| Decoding: upsampling and YCbCr→RGBA | 7.6 | 1.5 |
| Encoding: gathering and downsampling block samples | ~26 | ~2 |
| Encoding: RGB→YCbCr, forward DCT and quantization | ~15 | ~7 |

- **Decoder entropy:** a fast path like libjpeg-turbo's `decode_mcu_fast` decodes whole MCUs from a 64-bit buffer
  refilled eight bytes at a time; an 11-bit lookup resolves short codes together with their coefficient value. When
  the data runs short (a marker), the MCU is decoded again by the checked path, so corrupt data behaves as before.
  Progressive AC refinement finds the coefficients to correct from a bit mask instead of testing every position.
- **Inverse DCT:** AVX2 with `pmaddwd` on two blocks at once, using libjpeg-turbo's integer arithmetic.
- **Upsampling and color:** fancy upsampling and YCbCr→RGBA with `Vector256`/`Vector128`.
- **Encoder:** interior blocks gather and average their samples without clamping (vectorized), RGB→YCbCr and
  quantization are vectorized; the quantizer divides in single precision, exact for every DCT output.

BenchmarkDotNet means (`JpegDecodeBenchmarks`, `JpegEncodeBenchmarks`, 9 iterations), i5-12500H. Inputs are local and
fixed: a 12 MP iPhone photo (4:2:0), and 1920×1440 JPEGs saved by Pillow from the same photo (q75 4:2:0, q90 4:4:4, q75
progressive). Encoding uses the same image as PNG at 1920×1440 and at 12 MP. Pillow 10.2.0 (libjpeg-turbo with SIMD)
times are single-threaded minima measured separately, for context:

| Operation | Threads | 0.7.3 (ms) | 0.7.4 (ms) | Change | Pillow (ms) |
| --- | --- | ---: | ---: | ---: | ---: |
| Decode 1920×1440 q75 4:2:0 | 1 | 18.54 | 9.30 | −50% | 6.5 |
| | default | 12.28 | 8.02 | −35% | |
| Decode 1920×1440 q90 4:4:4 | 1 | 23.83 | 15.95 | −33% | 11.2 |
| | default | 20.29 | 15.37 | −24% | |
| Decode 1920×1440 progressive | 1 | 33.06 | 18.97 | −43% | 18.6 |
| | default | 23.73 | 17.20 | −28% | |
| Decode 12 MP photo | 1 | 108.13 | 54.46 | −50% | 51.4 |
| | default | 75.70 | 50.20 | −34% | |
| Encode 1920×1440 q75 4:2:0 | 1 | 48.64 | 14.87 | −69% | 5.7 |
| | default | 13.65 | 8.67 | −36% | |
| Encode 1920×1440 q90 4:4:4 | 1 | 79.45 | 25.88 | −67% | — |
| | default | 22.75 | 15.34 | −33% | |
| Encode 1920×1440 progressive | 1 | 71.21 | 37.43 | −47% | — |
| | default | 35.56 | 31.21 | −12% | |
| Encode 12 MP q75 4:2:0 | 1 | 203.39 | 62.41 | −69% | — |
| | default | 47.82 | 32.91 | −31% | |
| Encode 12 MP q90 4:4:4 | 1 | 312.05 | 111.42 | −64% | — |
| | default | 89.18 | 59.95 | −33% | |
| Encode 12 MP progressive | 1 | 288.81 | 150.23 | −48% | — |
| | default | 136.85 | 119.48 | −13% | |

Allocations are unchanged (10.56 MB for a 1920×1440 decode, 0.5 MB for a q75 encode); Huffman lookup tables now come
from the shared pool, so small files allocate less than before. Single-threaded baseline decoding is 1.43× libjpeg-turbo
at 4:2:0 in this run (1.2–1.3× in the stage profile), 1.06× for the 12 MP photo and 1.42× at q90 4:4:4, where Huffman
decoding dominates; progressive decoding is level with it. The q75 encoder is 2.6× Pillow on one thread (8.5× in 0.7.3);
progressive encoding gained least, because its entropy coding (statistics and refinement passes) was not changed. Raw
data: [jpeg-0.7.4-x64-decode-before.csv](measurements/jpeg-0.7.4-x64-decode-before.csv),
[-after](measurements/jpeg-0.7.4-x64-decode-after.csv),
[jpeg-0.7.4-x64-encode-before.csv](measurements/jpeg-0.7.4-x64-encode-before.csv),
[-after](measurements/jpeg-0.7.4-x64-encode-after.csv).

## Own deflate versus System.IO.Compression (0.7.2)

PNG uses its own deflate/inflate (`Algorithms/Compression/`) rather than `ZLibStream`/`DeflateStream`, which wrap the
zlib-ng shipped with the runtime. On 2026-10-18 the core was ported to `ZLibStream`/`DeflateStream` and measured against
0.7.2; the port was reverted because it made default PNG encoding up to 6× slower.

Setup: `PngCompressionBenchmarks` (BenchmarkDotNet, one launch, two warm-ups, six iterations), i5-12500H, .NET 10.0.
Inputs are built from the corpus: `photo.png` upscaled bicubic to 4000×3000, `graphic.png` upscaled nearest to 2000×1500,
and a synthetic 2000×1500 gradient with ±6 noise. Each version decodes the file it encoded itself. Raw data:
[deflate-0.7.2-x64-own.csv](measurements/deflate-0.7.2-x64-own.csv) and
[deflate-0.7.2-x64-bcl.csv](measurements/deflate-0.7.2-x64-bcl.csv).

| Image | Level | Threads | Encode own | Encode BCL | Decode own | Decode BCL | Size own | Size BCL |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Photo 4000×3000 | 1 | all | 92 ms | 234 ms | 70 ms | 81 ms | 6.31 MB | 7.27 MB |
| | 1 | 1 | 243 ms | 263 ms | 124 ms | 135 ms | | |
| | 6 | all | 271 ms | 1.62 s | 58 ms | 58 ms | 5.405 MB | 5.407 MB |
| | 6 | 1 | 1.52 s | 1.65 s | 92 ms | 111 ms | | |
| | 9 | all | 3.81 s | 28.4 s | 57 ms | 57 ms | 4.817 MB | 4.819 MB |
| | 9 | 1 | 29.2 s | 30.4 s | 86 ms | 103 ms | | |
| Noisy 2000×1500 | 1 | all | 54 ms | 79 ms | 27 ms | 30 ms | 4.15 MB | 3.90 MB |
| | 1 | 1 | 103 ms | 85 ms | 38 ms | 40 ms | | |
| | 6 | all | 110 ms | 518 ms | 20 ms | 29 ms | 3.515 MB | 3.520 MB |
| | 6 | 1 | 666 ms | 521 ms | 31 ms | 40 ms | | |
| | 9 | all | 205 ms | 1.09 s | 19 ms | 29 ms | 3.425 MB | 3.430 MB |
| | 9 | 1 | 1.34 s | 1.09 s | 30 ms | 39 ms | | |
| Graphic 2000×1500 | 1 | all | 11.1 ms | 10.9 ms | 9.5 ms | 9.4 ms | 32.8 kB | 31.0 kB |
| | 6 | all | 11.3 ms | 14.3 ms | 9.0 ms | 9.4 ms | 16.9 kB | 16.9 kB |
| | 9 | all | 12.7 ms | 19.0 ms | 9.1 ms | 9.5 ms | 8.5 kB | 8.5 kB |

Why the own implementation stays:

- **Parallel encoding.** Inputs of 256 KiB and more are deflated in parallel segments joined to the exact sequential
  parse, so the default (level 6, all threads) is 4.7–6× faster than `ZLibStream`, which compresses on one thread.
  On one thread the two are within −20% to +10% of each other; zlib-ng is faster only on noisy data.
- **Faster decoding.** Inflate is equal or up to 1.5× faster, and the pipelined decoder unfilters rows while it runs.
- **Same compression.** Levels 6 and 9 differ by less than 0.2%; level 1 goes either way (photo +15% with zlib-ng,
  noise −6%).
- **Deterministic output.** The bytes do not depend on the thread count or the runtime version; zlib-ng output may
  change between runtimes, so byte-comparing tests would break.
- **Behaviour the BCL streams lack.** `ZLibStream` accepts a truncated stream silently, writes nothing for empty input
  and reports every error as the same `InvalidDataException`; the own inflater detects truncation, enforces output
  limits per block and stops at the expected size like libpng.

In 0.7.2 `ZLibStream` allocated 2–20% less managed memory (at most about 0.9 MB less per operation); its native buffers
are not counted. 0.7.3 closed that gap without changing the time or the output: the deflater rents its block symbol
buffers (−0.38 MB per encode, −20% for the graphic) and the inflater rebuilds its Huffman tables in place (−0.3 to
−0.8 MB per decode, which now allocates only the decoded image). Raw data:
[deflate-0.7.3-x64-own.csv](measurements/deflate-0.7.3-x64-own.csv). Revisit the choice only with new measurements,
for example if the runtime gains a parallel or span-based deflate.

## Integration measurements (0.7.0)

Complete user paths on 12 MP inputs, against 0.6.9, with the same `integration-probe` (0.6.9 API only) and input bytes.
The photo is a 3840×3072 JPEG (quality 90) tiled from 30 Kodak images, with EXIF orientation 6 and a Display P3 ICC
profile. The HDR file is a 4000×3000 JPEG with an ISO 21496-1 RGB gain map at quarter resolution. Time: median of nine
samples of at least 100 ms after two warm-ups, runs alternated, CPUs 0–7, 2026-10-17. Allocations are per call.
Cancellation is requested after a quarter of the median; latency is the time from the callback to
`OperationCanceledException` (median and maximum of nine). Peak RSS is measured in a fresh process per operation after
reading the input (reset through `/proc/self/clear_refs`, so it includes JIT start-up), median of three.

| Operation | 0.6.9 (ms) | 0.7.0 (ms) | Allocated | Cancel latency | Peak RSS |
| --- | ---: | ---: | ---: | ---: | ---: |
| `Image.Load` (12 MP JPEG) | 72.2–75.8 | 74.2–80.6 | 45.0 MiB | 0.2 ms (max 1.8) | 75 MiB |
| Thumbnail 256 px: `TargetSize`, orientation, ICC → sRGB, JPEG | 64.4–67.6 | 64.2–66.6 | 6.2 MiB | — | 24 MiB |
| Load with orientation and ICC → sRGB, `Resize(1600, 0)`, WebP q80 | 426–436 | 429–440 | 156.5 MiB | 0.2–0.3 ms (max 0.4) | 246 MiB |
| `ReadMetadata` | 1.75–1.88 | 1.75–1.89 | < 0.1 MiB | — | 11 MiB |
| `EditMetadata` (EXIF model, remove GPS) | 4.05–4.75 | 4.36–4.77 | 3.3 MiB | — | 18 MiB |
| HDR: `LoadGainMap`, `ToHdr`, `ToneMap` | 131.8–132.6 | 131.4–132.9 | 277.6 MiB | 0.1–0.2 ms (max 0.4) | 308 MiB |
| HDR: resize to half and save with the gain map (`Require`) | 72.8–73.1 | 72.9–74.0 | 63.2 MiB | 0.1–0.2 ms (max 0.4) | 133 MiB |

Allocations and peak memory are identical between the releases; the 0.7.0 plugin SPI changes nothing in the pixel loops.
The first sweep showed +5% for `Image.Load` and +7% for metadata, but three more alternating rounds put both builds in
the same bimodal ranges (load 72–76 ms; metadata read 1.75 or 1.88 ms): noise, not a regression. Peak memory is close to
the decoded pixels plus codec buffers; the thumbnail never holds the 12 MP image, while HDR reconstruction adds the
16-byte-per-pixel `HdrImage` (183 MiB for 12 MP).

**Long animations.** The 0.6.0 `sequence-probe` extended to 10,000 frames (256×256 sprite, one process per row, wall
time including start-up). Retained memory over the warmed baseline stays bounded:

| Format | Frames | Writer retained +MiB | Reader retained +MiB | Write / read time | Output |
| --- | ---: | ---: | ---: | ---: | ---: |
| GIF | 1,000 / 10,000 | 0.34 / 0.34 | 0.14 / 0.14 | 0.6 / 4.2 s, 0.4 / 2.4 s | 0.57 / 5.7 MB |
| PNG | 1,000 / 10,000 | 0.47 / 0.47 | 1.34 / 1.34 | 1.0 / 5.7 s, 0.6 / 1.7 s | 0.49 / 4.9 MB |
| WebP | 1,000 / 10,000 | 2.49 / 5.64 | 0.11 / 0.94 | 1.2 / 7.7 s, 0.3 / 1.0 s | 0.10 / 0.96 MB |

WebP's growth comes from shared `ArrayPool` buffers filling up, not per-frame state: at 20,000 frames the writer retains
5.63 MiB and the reader 0.94 MiB. A frame reader applies `MaxPixels` to the whole sequence; 10,000 frames of 256×256
(655 Mpx) exceed the default 2²⁸, so the probe (and any application streaming long animations) raises the limit. Raw
data: [integration-0.7.0-x64.json](measurements/integration-0.7.0-x64.json). Not measured on ARM64.

```bash
dotnet build -c Release benchmarks/PictureSharp.Benchmarks
dotnet benchmarks/PictureSharp.Benchmarks/bin/Release/net10.0/PictureSharp.Benchmarks.dll integration-probe prepare /tmp/probe
taskset -c 0-7 dotnet benchmarks/PictureSharp.Benchmarks/bin/Release/net10.0/PictureSharp.Benchmarks.dll integration-probe time /tmp/probe integration.json
taskset -c 0-7 dotnet benchmarks/PictureSharp.Benchmarks/bin/Release/net10.0/PictureSharp.Benchmarks.dll integration-probe peak /tmp/probe hdr-tone-map
taskset -c 0-7 dotnet benchmarks/PictureSharp.Benchmarks/bin/Release/net10.0/PictureSharp.Benchmarks.dll sequence-probe webp 10000 write /tmp/sequence.webp
```

## Editing and saving HDR photos (0.6.9)

`Load` → `Resize` to half → `Save` (JPEG quality 90) of a synthetic JPEG with an ISO 21496-1 RGB gain map at quarter
resolution (`HdrEditBenchmarks`, `hdr-edit-probe`: median of nine runs after three warmups, ranges from two runs). "SDR"
skips `LoadGainMap` and saves the base only; "gain map" resizes the map with the base and writes it with
`JpegGainMapMode.Require`.

| Image | SDR | With gain map | Allocated (SDR / gain map) | Output |
| --- | --- | --- | --- | --- |
| 12 MP (4000×3000, map 1000×750) | 54–56 ms | 59–63 ms | 57.7 / 63.2 MiB | 190 / 246 KB |
| 48 MP (8000×6000, map 2000×1500) | 181–183 ms | 197–198 ms | 230.0 / 251.1 MiB | 626 / 809 KB |

Keeping the map adds 6–16% time and only the map's own buffers. Raw data:
[hdr-edit-0.6.9-x64.json](measurements/hdr-edit-0.6.9-x64.json). Not measured on ARM64.

```bash
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- hdr-edit-probe docs/measurements/hdr-edit-0.6.9-x64.json
```

## SIMD for HDR and graphics (0.6.7)

0.6.7 adds vector paths to `ToHdr`, `ToneMap`, all twelve `DrawImage` blend modes, `AutoCrop`/`FindTrimBounds` and
`ApplyMask`: a `Vector128` form (SSE or NEON) and, where faster, an AVX2 `Vector256` form (`*.X86.cs`). Each performs
the scalar operations in the same order and is tested bit-identical (`*SimdTests`, also with the fallbacks disabled).
Graphics results are byte-identical to 0.6.5; `ToHdr` changed slightly because it now uses PictureSharp's deterministic
`float` power and exponential functions (see [HDR photos](hdr.md)), which also makes x64 and ARM64 identical.

Table lookups (sRGB, base linearization, map samples) stay scalar per lane: there is no efficient byte gather, and
AVX2's float gather needs pointers. This limits the linear-light blend modes. A vector luma for `GetHistogram` gained
only 3.5% and was dropped; `Levels`/`Gamma` are byte lookups and stay scalar.

BenchmarkDotNet 0.15.2 (`HdrBenchmarks`, `GraphicsBenchmarks`), one launch, 3 warmups, 9 iterations, mean, CPUs 0–7.
The baseline is 0.6.5 (`git archive`) with the same benchmark sources. HDR inputs are synthetic JPEGs with a
quarter-resolution ISO 21496-1 map (mono with gamma 1, or RGB with gamma 0.85–0.87 and a negative minimum, as iPhone
maps). Graphics inputs are those of the 0.6.3 section. Allocations are unchanged.

### x64, 12 MP

| Operation | 0.6.5 | 0.6.7 Vector128 | 0.6.7 AVX2 | Speed-up |
| --- | ---: | ---: | ---: | ---: |
| `ToHdr`, mono map | 99.5 ms | 39.6 ms | 34.3 ms | 2.9× |
| `ToHdr`, mono map, one thread | 663.3 ms | 169.7 ms | 136.8 ms | 4.8× |
| `ToHdr`, RGB map with gamma | 249.2 ms | 78.4 ms | 54.0 ms | 4.6× |
| `ToHdr`, RGB map with gamma, one thread | 1346.6 ms | 372.4 ms | 258.2 ms | 5.2× |
| `ToneMap` (Vector128 only) | 86.8 ms | 50.9 ms | 49.9 ms | 1.7× |
| `FindTrimBounds` | 2.27 ms | 1.26 ms | 1.27 ms | 1.8× |
| `AutoCrop` (includes the crop copy) | 5.97 ms | 4.88 ms | 4.98 ms | 1.2× |
| `ApplyMask` (alpha) | 23.1 ms | 8.2 ms | 8.0 ms | 2.9× |
| `ApplyMask` (luminance) | 30.2 ms | 10.5 ms | 10.7 ms | 2.8× |
| `DrawImage` `Normal`, opacity 0.8 | 49.8 ms | 29.3 ms | 17.1 ms | 2.9× |
| `DrawImage` `Multiply` | 76.3 ms | 35.9 ms | 23.2 ms | 3.3× |
| `DrawImage` `Screen` | 80.9 ms | 37.3 ms | 23.9 ms | 3.4× |
| `DrawImage` `SoftLight` | 99.5 ms | 58.2 ms | 33.2 ms | 3.0× |
| `DrawImage` `ColorBurn` | 98.6 ms | 50.3 ms | 31.0 ms | 3.2× |
| `DrawImage` `Multiply`, linear light | 84.8 ms | 67.6 ms | 63.4 ms | 1.3× |
| `Levels` (unchanged control) | 11.3 ms | 11.4 ms | 11.3 ms | — |
| `GetHistogram` (unchanged control) | 25.9 ms | 26.7 ms | 26.2 ms | — |

`ToneMap` and the mask and trim scans have no AVX2 form (that column runs Vector128). Speed-ups at 3 MP and on the
200×160 image are similar.

### ARM64, 12 MP

Orange Pi 5 (RK3588, 8 cores), .NET 10.0.112, same settings, Vector128 (NEON) path.

| Operation | 0.6.5 | 0.6.7 | Speed-up |
| --- | ---: | ---: | ---: |
| `ToHdr`, mono map | 155.9 ms | 100.2 ms | 1.6× |
| `ToHdr`, mono map, one thread | 724.7 ms | 473.8 ms | 1.5× |
| `ToHdr`, RGB map with gamma | 533.6 ms | 195.2 ms | 2.7× |
| `ToHdr`, RGB map with gamma, one thread | 2560.7 ms | 987.1 ms | 2.6× |
| `ToneMap` | 204.3 ms | 137.9 ms | 1.5× |
| `FindTrimBounds` | 6.38 ms | 3.52 ms | 1.8× |
| `AutoCrop` | 10.94 ms | 8.48 ms | 1.3× |
| `ApplyMask` (alpha) | 94.1 ms | 18.6 ms | 5.1× |
| `ApplyMask` (luminance) | 127.5 ms | 31.7 ms | 4.0× |
| `DrawImage` `Normal` (`graphics-probe`) | 129.5 ms | 100.4 ms | 1.3× |
| `DrawImage` `Multiply` | 207.3 ms | 104.1 ms | 2.0× |
| `DrawImage` `Screen` | 222.8 ms | 109.8 ms | 2.0× |
| `DrawImage` `SoftLight` | 292.8 ms | 155.1 ms | 1.9× |
| `DrawImage` `ColorBurn` | 292.0 ms | 133.5 ms | 2.2× |
| `DrawImage` `Multiply`, linear light | 252.3 ms | 182.0 ms | 1.4× |
| `Levels` / `GetHistogram` (controls) | 33.0 / 90.1 ms | 33.1 / 90.5 ms | — |

The `Normal` row comes from `graphics-probe` (median of seven) because its vector path came after the ARM64 run. A first
ARM64 run showed `ToneMap` slower (204 → 314 ms): reading vector lanes with a loop index made the JIT spill the vectors.
Constant lane indices fixed it on both architectures.

```bash
taskset -c 0-7 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*HdrBenchmarks*' '*GraphicsBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 9
DOTNET_EnableAVX2=0 taskset -c 0-7 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*HdrBenchmarks*' '*GraphicsBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 9
```

Quick medians without BenchmarkDotNet: `hdr-probe` and `graphics-probe`. Raw reports: x64
[HDR](measurements/simd-0.6.7-bdn-x64-hdr-avx2.csv) and [graphics](measurements/simd-0.6.7-bdn-x64-graphics-avx2.csv)
(with `-base` and `-vector128` variants), ARM64 [HDR](measurements/simd-0.6.7-bdn-arm64-hdr-new.csv) and
[graphics](measurements/simd-0.6.7-bdn-arm64-graphics-new.csv) (with `-base`), and the probe files `simd-0.6.7-*.json`.

Regression matrix against 0.6.5: median +0.24%, largest allocation increase +0.29%. Three cases exceeded +5% (WebP lossy
encode +26% in one of two samples, JPEG encode +7.7% in one, GIF encode +10.8% in both); no codec changed in 0.6.7 and
they were not rechecked alternating. [Raw matrix](measurements/simd-regression.csv).

## Graphics operations (0.6.3)

`graphics-probe` (median of seven samples of at least 150 ms, CPUs 0–7) on `photo_alpha.png` (200×160, "small") and the
same image resized to 4000×3000 ("12 MP"). In-place operations run on a copy-on-write `Clone`, so their time includes
one pixel copy (`CopyOnly`). `AutoCrop` and `FindTrimBounds` use a version with 5% transparent margins.

| Operation | Small µs | 12 MP ms | 12 MP allocated |
| --- | ---: | ---: | ---: |
| `CopyOnly` (clone baseline) | 6.1 | 2.9 | 45.8 MiB |
| `Levels` | 36.5 | 10.5 | 45.8 MiB |
| `Gamma` | 34.3 | 10.4 | 45.8 MiB |
| `AutoContrast(1)` | 98.1 | 34.2 | 45.8 MiB |
| `Equalize` | 91.2 | 35.4 | 45.8 MiB |
| `GetHistogram` | 66.3 | 26.1 | 10 KiB |
| `FindTrimBounds` | 6.2 | 2.3 | 107 B |
| `AutoCrop` | 12.8 | 6.1 | 35.5 MiB |
| `ApplyMask` (alpha) | 50.5 | 17.9 | 45.8 MiB |
| `ApplyMask` (luminance) | 76.7 | 25.3 | 45.8 MiB |
| `DrawImage` `Normal`, opacity 0.8 | 136.4 | 49.4 | 45.8 MiB |
| `DrawImage` `Multiply` | 205.7 | 74.8 | 45.8 MiB |
| `DrawImage` `SoftLight` | 279.4 | 97.4 | 45.8 MiB |
| `DrawImage` `Multiply`, linear light | 232.3 | 83.4 | 45.8 MiB |

Two bit-identical scalar changes came from these measurements. The histogram alternates between two sets of counters, so
runs of equal values do not chain dependent increments: this halved `GetHistogram`, `AutoContrast` and `Equalize` at 12
MP (56 → 26 ms, 68 → 34 ms). Blend modes are specialized through generic structs instead of a switch in the pixel loop:
`Multiply` 117 → 75 ms, `SoftLight` 148 → 97 ms. [Raw data](measurements/graphics-benchmarks.json).

```bash
taskset -c 0-7 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- graphics-probe /tmp/graphics.json
```

Regression matrix against 0.6.2: median −0.52%, largest allocation increase +0.13%. Rechecked cases: WebP lossless
encode +0.91%, JPEG encode +0.60%; PCX decode of the 1600×1200 graphic is bimodal and moved between −9.1% and +8.2%
across seven alternating runs, even with only `PictureSharp.dll` swapped, while the codec is unchanged. No repeatable
regression above 5%. [Raw matrix](measurements/graphics-regression.csv),
[alternating confirmations](measurements/graphics-regression-confirmation.json).

## Color management and linear light (0.6.2)

A local probe on a 3000×2250 RGB image (CPUs 0–7, best of seven runs after warm-up, `Clone` per run):

| Operation | Default | Linear light | Ratio |
| --- | ---: | ---: | ---: |
| Resize to 800×600 (Lanczos3) | 15.7 ms | 26.6 ms | 1.69 |
| Resize to 6000×4500 (Lanczos3) | 58.1 ms | 87.3 ms | 1.50 |
| `ConvertToSrgb()` (Display P3 matrix/TRC) | — | 38.1 ms | — |

Linear light uses `Vector4` float buffers with the same weights and passes as the fixed-point path; `ConvertToSrgb()`
writes new buffers so a canceled conversion commits nothing.

Regression matrix against 0.6.1 (one sweep per version): median +0.28%, largest allocation increase +0.27%. Nine cases
above +5% were rechecked: −1.79% (GIF decode) to +2.28% (single-threaded WebP lossless decode). No repeatable regression
above 5%. [Raw matrix](measurements/color-regression.csv),
[alternating confirmations](measurements/color-regression-confirmation.json).

## Metadata without image recompression (0.6.1 implementation)

The committed synthetic `src/photo.png` (256×192), encoded as JPEG, PNG and lossless WebP with a synthetic EXIF
profile. `ReadMetadata` inspects the container; `EditMetadata` removes EXIF GPS; "Load + edit + encode" is the
recompressing alternative. Memory input/output, warmed buffers, CPUs 0–7, medians of seven samples of at least 150 ms.

| Format | Read metadata µs | Edit metadata µs | Load + edit + encode µs | Edit allocated KiB | Load + edit + encode allocated KiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| JPEG | 6.34 | 22.02 | 939.84 | 43.09 | 303.60 |
| PNG | 3.07 | 41.26 | 5765.44 | 111.05 | 868.68 |
| Lossless WebP | 0.18 | 25.70 | 26046.40 | 103.32 | 809.53 |

These are small inputs, not guarantees. JPEG inspection scans entropy-coded data for marker boundaries; WebP skips whole
image chunks. No pixel buffers are allocated, but the input is read in full and the output staged in memory, so large
files need proportional memory. Compressed PNG metadata may be re-deflated; IDAT/fdAT data stays unchanged.
[Raw metadata measurements](measurements/metadata-benchmarks.json).

```bash
dotnet build -c Release benchmarks/PictureSharp.Benchmarks
taskset -c 0-7 dotnet benchmarks/PictureSharp.Benchmarks/bin/Release/net10.0/PictureSharp.Benchmarks.dll metadata-probe /tmp/metadata-results.json
```

Regression matrix against 0.6.0 (`78eba60`, one sweep per version): median +0.74%, largest allocation increase +0.66%.
Rechecked: JPEG encode +3.41%, GIF encode +1.56%, Lanczos resize +0.48%, large WebP lossless decode −0.29%, large PCX
decode −0.92%. No repeatable regression above 5%. [Raw matrix](measurements/metadata-regression.csv),
[alternating confirmations](measurements/metadata-regression-confirmation.json).

## Sequential animation memory (0.6.0)

Fresh processes on CPUs 0–7, 2026-10-15. Each animation has a 256×256 canvas and a moving 64×64 opaque sprite over
transparency. GIF uses default options, APNG compression level 1, WebP lossless/exact method 0; PNG/WebP use one codec
thread. The writer receives one reused source frame; readers consume files from disk without retaining frames.

The table shows the largest **retained managed heap increase over a warmed baseline** after forced full GCs (every 10
frames). The 25-frame warmup initializes code and shared pools, so this excludes the runtime and is neither the total
process footprint nor a transient peak.

| Format | Writer retained +MiB (10 / 100 / 1000 frames) | Reader retained +MiB | Output KiB |
| --- | ---: | ---: | ---: |
| GIF | 0.34 / 0.34 / 0.34 | 0.01 / 0.08 / 0.14 | 5.6 / 55.8 / 557.7 |
| PNG | 0.47 / 0.47 / 0.47 | 0.58 / 0.58 / 1.33 | 4.8 / 47.9 / 477.5 |
| WebP | 1.46 / 2.32 / 2.48 | 0.14 / 0.39 / 0.42 | 0.6 / 9.3 / 93.8 |

The full RGBA canvases alone would take 2.5/25/250 MiB; retained memory stays bounded while the frame count grows
100-fold. GIF keeps local palettes, APNG keeps counters instead of a frame index, and WebP keeps encoded candidates in a
bounded keyframe window (ten frames do not fill it). A non-seekable WebP or unknown-count APNG output uses an explicitly
requested temporary file of the encoded size.

The [raw measurements](measurements/sequence-memory.tsv) also list cumulative allocations (1000 frames: GIF write 64
MiB, PNG 630 MiB, WebP 215 MiB; read 1.3/259/45.6 MiB), all reclaimed, and RSS increases at 1000 frames of ≤5.0 MiB for
writers and ≤2.4 MiB for readers. Sparse GC sampling can miss short peaks; the bound comes from the per-frame buffers
and WebP window. Large canvases, poorly compressing frames and other WebP keyframe options add their own bounded working
set.

```bash
dotnet build -c Release benchmarks/PictureSharp.Benchmarks
# One process per row; repeat for gif/png/webp and 10/100/1000. Write creates the input used by read.
taskset -c 0-7 dotnet benchmarks/PictureSharp.Benchmarks/bin/Release/net10.0/PictureSharp.Benchmarks.dll sequence-probe webp 1000 write /tmp/sequence.webp
taskset -c 0-7 dotnet benchmarks/PictureSharp.Benchmarks/bin/Release/net10.0/PictureSharp.Benchmarks.dll sequence-probe webp 1000 read /tmp/sequence.webp
```

Regression matrix against 0.5.7 (`457f333`, two sweeps per version): median +0.09%, largest allocation difference
+0.49%. Rechecked: `photo.png` WebP lossless encode +1.62%, lossy −0.91%. No repeatable regression above 5%.
[Raw results](measurements/sequences-benchmarks.csv).

## Layered architecture refactoring (0.5.5)

Compared against `ca427ea` over all 80 matrix cases, two sweeps per version: median −0.79%. Three aggregates above +5%
did not reproduce in alternating runs (−1.43% to +0.85%); allocation differences stayed below 0.3%. No speedup is
claimed. Raw matrix: [measurements/architecture-benchmarks.csv](measurements/architecture-benchmarks.csv).

## JPEG thumbnails in 0.5.4

`DecoderOptions.TargetSize` decodes JPEG at 1/2, 1/4 or 1/8 scale with libjpeg-turbo's reduced inverse DCTs
(`jidctred.c`), bit-exact with libjpeg-turbo 2.1.5 (through Pillow 10.2.0 `draft`) on the corpus, damaged streams and
local photos. `ThumbnailBenchmarks` compares `Load` + `Thumbnail(size, size)` with `TargetSize` at the default reducing
gap 2 and at gap 1. Inputs: two 4032×3024 camera photos (4:2:0 and 4:4:4), the 4:2:0 photo re-encoded progressive
(quality 90), and the same photo scaled to 6048×4536 (27 MP). BenchmarkDotNet 0.15.2, 3 warmups, 9 iterations,
2026-10-15.

```bash
PICTURESHARP_BENCHMARK_THUMBNAIL_FILES='/absolute/12mp.jpg:/absolute/27mp_progressive.jpg' \
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- --filter '*ThumbnailBenchmarks*' \
  --launchCount 1 --warmupCount 3 --iterationCount 9
dotnet benchmarks/PictureSharp.Benchmarks/bin/Release/net10.0/PictureSharp.Benchmarks.dll peak /absolute/12mp.jpg TargetSize 256
```

Mean time in ms (managed allocations per operation in MB):

| Input | Size | Load + Thumbnail | TargetSize | TargetSize, gap 1 |
| --- | ---: | ---: | ---: | ---: |
| 12 MP baseline 4:2:0 | 256 | 86.2 (48.0) | 69.2 (3.2) | 59.3 (1.0) |
| | 512 | 88.4 (48.6) | 78.4 (12.8) | 69.5 (3.8) |
| | 1920 | 92.7 (58.7) | 93.4 (58.7) | 81.0 (22.9) |
| 12 MP baseline 4:4:4 | 256 | 75.2 (48.0) | 46.9 (3.2) | 35.0 (1.0) |
| | 512 | 78.0 (48.6) | 60.2 (12.8) | 47.2 (3.8) |
| | 1920 | 78.7 (58.7) | 80.5 (58.7) | 63.4 (22.9) |
| 12 MP progressive 4:2:0 | 256 | 157.8 (48.0) | 141.7 (3.3) | 133.2 (1.0) |
| | 512 | 159.0 (48.6) | 144.8 (12.8) | 141.0 (3.8) |
| | 1920 | 164.0 (58.7) | 164.4 (58.7) | 149.6 (22.9) |
| 27 MP baseline 4:2:0 | 256 | 144.0 (107.6) | 84.9 (1.9) | 84.9 (1.9) |
| | 512 | 144.0 (108.2) | 99.6 (7.6) | 84.0 (2.5) |
| | 1920 | 150.6 (118.3) | 150.9 (118.3) | 123.6 (37.8) |
| 27 MP progressive 4:2:0 | 256 | 308.6 (107.7) | 243.2 (2.0) | 245.0 (2.0) |
| | 512 | 285.5 (108.3) | 245.1 (7.6) | 238.6 (2.5) |
| | 1920 | 293.9 (118.3) | 299.3 (118.3) | 261.7 (37.8) |

Planes and coefficients are pooled and do not appear above, so peak memory comes from the `peak` mode: one operation in
a fresh process, rise of `VmHWM` after reading the input and warming up.

| Input | Size | Load + Thumbnail | TargetSize | TargetSize, gap 1 |
| --- | ---: | ---: | ---: | ---: |
| 12 MP baseline 4:2:0 | 256 / 512 / 1920 | 68 / 72 / 98 MiB | 7 / 25 / 98 MiB | 2 / 8 / 43 MiB |
| 12 MP baseline 4:4:4 | 256 / 512 / 1920 | 85 / 89 / 115 MiB | 6 / 25 / 115 MiB | 2 / 8 / 43 MiB |
| 12 MP progressive 4:2:0 | 256 / 512 / 1920 | 103 / 107 / 133 MiB | 42 / 60 / 133 MiB | 37 / 43 / 78 MiB |
| 27 MP baseline 4:2:0 | 256 / 512 / 1920 | 149 / 154 / 189 MiB | 4 / 15 / 189 MiB | 4 / 6 / 74 MiB |
| 27 MP progressive 4:2:0 | 256 / 512 / 1920 | 228 / 233 / 268 MiB | 83 / 94 / 268 MiB | 83 / 84 / 153 MiB |

- **Baseline:** at 256 and 512 px the peak falls by 72–97%; time falls only 10–54% because Huffman decoding of every
  coefficient remains.
- **Progressive:** the full-size coefficient buffer (36 MB for 12 MP 4:2:0, 82 MB for 27 MP) is needed until the last
  scan, so the peak falls only by the planes and RGBA (40–64% at 256 px).
- **1920 px with gap 2** leaves no room for a 1/2 decode (4032 < 2 × 2 × 1920) and costs the same as `Load` +
  `Thumbnail`; gap 1 decodes at 1/2 and saves 45–60% of the peak.
- The reduced IDCTs stay scalar: they do not show in the profile next to entropy decoding.

## Limits and cancellation in 0.5.3

Cancellation checks run per row, block, MCU row, macroblock row, frame or encoding stage (see [Limits and
cancellation](limits-and-cancellation.md)), never per pixel. Overhead against 0.5.2: same in-memory inputs, a photo at
2048×1536 and the `graphic` source scaled to 1600×1200, single-threaded codecs, median of 21 calls after 5 warm-ups,
three alternating runs per build, 2026-10-15.

All 20 encode/decode operations stayed within the run-to-run spread (median changes −5.0% to +2.4%, no repeatable
regression) with unchanged allocations; a multithreaded run, resize, Gaussian blur and grayscale showed the same.

Reading input now allocates the encoded bytes once. A 37.7 MB BMP (4096×3072) loaded from a stream allocated, besides
the 50 MB of pixels:

| Stream | 0.5.2 | 0.5.3 |
| --- | ---: | ---: |
| Seekable (`MemoryStream` without an exposed buffer, `BufferedStream`) | 2.00× input | 1.00× input |
| Non-seekable, 80 KiB reads | 3.22× input | 1.00× input |

A seekable stream is read into an exact-size array; a non-seekable one grows in pooled arrays (briefly up to about three
times the bytes read) and is copied once. Growth is bounded by `MaxInputBytes`, so an endless stream stops after
`MaxInputBytes + 1` bytes. `CancellationInFlightTests` cancel 1–9 MP operations after a quarter of their duration and
require them to end within 250 ms plus half that duration; all formats meet it.

## ICO implementation for 0.5.1

ICO wraps the existing PNG codec or an uncompressed DIB. `IcoBenchmarks` measures complete encode/decode of 16, 32 and
256 px synthetic RGBA icons with both encodings:

```bash
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- --filter '*IcoBenchmarks*'
```

Quick baseline (2026-10-15): median of seven batches of 50 calls after 50 warm-ups, in memory. Allocations come from
`GC.GetTotalAllocatedBytes(true)` across all threads.

| Size | Encoding | Encode (µs) | Decode (µs) | Encode bytes/op | Decode bytes/op |
| --- | --- | ---: | ---: | ---: | ---: |
| 16×16 | PNG | 172.84 | 13.69 | 414048 | 3168 |
| 16×16 | DIB | 6.43 | 6.74 | 1552 | 1424 |
| 256×256 | PNG | 1430.15 | 408.17 | 685133 | 270030 |
| 256×256 | DIB | 168.53 | 152.38 | 271024 | 262582 |

PNG includes the encoder's compression workspace.

## Format matrix and memory in 0.5.0

The 0.5.0 gate allowed WebP at most 3× the time of native libwebp 1.6.0 with SIMD. `MatrixBenchmarks` freezes six
inputs: corpus `photo` (256×192), `graphic` (200×150) and `photo_alpha` (200×160), `graphic` scaled 8× (1600×1200),
Kodak `kodim23` (768×512) and a local 4032×3024 photo. Each runs 20 default-option operations: WebP lossy/lossless
encode and decode (default and one thread), PNG, JPEG, BMP, GIF and PCX, Lanczos halving and bicubic doubling.
`AnimationBenchmarks` encodes and decodes 16-frame pans (384×320 opaque, 336×256 with alpha) as lossy and lossless WebP,
GIF and APNG. BenchmarkDotNet 0.15.2, 3 warmups, 9 iterations.

### WebP against libwebp 1.6.0

The reference is a C program linked with `libwebp.a` 1.6.0 calling `WebPEncode` (quality 75, method 4, as
`cwebp -q 75 -m 4`) and `WebPDecode` to RGBA from memory, median of 21 calls (7 for 12 MP). Neither side includes I/O or
process start-up; PictureSharp includes the returned `Image`. Single-threaded times are compared.

| Input | Operation | PictureSharp 1T (ms) | libwebp 1T (ms) | Ratio | PictureSharp default (ms) |
| --- | --- | ---: | ---: | ---: | ---: |
| photo 256×192 | lossy encode | 7.22 | 3.46 | 2.09× | 4.56 |
| photo 256×192 | lossy decode | 0.720 | 0.383 | 1.88× | 0.720 |
| photo 256×192 | lossless encode | 23.7 | 29.6 | 0.80× | 23.6 |
| photo 256×192 | lossless decode | 0.819 | 0.760 | 1.08× | 0.820 |
| graphic 200×150 | lossy encode | 5.91 | 2.91 | 2.03× | 3.98 |
| graphic 200×150 | lossy decode | 0.786 | 0.478 | 1.64× | 0.777 |
| graphic 200×150 | lossless encode | 1.53 | 1.07 | 1.44× | 1.52 |
| graphic 200×150 | lossless decode | 0.074 | 0.031 | 2.38× | 0.073 |
| photo_alpha 200×160 | lossy encode | 14.9 | 10.1 | 1.48× | 13.8 |
| photo_alpha 200×160 | lossy decode | 0.606 | 0.416 | 1.46× | 0.613 |
| photo_alpha 200×160 | lossless encode | 14.5 | 20.0 | 0.73× | 15.2 |
| photo_alpha 200×160 | lossless decode | 0.511 | 0.467 | 1.09× | 0.507 |
| graphic 8× 1600×1200 | lossy encode | 115.8 | 62.7 | 1.85× | 37.6 |
| graphic 8× 1600×1200 | lossy decode | 7.78¹ | 3.66 | 2.13× | 8.11 |
| graphic 8× 1600×1200 | lossless encode | 48.8 | 35.8 | 1.36× | 48.0 |
| graphic 8× 1600×1200 | lossless decode | 2.40¹ | 1.22 | 1.97× | 4.27 |
| kodim23 768×512 | lossy encode | 46.0 | 23.1 | 1.99× | 22.2 |
| kodim23 768×512 | lossy decode | 3.91 | 1.99 | 1.96× | 2.93 |
| kodim23 768×512 | lossless encode | 131.5 | 140.2 | 0.94× | 114.0 |
| kodim23 768×512 | lossless decode | 6.58 | 6.38 | 1.03× | 6.66 |
| photo 4032×3024 | lossy encode | 1530 | 741 | 2.07× | 358 |
| photo 4032×3024 | lossy decode | 135.9 | 90.3 | 1.51× | 83.9 |
| photo 4032×3024 | lossless encode | 3996 | 4298 | 0.93× | 2880 |
| photo 4032×3024 | lossless decode | 146.1 | 150.9 | 0.97× | 144.4 |

¹ After pooling the decoder buffers (median of 201 decodes after 200 warmups); before, a fresh 7.5 MB ARGB buffer per
decode gave 9.73 and 4.14 ms (3.41×).

Every operation is within 3×; the largest ratio is 2.38× for a 0.074 ms decode dominated by fixed per-call costs.

### Allocations

A 12-megapixel image holds 46.5 MiB of `Rgba32` pixels. Before 0.5.0 several codecs allocated temporary buffers of that
size per call, and the WebP animation encoder about 1 GB for 16 frames of 384×320. In 0.5.0 temporary buffers are pooled
or avoided, with identical output: pooled VP8L histogram slabs (as libwebp's `VP8LAllocateHistogramSet`) and animation
subframes, pooled encoder output segments and decoder planes, and a pooled direct-mapped GIF color cache (a 12 MP still
GIF encodes 11% faster, 752 → 666 ms).

Allocated bytes per call with default options, on the 4032×3024 photo unless noted:

| Operation | 0.4.8 | 0.5.0 |
| --- | ---: | ---: |
| WebP lossless decode | 93.7 MB | 47.2 MB |
| WebP lossy decode | 65.2 MB | 47.7 MB |
| PNG decode | 96.8 MB | 48.9 MB |
| JPEG decode | 64.6 MB | 47.1 MB |
| GIF decode | 93.6 MB | 47.1 MB |
| PNG encode | 144 MB | 50 MB |
| BMP encode | 133 MB | 35.5 MB |
| PCX encode | 119 MB | 28.5 MB |
| GIF encode | 227 MB | 28.1 MB |
| GIF encode, 1024×768 photo | 61 MB | 7.4 MB |
| WebP lossless encode | 41.7 MB | 19.4 MB |
| WebP lossless encode, photo 256×192 | 8.4 MB | 1.4 MB |
| WebP lossless animation, 16 × 384×320 | 979 MB | 33 MB |
| WebP lossy animation, 16 × 384×320 | 41 MB | 14 MB |
| APNG encode, 16 × 384×320 | 42 MB | 18 MB |

`AllocationTests` keep budgets for these paths. Times stayed within noise or improved (BMP encode −31%, PCX −15%,
lossless animation −9%); the single-threaded 12 MP lossless encode measured +3% (3866 → 3996 ms), not repeated.

## ARM64 in 0.4.8

Before 0.4.8 hand-written vector paths were x86-only. 0.4.8 runs the 128-bit SSE bodies on AdvSimd through the
`Internal/Simd` helpers and adds ARM64 versions of the AVX2-only routines (VP8 loop filters, distortion and
quantization, JPEG DCTs) plus CRC-32 and Adler-32. Output is identical to scalar and x64.

Orange Pi 5 (Cortex-A76 cores 4–7, `performance` governor), Ubuntu 24.04, .NET SDK 10.0.112,
`DOTNET_TieredCompilation=0`, median of seven calls after two warmups through the public API from memory; "before" is
0.4.7. Inputs: a local 4032×3024 photo and a 1200×900 photo.

| Operation | 4032×3024 before | after | change | 1200×900 before | after | change |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| WebP lossy encode (q75, m4) | 3597 ms | 2103 ms | −42% | 360.5 ms | 212.8 ms | −41% |
| WebP lossy decode | 392.4 ms | 197.8 ms | −50% | 35.3 ms | 23.1 ms | −35% |
| WebP lossless encode (m4) | 11323 ms | 9264 ms | −18% | 1042 ms | 818 ms | −21% |
| WebP lossless decode | 532.5 ms | 436.2 ms | −18% | 53.0 ms | 40.9 ms | −23% |
| JPEG encode (q75) | 173.6 ms | 160.3 ms | −8% | 19.1 ms | 17.8 ms | −7% |
| JPEG decode | 223.2 ms | 161.5 ms | −28% | 22.9 ms | 15.7 ms | −31% |
| PNG encode | 1100 ms | 1052 ms | −4% | 120.0 ms | 117.8 ms | −2% |
| PNG decode | 245.6 ms | 245.2 ms | 0% | 20.1 ms | 20.1 ms | 0% |
| Lanczos ½ resize | 118.0 ms | 121.4 ms | +3% | 11.6 ms | 11.7 ms | +1% |

PNG and resizing already used portable vectors and did not change; PNG encoding is dominated by the scalar deflate match
finder. On the same cores libwebp 1.6.0 (NEON, one thread) took 2.01 s for `cwebp -q 75 -m 4` and 205 ms for `dwebp`
(internal timers), so PictureSharp ran at 1.05× and 0.96×. x64 showed no change beyond ±1.5%.

## Hot paths in 0.4.6

The 0.4.6 matrix freezes 40 operations on fixed bytes: PNG decoding (corpus sources, a palette graphic at 1600×1200,
local photos at 1920×1440 and 4000×3000), PNG encoding at levels 1, 6 and 9, resizing with every kernel, JPEG decoding
and encoding (baseline, progressive, 4:2:0 and 4:4:4), WebP lossy, BMP and GIF. Every output is identical to 0.4.5, also
with AVX2 or all intrinsics disabled.

- **Deflate:** a faster match finder, and inputs of 256 KiB and more parsed in parallel 64 KiB speculative segments
  joined to the exact sequential parse, so output does not depend on threads.
- **Inflate:** a register-resident bit buffer with 64-bit refills and 8-byte match copies; PNG unfilters rows on another
  thread as deflate blocks complete.
- **Checksums:** CRC-32 with PCLMULQDQ and Adler-32 with SSSE3, ported from Chromium's zlib (about 12× and 8× faster).
- **PNG filters:** the encoder costs all five filters in one `Vector128` pass and filters bands of rows in parallel;
  decoding uses per-pixel Sub/Average and a vector Paeth.
- **Resize:** both passes accumulate 32-bit sums with `Vector128<int>`; premultiplication runs in 16-bit lanes in
  parallel, and an opaque source is read in place.
- **JPEG:** parallel block preparation, AVX2 forward DCT and reciprocal quantization; an AVX2 inverse DCT for blocks
  that cannot overflow 32-bit lanes, and parallel color conversion.
- **WebP lossy encoding:** RGB→YUV conversion runs in parallel bands without dithering.

New options: `PngEncoderOptions.MaxDegreeOfParallelism` and `JpegEncoderOptions.MaxDegreeOfParallelism` (default -1).

Default-JIT medians, complete API calls from memory, 9 samples, two alternating batches, default parallelism (on one
thread PNG level 6 took 435 ms, JPEG decode 23 ms and JPEG q75 encode 49 ms). Pillow 10.2.0 single-threaded times are
given for context:

| Operation | 0.4.5 (ms) | 0.4.6 (ms) | Reduction | Pillow (ms) |
| --- | ---: | ---: | ---: | ---: |
| PNG decode 1920×1440 (PictureSharp file) | 54.70 / 53.28 | 31.33 / 31.15 | 42–43% | — |
| PNG decode 1920×1440 (Pillow file) | 68.77 / 67.28 | 33.47 / 33.51 | 50–51% | 50.5 |
| PNG decode 4000×3000 (Pillow file) | 296.97 / 285.82 | 135.63 / 136.28 | 52–54% | 227.7 |
| PNG decode photo 256×192 | 4.42 / 5.05 | 2.11 / 2.07 | 52–59% | — |
| PNG encode 1920×1440, level 1 | 302.22 / 302.08 | 62.14 / 61.96 | 79% | 142.1 |
| PNG encode 1920×1440, level 6 | 642.15 / 637.13 | 80.38 / 77.83 | 88% | 463.0 |
| PNG encode 1920×1440, level 9 | 1454.01 / 1427.78 | 177.06 / 175.61 | 88% | 930.6 |
| PNG encode 4000×3000, level 6 | 2804.71 / 2698.79 | 321.04 / 325.61 | 88% | 2055.2 |
| PNG encode graphic 1600×1200, level 6 | 20.95 / 21.27 | 8.11 / 8.23 | 61% | — |
| Resize 1920×1440 → 800×600, Lanczos | 11.70 / 15.15 | 5.16 / 4.04 | 56–73% | 23.5 |
| Resize 4000×3000 → 1920×1440, Lanczos | 43.68 / 43.15 | 16.80 / 16.87 | 61% | 106.2¹ |
| Resize 4000×3000 → 400×300, box | 20.06 / 19.92 | 5.08 / 5.19 | 74–75% | — |
| JPEG decode 1920×1440 | 34.12 / 31.12 | 16.86 / 18.46 | 41–51% | 10.5 |
| JPEG decode 1920×1440, progressive | 58.98 / 53.31 | 36.33 / 38.07 | 29–38% | 28.7 |
| JPEG decode 4032×3024 | 122.71 / 124.92 | 67.69 / 71.06 | 43–45% | 52.4 |
| JPEG encode 1920×1440, q75 4:2:0 | 71.61 / 72.64 | 15.49 / 15.54 | 78–79% | 6.7 |
| JPEG encode 1920×1440, q90 4:4:4 | 123.58 / 121.53 | 25.29 / 25.45 | 79–80% | — |
| JPEG encode 1920×1440, progressive | 157.99 / 156.72 | 45.36 / 46.76 | 70–71% | — |

¹ Pillow resized the 4032×3024 JPEG photo, not the identical 4000×3000 source.

WebP lossy decoding and encoding stayed within noise apart from the parallel color conversion (2–3%); BMP and GIF are
unchanged. PNG allocations dropped (1920×1440 encode 93 → 45 MiB, decode 42 → 34 MiB, 4000×3000 encode 374 → 182 MiB);
JPEG encoding allocates about 0.7 MiB more at 1920×1440. The remaining gaps to libjpeg-turbo are sequential Huffman
coding and, on one thread, the JPEG encoder's color conversion and downsampling; the single-threaded PNG encoder now
matches Pillow.

## VP8L encoding in 0.4.5

The 0.4.5 encode matrix uses the corpus sources `photo`, `graphic`, `photo_alpha`, `graphic_alpha`, `odd_alpha` and
`wide` at methods 0, 4, 5 (quality 75) and 6 (quality 100), `graphic.png` scaled to 1600×1200, and two local photos
(1920×1440, 4000×3000). All 35 outputs are byte-identical to 0.4.4 with every SIMD level; the 1920×1440 method-4 VP8L
chunk matches `cwebp` 1.6.0. All changes use integer arithmetic, so summation order never alters a cost:

- **Predictor search:** unmodified tile rows use ports of `PredictorSub0..13` (SSE2/AVX2); histogram entropy terms are
  updated incrementally, and `CombinedShannonEntropy` visits only non-zero entries.
- **Cross-color search:** `CollectColorRedTransforms`/`CollectColorBlueTransforms` ported from SSE2/AVX2; the
  accumulated entropy is computed once per tile.
- **Histogram clustering:** vectorized `PopulationCost`, a 4096-entry exact `SLog` table, and parallel tile remapping.
- **Hash chain:** parallel speculative segments joined into the exact sequential result (sequential speed equals
  libwebp's, so the gain comes from threads).
- **Other:** the cache-size estimate counts literals once; the bit writer ORs one 64-bit word per write.

Default-JIT medians of complete `Image.ToWebP` calls from memory (7 samples for 0.4.4, 5 for 0.4.5). `cwebp -v` 1.6.0
reports its internal single-threaded time (minimum of three):

| Input | 0.4.4, batches 1 / 2 (ms) | 0.4.5 (ms) | Reduction | cwebp internal (ms) |
| --- | ---: | ---: | ---: | ---: |
| Photo 256×192, m4 | 66.05 / 62.73 | 25.01 | 60–62% | 37 |
| Photo with alpha 200×160, m4 | 40.21 / 38.34 | 15.94 | 58–60% | 22 |
| Graphic 1600×1200, m4 | 54.01 / 53.30 | 52.55 | 1–3% | 38 |
| Graphic 1600×1200, m5 | 464.46 / 453.45 | 192.96 | 57–58% | 223 |
| Photo 1920×1440, m0 | 388.50 / 387.94 | 129.56 | 67% | 275 |
| Photo 1920×1440, m4 | 1504.39 / 1480.83 | 731.68 | 51% | 960 |
| Photo 1920×1440, m4, one thread | 1513.13 | 993.16 | 34% | 960 |
| Photo 1920×1440, m5 | 2016.94 | 893.11 | 56% | 1313 |
| Photo 1920×1440, m6 q100 | 3705.44 | 2163.04 | 42% | — |
| Photo 4000×3000, m4 | 7800.96 | 3264.28 | 58% | 4820 |

Small corpus sources at methods 4 and 5 became 50–72% faster. The 1600×1200 graphic at m4 (backward-reference tracing)
takes about 1.4× native time; the other inputs are at or below `cwebp`. Allocations did not grow.

## VP8L decoding in 0.4.4

The 0.4.4 decode matrix contains seven committed VP8L inputs (`photo_ll_z6`, `photo_ll_q0m0`, `graphic_ll_z6`,
`photo_alpha_ll_z6`, `graphic_alpha_ll_z6`, `odd_alpha_ll_z9`, `wide_ll_z6`) and the two local photos from 0.4.3.
Acceptance required a repeatable gain of at least 3% on a large input in two batches, no repeatable regression above 3%
and unchanged allocations.

- The entropy loop reuses prefetched bits for the second Huffman lookup, and bit reads refill a 32-bit word through an
  inlined fast path.
- Backward references with distance 1 or 2 use span fills, as libwebp's small-pattern copy; bounds checks stay before
  copying.
- Predictors 5–7 and 13 use SSE2 across channels while carrying the left pixel sequentially; mode 13 corrects negative
  odd differences to keep C's division toward zero.

Fixed-JIT medians, 40 warmups, 21 samples, two serial batches:

| Input | 0.4.3 medians, batches 1 / 2 (ms) | 0.4.4 medians, batches 1 / 2 (ms) | Reduction |
| --- | ---: | ---: | ---: |
| Photo, z6 | 1.091 / 1.124 | 0.943 / 0.976 | 13.5% / 13.1% |
| Photo with alpha | 0.671 / 0.682 | 0.578 / 0.595 | 13.9% / 12.8% |
| Photo, 1920×1440 | 55.24 / 56.38 | 51.77 / 50.35 | 6.3% / 10.7% |
| Photo, 4000×3000 | 210.63 / 204.41 | 190.41 / 190.43 | 9.6% / 6.8% |

The other corpus inputs gained 2.7–23.4%, except graphic z6 (−3.0% / −0.6%, not repeatable). Allocations were unchanged
(about 21.6 / 92.7 MiB for the large inputs). A default-JIT batch measured 49.19 → 45.34 ms (7.8%) and 187.63 → 169.17
ms (9.8%) for the large inputs; do not mix the two JIT modes.

The new SSE2 predictors (BenchmarkDotNet, fixed JIT, zero allocations) were 2.02–2.26× (mode 5), 1.86–3.08× (6),
1.84–3.01× (7) and 3.60–3.89× (13) faster than scalar across tile bits 2/3/5.

## VP8L predictors in 0.4.3

Release 0.4.3 adds SSE2 and AVX2 reconstruction for lossless WebP predictor modes 10, 11 and 12, ported from libwebp
1.6.0. Each pixel depends on its reconstructed left neighbour: AVX2 prepares eight pixels and carries the dependency
through both 128-bit halves. Results match the scalar code exactly.

### Predictor measurements

BenchmarkDotNet 0.15.2 with tiered compilation disabled, on a deterministic 1920×1440 residual array (seed 43), for tile
sizes 4/8/32: mode 10 was 1.75–2.15× faster than scalar, mode 11 11.47–11.84×, mode 12 6.86–8.36× and mixed modes
7.13–7.69×, with zero allocations; SSE2 alone also beat scalar. These are transform timings; files using other
predictors or dominated by Huffman decoding gain less.

### Complete decoding

`Image.Load` from memory of lossless WebP files encoded once from a local photo (`Exact=true`, method 4, quality 75),
default JIT, 8 warmups, 21 calls, medians of two batches:

| Image | Before, batches 1 / 2 | After, batches 1 / 2 | Reduction |
| --- | ---: | ---: | ---: |
| 1920×1440 | 49.22 / 53.75 ms | 48.48 / 49.92 ms | 1.5% / 7.1% |
| 4000×3000 | 208.38 / 206.44 ms | 193.72 / 186.66 ms | 7.0% / 9.6% |

The smaller-image gain is modest (its percentile intervals overlap). Corpus inputs showed no repeatable regression above
3%, and allocations were unchanged. On the same bytes `dwebp -v` 1.6.0 reported 47 and 190 ms internally, so the
PictureSharp API took about 1.14× / 1.13× the native decode time.
