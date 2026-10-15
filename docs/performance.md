# Performance

## Phase 10 measurement contract

Before accepting an optimization, compare the same encoded bytes and options with the preceding
release on the same CPU/runtime/GC configuration. Record median, spread and allocated bytes for
the complete operation; microbenchmarks explain a change but do not establish an end-to-end gain.
Run performance jobs serially. Keep private photographs and raw benchmark artifacts out of Git.

The 0.4.4 decode matrix contains seven committed static VP8L inputs:
`photo_ll_z6`, `photo_ll_q0m0`, `graphic_ll_z6`, `photo_alpha_ll_z6`,
`graphic_alpha_ll_z6`, `odd_alpha_ll_z9`, and `wide_ll_z6` (all `.webp`).
It covers photo/graphic content, alpha, different encoder effort, odd dimensions and a wide row.
Two optional local photo inputs (1920×1440 and 4000×3000) reuse the exact bytes from 0.4.3.
Predictor microbenchmarks cover modes 5–7 and 13 as well as 10–12 and mixed 10–12,
with tile bits 2/3/5, fixed seed 43 and a 1920×1440 residual buffer.

For 0.4.4, require a repeatable full-decode median improvement of at least 3% on a large input
in two independent batches, no repeatable regression above 3% elsewhere in this matrix,
and unchanged allocations (apart from measurement noise). Investigate overlapping distributions
with longer runs before drawing a conclusion. The comparison probe uses 40 warmups and 21 samples;
small inputs use batches of 32 decodes per sample. Report fixed-JIT (`DOTNET_TieredCompilation=0`)
and default-JIT results separately. BenchmarkDotNet remains the reproducible benchmark entry point.
Validate scalar and SSE2 fallbacks separately from the default AVX2 path.

The final phase-10 matrix must additionally cover encoding/decoding of WebP lossy, PNG, BMP,
JPEG, GIF and PCX, animations, and processing (especially resize), with small and large images.
Freeze the specific corpus files/options before each corresponding optimization stage. ARM64
measurements require real hardware. For the final WebP gate, require at most 3× the native SIMD
time on the frozen matrix using libwebp 1.6.0; disclose process startup, I/O and API timing differences.
The final matrix and gate were completed in 0.5.0 (next section).

The 0.4.6 matrix (PNG, deflate, resizing, JPEG, WebP lossy, BMP and GIF) and the 0.4.5 lossless encode matrix
are described in later sections.

## Phase-10 matrix and memory in 0.5.0

`MatrixBenchmarks` freezes six inputs: the corpus sources `photo` (256×192), `graphic` (200×150) and `photo_alpha`
(200×160), `graphic` scaled 8× with nearest neighbour (1600×1200), Kodak `kodim23` (768×512, external corpus) and a
local 4032×3024 camera photo (through `PICTURESHARP_BENCHMARK_MATRIX_FILES`). Each input runs 20 operations with default
options: WebP lossy and lossless encoding and decoding (default threads and one thread), PNG, JPEG, BMP, GIF and PCX
encoding and decoding, Lanczos halving and bicubic doubling. `AnimationBenchmarks` encodes and decodes 16-frame pans
(384×320 opaque and 336×256 with alpha, derived from `photo` and `photo_alpha`) as lossy and lossless WebP, GIF and
APNG. Decode operations read bytes written once during setup. Measured on the i5-12500H, Linux x64, .NET 10.0.12,
BenchmarkDotNet 0.15.2 (default JIT, one launch, 3 warmups, 9 iterations).

### WebP against libwebp 1.6.0

The native reference is a small C program linked with `libwebp.a` 1.6.0 that calls `WebPEncode` (quality 75,
method 4, lossy or lossless, the same bytes as `cwebp -q 75 -m 4`) and `WebPDecode` to RGBA from memory, median of 21
calls (7 for the 12 MP photo). Both sides exclude file I/O and process startup; PictureSharp's time includes the
`Image` it returns. Single-threaded times are compared; PictureSharp's default (all cores) is shown for context.

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

¹ After the pooled decoder buffers described below (median of 201 single-threaded decodes after 200 warmups); the
BenchmarkDotNet run before that change measured 9.73 ms and 4.14 ms (3.41×), the time going into a fresh 7.5 MB
ARGB buffer per decode. The other decode rows predate that change and are not slower after it.

Every operation is within the 3× target; the largest ratio is 2.38× for the 200×150 lossless decode (0.074 ms, where
fixed per-call costs dominate). The animation encoders have no native counterpart in this harness.

### Allocations

A 12-megapixel image holds 46.5 MiB of `Rgba32` pixels. Before 0.5.0 several encoders and decoders allocated
temporary buffers of similar size per call, and the WebP animation encoder allocated about 1 GB for a 16-frame
384×320 animation. In 0.5.0 the temporary buffers come from `ArrayPool<T>.Shared` or are avoided, with identical
output bytes and pixels:

