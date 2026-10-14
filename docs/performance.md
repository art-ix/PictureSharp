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
JPEG and GIF, animations, and processing (especially resize), with small and large images.
Freeze the specific corpus files/options before each corresponding optimization stage. ARM64
measurements require real hardware. For the final WebP gate, require at most 3× the native SIMD
time on the frozen matrix using libwebp 1.6.0; disclose process startup, I/O and API timing differences.
These broader measurements and the final gate remain pending; the VP8L matrix alone does not close phase 10.

The 0.4.6 matrix (PNG, deflate, resizing, JPEG, WebP lossy, BMP and GIF) and the 0.4.5 lossless encode matrix
are described in the next sections.

## Hot paths in 0.4.6

The 0.4.6 matrix freezes 40 operations on fixed bytes: PNG decoding of the corpus `photo` and `photo_alpha`
sources, a palette graphic scaled 8× (1600×1200), and local photos at 1920×1440 and 4000×3000 (written by
RasterSharp 0.4.5 and by Pillow 10.2.0); PNG encoding at levels 1, 6 and 9; resizing with every kernel, down and
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
| PNG decode 1920×1440 (RasterSharp file) | 54.70 / 53.28 | 31.33 / 31.15 | 42–43% | — |
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

`CodecBenchmarks` (BenchmarkDotNet) covers PNG and JPEG encoding and decoding and Lanczos halving of three corpus
sources; add local images through `RASTERSHARP_BENCHMARK_CODEC_FILES`.

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
`RASTERSHARP_BENCHMARK_ENCODE_FILES` (same path separator rules as the decode benchmark).

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

On the same bytes, `dwebp -v` 1.6.0 reported median internal decode times of 47 ms and 190 ms (five warmups, 21 samples; 10th–90th percentiles 45–52 ms and 187–194 ms). That timer excludes file reading, process startup and output writing, has millisecond precision, and does not measure the same API overhead as RasterSharp. The BenchmarkDotNet/API times are about 1.14× / 1.13× those native internal times on these two inputs; this is context, not a guarantee for other files or machines.

## Running the benchmarks

Run from the repository root. The predictor benchmark has an iteration setup to restore the input; keep one invocation per iteration so previously reconstructed pixels are never reused as residuals:

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks -- \
  --filter '*LosslessPredictorBenchmarks*' --launchCount 1 --warmupCount 5 \
  --iterationCount 12 --invocationCount 1 --unrollFactor 1
```

The iterations are deliberately short for this focused comparison. BenchmarkDotNet reports confidence intervals and warnings; use more launches/iterations when investigating small differences. Repeat with `DOTNET_EnableAVX2=0` to exercise SSE2, or `DOTNET_EnableHWIntrinsic=0` to exercise scalar fallbacks. For production-style tiered-JIT measurements, omit `DOTNET_TieredCompilation=0` and provide substantially longer warmup runs.

The decoder benchmark reads each file once during setup, checks that it is a static lossless WebP, decodes from memory, and disposes each image. The seven committed corpus inputs work without reference tools:

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks -- \
  --filter '*LosslessDecodeBenchmarks*' --launchCount 1 --warmupCount 3 \
  --iterationCount 8 --iterationTime 100
```

Add local inputs through `RASTERSHARP_BENCHMARK_WEBP_FILES`: paths separated by the platform's path separator (`:` on Linux/macOS, `;` on Windows). Use the same encoded files for before/after comparisons; do not re-encode separately for each version. Local photos and benchmark artifacts remain ignored by Git.

```bash
RASTERSHARP_BENCHMARK_WEBP_FILES='/absolute/photo-2.8mp.webp:/absolute/photo-12mp.webp' \
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks -- \
  --filter '*LosslessDecodeBenchmarks*' --launchCount 1 --warmupCount 3 \
  --iterationCount 8 --iterationTime 100
reference/libwebp-1.6.0-build/dwebp -v /absolute/photo-12mp.webp
```

The encoder benchmark loads each image once and encodes it losslessly at quality 75:

```bash
RASTERSHARP_BENCHMARK_ENCODE_FILES='/absolute/photo.png' \
dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks -- \
  --filter '*LosslessEncodeBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 8
```

The codec benchmark encodes and decodes PNG and JPEG and halves each image with Lanczos:

```bash
RASTERSHARP_BENCHMARK_CODEC_FILES='/absolute/photo.png' \
dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks -- \
  --filter '*CodecBenchmarks*' --launchCount 1 --warmupCount 3 --iterationCount 8
```

Keep CPU, runtime, GC settings, input bytes and workload identical when comparing versions. Measure serially, inspect the spread, and repeat any apparent regression. BenchmarkDotNet writes timing and allocation reports to `BenchmarkDotNet.Artifacts/`.