- VP8L encoder: histograms share one pooled slab per histogram set (as libwebp's `VP8LAllocateHistogramSet`) instead
  of five arrays each; the animation encoder reuses one subframe for all candidates; the alpha plane is encoded from a
  pooled ARGB buffer.
- `Image.Encode` and the `To*` methods write into pooled segments and copy once into the exact-size result; the
  deflate output buffer is pooled and PNG writes IDAT from it.
- GIF encoding of a still image skips the rendered and the empty canvas, pools the quantizer input, and the median
  cut maps colors through a pooled direct-mapped cache sized to the image (2^12–2^20 entries) instead of a dictionary of
  every distinct color; a 12 MP still image encodes about 11% faster (752 → 666 ms).
- Decoders keep VP8L ARGB pixels, PNG compressed and inflated data, JPEG planes and coefficients, and VP8 YUV planes
  in pooled buffers until the RGBA conversion; the last GIF frame takes the canvas instead of a copy.

Allocated bytes per call with default options (BenchmarkDotNet or the runtime's allocation counter), on the
4032×3024 photo unless noted:

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

`AllocationTests` keep budgets for these paths. Times stayed within run-to-run variation or improved (BMP encoding
−31%, PCX −15%, lossless animation −9% at 12 MP and 384×320); the single-threaded 12 MP lossless encode measured +3%
(3866 → 3996 ms), at the edge of the tolerance and not repeated.


## ARM64 in 0.4.8

Before 0.4.8 every hand-written vector path was x86-only, so ARM64 ran the scalar code except for the routines
written with portable `Vector128` operations (PNG filters, resizing, premultiplication, VP8L histograms and entropy,
JPEG coefficient masks). 0.4.8 runs the 128-bit SSE bodies on AdvSimd through the `Internal/Simd` helpers and adds
ARM64 versions of the AVX2-only routines (VP8 loop filters, distortion and quantization, JPEG DCTs) plus CRC-32 and
Adler-32. Every output is identical to the scalar code and to x64.

Measured on 2026-10-14: Orange Pi 5 (RK3588, Cortex-A76 cores 4–7 via `taskset -c 4-7`, `performance` governor),
Ubuntu 24.04, .NET SDK 10.0.112, `DOTNET_TieredCompilation=0`. Each value is the median of seven calls after two
warmups, through the public API from memory (encode from a decoded `Image`, decode from bytes written with default
options); "before" is the 0.4.7 commit built the same way. Inputs: a local 4032×3024 photo and a 1200×900 photo.

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

PNG and resizing already used portable vectors on ARM64; their code did not change in 0.4.8, so the small
differences are run-to-run variation. PNG encoding time is dominated by the scalar deflate match finder.

On the same board and cores, libwebp 1.6.0 (NEON, single thread) reported 2.01 s for `cwebp -q 75 -m 4` of the
4032×3024 photo and 205 ms for `dwebp` of the resulting file (internal timers, three runs each; they exclude file
I/O and process startup). PictureSharp's API times above are 1.05× and 0.96× of those, well within the 3× phase-10
target for these inputs; the frozen phase-10 matrix still has to confirm it. On x64 (i5-12500H) the same probe
showed no change beyond run-to-run variation (within ±1.5%) for WebP, JPEG and lossless decoding.

## Hot paths in 0.4.6

The 0.4.6 matrix freezes 40 operations on fixed bytes: PNG decoding of the corpus `photo` and `photo_alpha`
sources, a palette graphic scaled 8× (1600×1200), and local photos at 1920×1440 and 4000×3000 (written by
PictureSharp 0.4.5 and by Pillow 10.2.0); PNG encoding at levels 1, 6 and 9; resizing with every kernel, down and
up, opaque and with alpha; JPEG decoding (Pillow quality 85, baseline and progressive, and a 4032×3024 camera
file) and encoding (4:2:0 q75, 4:4:4 q90, progressive with optimized tables); WebP lossy decoding and encoding;
BMP and GIF encoding. Every output (encoded bytes or decoded/resized pixels) is identical to 0.4.5, also with
AVX2 disabled and with all hardware intrinsics disabled. Acceptance followed the phase-10 contract.

Changes:

- **Deflate:** the match finder compares two-byte pairs at the start and at the current best length before a full
  comparison, measures matches eight bytes at a time and maps distances to codes through zlib's table; the bit
  writer reserves space per block. Inputs of 256 KiB and more are parsed in parallel 64 KiB segments: the state at
  a search that starts without a pending match is only the position (every earlier position is in the hash chains
  and the chains reach back 32 KiB), so a segment parsed speculatively from its first byte agrees with the true
  parse from the first such position they share. The join replays the true parse until it meets the segment and
  copies the rest, while the next wave of segments is being parsed. The compressed bytes do not depend on threads.
- **Inflate:** a fast loop keeps the bit buffer in registers, refills 64 bits at once and copies matches in 8-byte
  words while enough input and output remain; the exact-size output buffer is returned without a copy. PNG
  decoding is pipelined: another thread unfilters (in a copy of each row) and converts rows as soon as each deflate
  block completes.
- **Checksums:** CRC-32 folds 64 bytes per step with PCLMULQDQ and Adler-32 uses SSSE3, ported from Chromium's
  zlib (about 12× and 8× faster on 8 MB).
- **PNG filters:** the encoder computes the cost of all five filters in one `Vector128` pass and writes only the
  chosen one; bands of rows are filtered in parallel (each band packs the row above it). Decoding uses per-pixel
  Sub/Average for 3 and 4 bytes per pixel (byte-wise addition inside a `uint`) and a vector Paeth per pixel.
- **Resize:** both passes accumulate the same 32-bit sums with `Vector128<int>` (the vertical pass keeps 16 output
  bytes in registers); premultiplication runs in 16-bit lanes and in parallel, and an opaque source is read in place.
- **JPEG:** the encoder converts colors once, prepares rows of blocks in parallel, runs the forward DCT with AVX2 on
  transposed 8×8 blocks, quantizes by exact reciprocal multiplication, stores coefficients in zigzag order and
  walks non-zero coefficients through bit masks. The decoder runs the inverse DCT with AVX2 in 32-bit lanes when a
  block's dequantized inputs and first-pass results stay below 12000 (no intermediate can then overflow; other
  blocks use the 64-bit scalar code), and converts colors and progressive blocks in parallel.
- **WebP lossy encoding:** RGB→YUV conversion runs in parallel bands when no dithering is requested.

New options: `PngEncoderOptions.MaxDegreeOfParallelism` and `JpegEncoderOptions.MaxDegreeOfParallelism`
(default -1, automatic), as for WebP.

Default-JIT medians on the Intel Core i5-12500H, Linux x64, .NET 10.0.12, workstation GC, default parallelism,
complete API calls from memory, 9 samples, two alternating batches each (pixel copies of decoded images are included
in both versions). Pillow 10.2.0 (zlib 1.3, libjpeg-turbo) times are single-threaded medians of the same operation
from Python, for context:

| Operation | 0.4.5 (ms) | 0.4.6 (ms) | Reduction | Pillow (ms) |
| --- | ---: | ---: | ---: | ---: |
| PNG decode 1920×1440 (PictureSharp file) | 54.70 / 53.28 | 31.33 / 31.15 | 42–43% | — |
| PNG decode 1920×1440 (Pillow file) | 68.77 / 67.28 | 33.47 / 33.51 | 50–51% | 50.5 |
| PNG decode 4000×3000 (Pillow file) | 296.97 / 285.82 | 135.63 / 136.28 | 52–54% | 227.7 |
| PNG decode photo 256×192 | 4.42 / 5.05 | 2.11 / 2.07 | 52–59% | — |
| PNG encode 1920×1440, level 1 | 302.22 / 302.08 | 62.14 / 61.96 | 79% | 142.1 |
| PNG encode 1920×1440, level 6 | 642.15 / 637.13 | 80.38 / 77.83 | 88% | 463.0 |
| PNG encode 1920×1440, level 6, one thread | 642.15 / 637.13 | 435.14 / 434.05 | 32% | 463.0 |
| PNG encode 1920×1440, level 9 | 1454.01 / 1427.78 | 177.06 / 175.61 | 88% | 930.6 |
| PNG encode 4000×3000, level 6 | 2804.71 / 2698.79 | 321.04 / 325.61 | 88% | 2055.2 |
| PNG encode graphic 1600×1200, level 6 | 20.95 / 21.27 | 8.11 / 8.23 | 61% | — |
| Resize 1920×1440 → 800×600, Lanczos | 11.70 / 15.15 | 5.16 / 4.04 | 56–73% | 23.5 |
| Resize 1920×1440 → 800×600, bilinear | 8.26 / 10.29 | 2.68 / 3.02 | 68–71% | — |
| Resize 4000×3000 → 1920×1440, Lanczos | 43.68 / 43.15 | 16.80 / 16.87 | 61% | 106.2¹ |
| Resize 4000×3000 → 400×300, box | 20.06 / 19.92 | 5.08 / 5.19 | 74–75% | — |
| Resize 200×160 with alpha → 800×640, bicubic | 4.75 / 4.59 | 2.72 / 2.81 | 39–43% | — |
| JPEG decode 1920×1440 | 34.12 / 31.12 | 16.86 / 18.46 | 41–51% | 10.5 |
| JPEG decode 1920×1440, one thread | 34.12 / 31.12 | 23.33 / 23.50 | 25–32% | 10.5 |
| JPEG decode 1920×1440, progressive | 58.98 / 53.31 | 36.33 / 38.07 | 29–38% | 28.7 |
| JPEG decode 4032×3024 | 122.71 / 124.92 | 67.69 / 71.06 | 43–45% | 52.4 |
| JPEG encode 1920×1440, q75 4:2:0 | 71.61 / 72.64 | 15.49 / 15.54 | 78–79% | 6.7 |
| JPEG encode 1920×1440, q75, one thread | 71.61 / 72.64 | 49.48 / 48.52 | 31–33% | 6.7 |
| JPEG encode 1920×1440, q90 4:4:4 | 123.58 / 121.53 | 25.29 / 25.45 | 79–80% | — |
| JPEG encode 1920×1440, progressive | 157.99 / 156.72 | 45.36 / 46.76 | 70–71% | — |

¹ Pillow resized the 4032×3024 JPEG photo, not the identical 4000×3000 source.

WebP lossy decoding (1920×1440: 32.1 ms, `dwebp -v` 30 ms; 4032×3024: 83 ms, `dwebp` 88 ms) and encoding (1920×1440
q75: 135–139 ms with threads, 442–450 ms on one thread; `cwebp -v` 200 ms) stayed within noise of 0.4.5 apart from
the parallel color conversion (2–3%); their DSP functions already had SIMD since 0.4.1. BMP and GIF encoding are
unchanged (GIF quantization of a 256×192 photo still takes about 80 ms and remains open).

Allocations dropped for PNG (1920×1440 encode 93 → 45 MiB, decode 42 → 34 MiB, 4000×3000 encode 374 → 182 MiB)
and stayed equal for resizing and JPEG decoding. JPEG encoding allocates about 0.7 MiB more at 1920×1440
(2.0 → 2.7 MiB; parallel work items and quantization divisors; the color planes are pooled).
The remaining gaps to libjpeg-turbo are the sequential Huffman coding (JPEG decode and encode) and, on one thread,
the color conversion and downsampling of the JPEG encoder; the single-threaded PNG encoder is now at Pillow's speed.

`CodecBenchmarks` (BenchmarkDotNet) covers PNG, JPEG and lossy WebP (default options) encoding and decoding and Lanczos halving of three corpus
sources; add local images through `PICTURESHARP_BENCHMARK_CODEC_FILES`.

## VP8L encoding in 0.4.5

The 0.4.5 encode matrix uses the committed corpus sources `photo`, `graphic`, `photo_alpha`, `graphic_alpha`,
`odd_alpha` and `wide` (`tests/assets/corpus/src/*.png`) at methods 0, 4 and 5 (quality 75) and method 6
(quality 100); `graphic.png` scaled 8× with nearest-neighbour sampling to 1600×1200; and the two local photos
decoded from the exact 0.4.3/0.4.4 benchmark files (1920×1440 at methods 0/4/5/6, 4000×3000 at method 4).
Every output (35 encodes, including single-threaded variants) is byte-identical to 0.4.4, with AVX2 disabled
and with all hardware intrinsics disabled as well; the VP8L chunk of the 1920×1440 method-4 output matches
`cwebp` 1.6.0. Acceptance followed the phase-10 contract: a repeatable gain on large inputs, no repeatable
regression above 3%, and no allocation growth.

Changes, all in integer arithmetic, so the order of summation never alters a cost:

- **Predictor search:** residuals of a tile row use ports of `PredictorSub0..13` (SSE2/AVX2) whenever the row is
  not modified (exact mode, or no near-lossless and no fully transparent pixel in the run). The entropy terms of
  the accumulated histograms are kept up to date incrementally, and `CombinedShannonEntropy` only visits the
  non-zero entries of the tile histogram (found with a vector mask).
- **Cross-color search:** `CollectColorRedTransforms`/`CollectColorBlueTransforms` are ported from SSE2/AVX2; the
  accumulated histogram's entropy terms are computed once per tile instead of for every candidate multiplier.
- **Histogram clustering:** `PopulationCost` of two histograms gathers sum, maximum and zeros with vectors and
  finds streak boundaries eight positions at a time; `SLog` uses a 4096-entry table of the exact fixed-point
  values. Remapping tiles to clusters runs in parallel (each tile is independent).
- **Hash chain:** the match search runs in parallel segments. Each segment searches speculatively from its top
  and records where searches started; a sequential pass then follows the true order and, as soon as it reaches a
  recorded start, reuses the rest of that segment, because a search result depends only on the position. The
  result equals the sequential one. A native harness linked against libwebp 1.6.0 gave the same matches with
  equal sequential speed (86 vs 89 ms at 1920×1440, 997 vs 975 ms at 4000×3000, raw pixels), so the remaining
  gain comes from threads. When several crunch configurations already run in parallel (methods 5–6), the
  search inside each stays sequential to avoid extra buffers.
- **Other:** the cache-size estimate counts literals once and subtracts each size's hits; the bit writer ORs one
  64-bit word per write.

Default-JIT medians on the Intel Core i5-12500H, Linux x64, .NET 10.0.12, workstation GC, default
`MaxDegreeOfParallelism` (complete `Image.ToWebP` API calls from memory; 7 samples for 0.4.4, 5 for 0.4.5;
batch 2 of 0.4.4 covers the inputs it lists). `cwebp -v` 1.6.0 reports its internal single-threaded encode time
(minimum of three runs), excluding process start, PNG reading and file writing:

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

`cwebp` needed about 7.75 s of wall time for the 1920×1440 m6 q100 encode (process time, not comparable).
On the small corpus sources, methods 4 and 5 of the photo, alpha, odd-size and wide images became 50–72% faster;
the small graphic encodes at methods 0 and 4 (0.2–1.7 ms) stayed within measurement noise. The 1600×1200 graphic
at m4 is dominated by backward-reference tracing of long runs and takes about 1.4× the native time; the
single-threaded 1920×1440 m4 encode takes 1.03×, and the other listed inputs are faster than `cwebp`.

Allocations per encode stayed equal or decreased (photo m4 7.73 → 7.72 MiB, 1920×1440 m4 23.36 → 23.35 MiB,
4000×3000 m4 68.99 → 68.99 MiB). Method-6 allocations vary between runs with thread-pool reuse of rented buffers
(84–110 MiB for 0.4.4 at 1920×1440; 82 MiB in the final 0.4.5 run).

A fixed-JIT batch (`DOTNET_TieredCompilation=0`, 5 samples) measured the build before the last two changes (buffer
pooling and sequential search inside parallel configurations, which do not affect these single-configuration
encodes): photo m4 58.22 → 26.35 ms, photo with alpha m4 38.97 → 16.91 ms, graphic m4 53.99 → 53.94 ms,
1920×1440 m0 427.15 → 137.62 ms and m4 1603.29 → 738.87 ms.

`LosslessEncodeBenchmarks` covers the six corpus sources at methods 0, 4 and 5; add local images through
`PICTURESHARP_BENCHMARK_ENCODE_FILES` (same path separator rules as the decode benchmark).

## VP8L decoding in 0.4.4

The entropy loop now reuses the prefetched bits for the second Huffman table lookup and accesses
table entries by reference. Bit reads refill a 32-bit word through the inlined fast path instead of
shifting each consumed byte. The guarded byte-at-a-time tail remains in place. Backward references
with distance 1 or 2 use span fills for repeating pixels/pairs, following libwebp's small-pattern copy.
All backward-reference bounds checks remain before copying.

Predictors 5–7 and 13 now use SSE2 across color channels while carrying the reconstructed left pixel
sequentially. Mode 13 corrects negative odd differences before arithmetic shifting to preserve C's
division toward zero. These paths also run on AVX2 CPUs; no separate AVX2 version is needed for
this implementation. Short runs and CPUs without SSE2 retain scalar code.

Diagnostic stage timing on the baseline attributed approximately 46 ms entropy / 7 ms inverse
transforms at 1920×1440 and 164 ms / 35 ms at 4000×3000. This was temporary coarse instrumentation,
not a sampling profile of individual Huffman or bit-reader instructions, and is not used for speedup claims.

Fixed-JIT before/after measurements on the Intel Core i5-12500H, Linux x64, .NET SDK 10.0.112/runtime 10.0.12, workstation GC, and the input bytes described in the measurement contract
(40 warmups, 21 samples; two independent serial batches):

| Input | 0.4.3 medians, batches 1 / 2 (ms) | 0.4.4 medians, batches 1 / 2 (ms) | Reduction |
| --- | ---: | ---: | ---: |
| Photo, q0/m0 | 0.978 / 1.016 | 0.951 / 0.948 | 2.7% / 6.7% |
| Graphic with alpha | 0.0517 / 0.0496 | 0.0396 / 0.0392 | 23.4% / 21.1% |
| Odd dimensions with alpha | 0.1056 / 0.1025 | 0.0948 / 0.0964 | 10.2% / 5.9% |
| Wide row | 0.0507 / 0.0509 | 0.0474 / 0.0472 | 6.4% / 7.4% |
| Photo, z6 | 1.091 / 1.124 | 0.943 / 0.976 | 13.5% / 13.1% |
| Graphic, z6 | 0.0843 / 0.0898 | 0.0869 / 0.0903 | −3.0% / −0.6% |
| Photo with alpha | 0.671 / 0.682 | 0.578 / 0.595 | 13.9% / 12.8% |
| Photo, 1920×1440 | 55.24 / 56.38 | 51.77 / 50.35 | 6.3% / 10.7% |
| Photo, 4000×3000 | 210.63 / 204.41 | 190.41 / 190.43 | 9.6% / 6.8% |

The graphic z6 result is effectively unchanged: the small slowdown was not repeatable above 3%.
For the large inputs, batch-2 10th–90th percentile intervals were 51.78–57.71 → 46.69–53.36 ms
and 202.62–208.37 → 188.65–198.58 ms. Corpus allocations were exactly unchanged; large-input
allocations stayed approximately 21.6 / 92.7 MiB, with less than 256 bytes of per-call measurement noise.
These are full `Image.Load` API measurements from memory, not a comparison against native process time.

A separate default-JIT batch measured 49.19 → 45.34 ms (7.8%) and 187.63 → 169.17 ms (9.8%)
for the two large inputs. On the seven corpus inputs, changes ranged from a 0.4% slowdown to a
13.5% improvement; this batch did not show a regression above the 3% threshold. Do not mix these
numbers with the fixed-JIT table: tiered compilation materially changes timings.

An independent BenchmarkDotNet run of all nine decode inputs (fixed JIT, three warmups, eight
100-ms target iterations) measured 52.41 ± 2.38 ms / 21.63 MiB and 202.95 ± 2.48 ms / 92.67 MiB
for the large photos (mean ± standard deviation). GC scheduling and iteration setup differ from the
comparison probe, so these absolute measurements must not replace its paired before/after results.

BenchmarkDotNet 0.15.2 also ran all 48 predictor cases (eight mode settings × three tile sizes ×
scalar/automatic), with fixed JIT, five warmups and twelve measured single-invocation iterations.
New modes at tile bits 5 (mean ± standard deviation):

| Predictor | Scalar (ms) | Automatic SSE2 (ms) | Speedup |
| --- | ---: | ---: | ---: |
| 5 | 11.420 ± 0.250 | 5.059 ± 0.168 | 2.26× |
| 6 | 8.786 ± 0.280 | 2.857 ± 0.069 | 3.08× |
| 7 | 8.751 ± 0.172 | 2.909 ± 0.114 | 3.01× |
| 13 | 28.906 ± 0.292 | 8.026 ± 0.330 | 3.60× |

Across tile bits 2/3/5, speedups were 2.02–2.26×, 1.86–3.08×, 1.84–3.01× and 3.60–3.89×
respectively, with zero allocations. These short microbenchmarks measure reconstruction only;
BenchmarkDotNet warns about their short iteration time.

## VP8L predictors in 0.4.3

Release 0.4.3 adds SSE2 and AVX2 reconstruction for lossless WebP predictor modes 10 (nested averages), 11 (selection) and 12 (clamped addition/subtraction). The routines are ported from libwebp 1.6.0. Pixels still depend on the reconstructed pixel to their left: AVX2 prepares eight pixels, then carries that dependency through the two 128-bit halves. Short tails and CPUs without SSE2 use scalar code.

This release does not optimize Huffman decoding, encoder predictor search, PNG, resizing or ARM. Modes 5–7 and 13 remain scalar. Results must match the scalar implementation exactly.

### Predictor measurements

Measured on 2026-10-14: Intel Core i5-12500H, Linux x64, .NET SDK 10.0.112, runtime 10.0.12, BenchmarkDotNet 0.15.2, workstation GC. Tiered compilation was disabled for the BenchmarkDotNet runs so short, single-invocation iterations did not measure transitions between JIT tiers.

The input is a deterministic 1920×1440 array of residuals (seed 43). Buffers are reset outside the timed operation. Mixed mode cycles through 10, 11 and 12 across tiles. Numbers are mean ± standard deviation, in milliseconds, for 32-pixel tiles (`Bits=5`):

| Predictor | Scalar | Automatic SIMD | Speedup |
| --- | ---: | ---: | ---: |
| 10 | 11.122 ± 0.082 | 5.162 ± 0.075 | 2.15× |
| 11 | 73.534 ± 0.369 | 6.211 ± 0.130 | 11.84× |
| 12 | 35.502 ± 0.277 | 4.245 ± 0.080 | 8.36× |
| Mixed | 40.286 ± 0.309 | 5.236 ± 0.088 | 7.69× |

All 24 cases (four mode settings × three tile sizes × two implementations) were measured. For tile sizes 4/8/32, speedups were 1.75–2.15× for mode 10, 11.47–11.84× for mode 11, 6.86–8.36× for mode 12 and 7.13–7.69× for mixed modes. Both implementations allocated zero bytes during reconstruction. Separate runs with AVX2 disabled confirmed improvements over scalar for all three modes using SSE2.

These are transform timings, not complete image decoding timings. In particular, a file that uses other predictors or spends most of its time decoding Huffman symbols will gain less.

### Complete decoding

The before/after comparison used a saved copy of the initial 0.4.2 working-tree library, including its pre-existing changes, and the same encoded bytes after adding the new predictors. The large inputs are lossless WebP files generated once from a local photograph, with `Exact=true`, method 4 and quality 75. They are not committed. Each batch used eight warmups and 21 measured calls to `Image.Load` from a byte array, with default tiered compilation. The table shows medians from two independent batches:

| Image | Before, batches 1 / 2 | After, batches 1 / 2 | Reduction |
| --- | ---: | ---: | ---: |
| 1920×1440 | 49.22 / 53.75 ms | 48.48 / 49.92 ms | 1.5% / 7.1% |
| 4000×3000 | 208.38 / 206.44 ms | 193.72 / 186.66 ms | 7.0% / 9.6% |

For the second batch, the 10th–90th percentile intervals were 50.92–57.24 ms before and 46.90–52.44 ms after for 1920×1440, and 204.41–215.11 ms before and 183.07–189.45 ms after for 4000×3000. Treat the smaller-image improvement as modest: its intervals overlap. The photo, graphic and alpha-photo corpus inputs showed no repeatable regression above 3%. Allocations for full decoding were unchanged (about 21.6 MiB and 92.7 MiB per large image); predictor reconstruction itself adds no allocations.

A separate BenchmarkDotNet run with tiered compilation disabled measured the new decoder at 53.70 ± 0.26 ms / 21.63 MiB for 1920×1440 and 215.54 ± 0.62 ms / 92.67 MiB for 4000×3000. These figures include the complete library API and GC costs and should not be substituted for the default-JIT before/after measurements above.

On the same bytes, `dwebp -v` 1.6.0 reported median internal decode times of 47 ms and 190 ms (five warmups, 21 samples; 10th–90th percentiles 45–52 ms and 187–194 ms). That timer excludes file reading, process startup and output writing, has millisecond precision, and does not measure the same API overhead as PictureSharp. The BenchmarkDotNet/API times are about 1.14× / 1.13× those native internal times on these two inputs; this is context, not a guarantee for other files or machines.

## Running the benchmarks

Run from the repository root. The predictor benchmark has an iteration setup to restore the input; keep one invocation per iteration so previously reconstructed pixels are never reused as residuals:

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*LosslessPredictorBenchmarks*' --launchCount 1 --warmupCount 5 \
  --iterationCount 12 --invocationCount 1 --unrollFactor 1
```

The iterations are deliberately short for this focused comparison. BenchmarkDotNet reports confidence intervals and warnings; use more launches/iterations when investigating small differences. Repeat with `DOTNET_EnableAVX2=0` to exercise SSE2, or `DOTNET_EnableHWIntrinsic=0` to exercise scalar fallbacks. For production-style tiered-JIT measurements, omit `DOTNET_TieredCompilation=0` and provide substantially longer warmup runs.

The decoder benchmark reads each file once during setup, checks that it is a static lossless WebP, decodes from memory, and disposes each image. The seven committed corpus inputs work without reference tools:

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*LosslessDecodeBenchmarks*' --launchCount 1 --warmupCount 3 \
  --iterationCount 8 --iterationTime 100
```

Add local inputs through `PICTURESHARP_BENCHMARK_WEBP_FILES`: paths separated by the platform's path separator (`:` on Linux/macOS, `;` on Windows). Use the same encoded files for before/after comparisons; do not re-encode separately for each version. Local photos and benchmark artifacts remain ignored by Git.

```bash
PICTURESHARP_BENCHMARK_WEBP_FILES='/absolute/photo-2.8mp.webp:/absolute/photo-12mp.webp' \
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*LosslessDecodeBenchmarks*' --launchCount 1 --warmupCount 3 \
  --iterationCount 8 --iterationTime 100
reference/libwebp-1.6.0-build/dwebp -v /absolute/photo-12mp.webp
```

The encoder benchmark loads each image once and encodes it losslessly at quality 75:

```bash
PICTURESHARP_BENCHMARK_ENCODE_FILES='/absolute/photo.png' \
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*LosslessEncodeBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 8
```

The codec benchmark encodes and decodes PNG and JPEG and halves each image with Lanczos:

```bash
PICTURESHARP_BENCHMARK_CODEC_FILES='/absolute/photo.png' \
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*CodecBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 8
```

The phase-10 matrix and the animation benchmarks (about 50 minutes together on the i5-12500H):

```bash
PICTURESHARP_BENCHMARK_MATRIX_FILES='/absolute/kodim23.png:/absolute/photo-12mp.png' \
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- \
  --filter '*MatrixBenchmarks*' '*AnimationBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 9
```

Keep CPU, runtime, GC settings, input bytes and workload identical when comparing versions. Measure serially, inspect the spread, and repeat any apparent regression. BenchmarkDotNet writes timing and allocation reports to `BenchmarkDotNet.Artifacts/`.


## ICO implementation for 0.5.1

ICO adds a small container around the existing PNG codec or an uncompressed DIB.
`IcoBenchmarks` measures complete encode/decode operations for 16, 32 and 256 px
synthetic RGBA icons with both encodings:

```bash
dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- --filter '*IcoBenchmarks*'
```

Quick local measurements (2026-10-15, i5-12500H, x64, .NET 10.0.12, default JIT/GC):
50 warm-up calls, median of seven batches of 50 calls, in-memory inputs/outputs,
no process startup or disk I/O. Allocations use `GC.GetTotalAllocatedBytes(true)`
and include all threads. This is a short baseline for the new codec, not a full
BenchmarkDotNet run or a claim of improvement over an earlier ICO implementation.
The image uses `(13*x, 17*y, x+y)` modulo 256 and alpha `((x+y)%3)*127`.

| Size | Encoding | Encode (µs) | Decode (µs) | Encode bytes/op | Decode bytes/op |
| --- | --- | ---: | ---: | ---: | ---: |
| 16×16 | PNG | 172.84 | 13.69 | 414048 | 3168 |
| 16×16 | DIB | 6.43 | 6.74 | 1552 | 1424 |
| 256×256 | PNG | 1430.15 | 408.17 | 685133 | 270030 |
| 256×256 | DIB | 168.53 | 152.38 | 271024 | 262582 |

PNG includes the existing encoder's compression workspace costs. DIB writes rows
through a pooled buffer. Container output uses pooled segments before emitting
the directory and payloads, including on non-seekable streams. No new SIMD path
was added; the PNG payload reuses existing optimized code.

## Limits and cancellation in 0.5.3

Cancellation checks run per row, block, MCU row, macroblock row, frame or encoding stage (see
[Resource limits and cancellation](limits-and-cancellation.md)), never per pixel. The overhead was measured against
the 0.5.2 build with a comparison probe: the same in-memory inputs and options, a photo scaled to 2048×1536 and the
`graphic` source scaled 8× (1600×1200), single-threaded codecs, 5 warm-ups and the median of 21 calls, three
alternating runs per build (i5-12500H, Linux x64, .NET 10.0.12, default JIT/GC, 2026-10-15).

| Operation | 0.5.2 (ms) | 0.5.3 (ms) | Median change | Runs |
| --- | ---: | ---: | ---: | --- |
| photo WebP lossy encode / decode | 290.80 / 25.45 | 289.85 / 25.09 | −0.3% / −1.7% | ±2% |
| photo WebP lossless (method 2) encode / decode | 960.55 / 23.73 | 960.13 / 23.94 | +0.3% / +1.4% | −1…+3% |
| photo PNG encode / decode | 624.35 / 29.08 | 623.25 / 29.27 | −0.3% / +0.4% | −3…+1% |
| photo JPEG encode / decode | 51.13 / 15.19 | 52.37 / 15.06 | +2.4% / −0.6% | −1…+5% / −2…0% |
| photo GIF encode / decode | 268.53 / 14.35 | 270.93 / 14.39 | −0.2% / −1.4% | −4…+1% |
| photo BMP encode / decode | 6.87 / 3.50 | 6.81 / 3.31 | +1.2% / −5.0% | −8…+3% |
| graphic WebP lossy encode / decode | 121.17 / 8.15 | 115.44 / 7.91 | −4.8% / −2.5% | −5…−2% |
| graphic PNG encode / decode | 12.18 / 5.48 | 12.18 / 5.46 | 0.0% / −0.4% | −3…+5% |
| graphic JPEG encode / decode | 20.53 / 8.21 | 20.37 / 8.08 | +0.3% / −0.2% | −5…+2% |
| graphic GIF encode / decode | 18.13 / 6.70 | 18.16 / 6.82 | +0.1% / +1.5% | 0…+4% |

The changes stay within the run-to-run spread in both directions; no operation shows a repeatable regression.
Allocations per operation are unchanged. A first, multithreaded run of the same probe (default parallelism) showed
±4% noise with the same conclusion; resize, Gaussian blur and grayscale were also unchanged.

Reading input now allocates the encoded bytes once. A 37.7 MB BMP (4096×3072) loaded from a stream allocated, besides
the 50 MB of decoded pixels:

| Stream | 0.5.2 | 0.5.3 |
| --- | ---: | ---: |
| Seekable (`MemoryStream` without an exposed buffer, `BufferedStream`) | 2.00× input | 1.00× input |
| Non-seekable, 80 KiB reads | 3.22× input | 1.00× input |

A seekable stream is read into an exactly sized array; a non-seekable one grows in pooled arrays and is copied once.
The growing buffer is bounded by `MaxInputBytes`, so an endless stream stops after at most `MaxInputBytes + 1` bytes.
Peak memory while reading is the input for a seekable stream; a non-seekable stream briefly holds the old and the
doubled pooled buffer while growing, up to about three times the bytes read so far; the pool keeps large buffers for reuse until memory pressure trims it.

`CancellationInFlightTests` cancel decoding, encoding, animations and processing of 1–9 MP images after a quarter of
their uncanceled duration and require the call to end within 250 ms plus half that duration; all formats meet it.

## JPEG thumbnails in 0.5.4

`DecoderOptions.TargetSize` decodes JPEG at 1/2, 1/4 or 1/8 scale with libjpeg-turbo's reduced inverse DCTs
(`jidctred.c`), bit-exact with libjpeg-turbo 2.1.5 (through Pillow 10.2.0 `draft`) on the corpus, on damaged
streams and on the local 12 MP and 27 MP photos; the tolerance is therefore 0. `ThumbnailBenchmarks` compares
`Load` + `Thumbnail(size, size)` with `TargetSize` at the default reducing gap 2 and at gap 1. Inputs: two local
4032×3024 camera photos (baseline 4:2:0 and 4:4:4), the 4:2:0 photo re-encoded progressive by Pillow (quality 90),
and the same photo scaled to 6048×4536 (27 MP) as baseline and progressive. i5-12500H, Linux x64, .NET 10.0.12,
BenchmarkDotNet 0.15.2 (one launch, 3 warmups, 9 iterations), default parallelism, 2026-10-15.

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

Steady-state allocations exclude the planes and coefficients, which are rented from the pool. Peak memory therefore
comes from a separate probe (`peak` mode of the benchmark program): one operation in a fresh process, rise of the
Linux peak resident set (`VmHWM`) after reading the input and warming up on a small image.

| Input | Size | Load + Thumbnail | TargetSize | TargetSize, gap 1 |
| --- | ---: | ---: | ---: | ---: |
| 12 MP baseline 4:2:0 | 256 / 512 / 1920 | 68 / 72 / 98 MiB | 7 / 25 / 98 MiB | 2 / 8 / 43 MiB |
| 12 MP baseline 4:4:4 | 256 / 512 / 1920 | 85 / 89 / 115 MiB | 6 / 25 / 115 MiB | 2 / 8 / 43 MiB |
| 12 MP progressive 4:2:0 | 256 / 512 / 1920 | 103 / 107 / 133 MiB | 42 / 60 / 133 MiB | 37 / 43 / 78 MiB |
| 27 MP baseline 4:2:0 | 256 / 512 / 1920 | 149 / 154 / 189 MiB | 4 / 15 / 189 MiB | 4 / 6 / 74 MiB |
| 27 MP progressive 4:2:0 | 256 / 512 / 1920 | 228 / 233 / 268 MiB | 83 / 94 / 268 MiB | 83 / 84 / 153 MiB |

- **Baseline:** for 256 and 512 px the peak falls by 72–97%; a baseline decode at reduced scale never holds a
  full-size buffer. The time falls less (by 10–54%) because Huffman decoding of every coefficient is unchanged.
- **Progressive:** the coefficient buffer (2 bytes per sample: 36 MB for 12 MP 4:2:0, 82 MB for 27 MP) is needed at
  full size until the last scan, so the peak falls only by the planes and RGBA (40–64% at 256 px).
- **1920 px with gap 2** has no room for a 1/2 decode of these photos (4032 < 2 × 2 × 1920) and costs the same as
  `Load` + `Thumbnail`; gap 1 decodes at 1/2 and saves 45–60% of the peak.
- No SIMD path was added: the reduced IDCTs are short scalar loops that do not appear in the profile next to entropy
  decoding. The full-size path is unchanged (bit-exact references, same timings).
