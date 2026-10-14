# Development

## Building and testing

Use the .NET 10 SDK or a compatible later SDK:

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --filter Category=Oracle      # comparisons with external tools; skipped when missing
```

The library version comes from the [VERSION](../VERSION) file, read by `Directory.Build.props`. Contributor rules (no dependencies, no `unsafe`, porting conventions) are in [AGENTS.md](../AGENTS.md), and the implementation phases with their completion criteria in [plan.md](../plan.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/RasterSharp/` | The library: `Image`, options, the `WebPConverter`, `Formats/` (codecs), `Compression/` (deflate/inflate), `Processing/`, `Internal/` |
| `tests/RasterSharp.Tests/` | xUnit tests |
| `tests/assets/` | Corpus, reference hashes, oracle scripts, public API snapshot |
| `benchmarks/` | BenchmarkDotNet |
| `samples/RasterSharp.Cli/` | [Command-line sample](cli.md) |
| `docs/` | These guides |

## Reference tools

The committed synthetic corpus and reference hashes let the tests run without native image tools. `Category=Oracle` tests compare results with external implementations when the tools are available.

New WebP ports and encoder comparisons use **libwebp 1.6.0**. Build its pinned sources and command-line tools with Git, CMake and a C compiler (with the giflib headers, `libgif-dev`, the script also builds the GIF oracles `gif2webp`, `anim_dump` and `anim_diff`):

```bash
./scripts/build-webp-reference.sh
dotnet test -c Release --filter Category=Oracle
```

Tests and corpus scripts automatically use `reference/libwebp-1.6.0-build/`. Set `RASTERSHARP_WEBP_TOOLS` to select another directory containing the 1.6.0 tools. Native sources and binaries stay in the Git-ignored `reference/` directory and are used only for verification.

Corpus regeneration requires libwebp 1.6.0 tools, ImageMagick, Python, NumPy and Pillow:

```bash
python3 -I tests/assets/generate_corpus.py
python3 -I tests/assets/make_reference.py
python3 -I tests/assets/make_jpeg_encoder_reference.py
python3 -I tests/assets/gif_reference.py
```

`generate_corpus.py` accepts format names (`webp`, `png`, `jpeg`, `bmp`, `gif`, `pcx`) to regenerate only those directories; without arguments it regenerates all formats, including PCX. `gif_reference.py` stores Pillow's quantization results; the GIF quantizer port follows the Pillow 10.2.0 C sources (`reference/pillow-10.2.0/`, fetched separately).

Use Pillow 10.2.0 / libjpeg-turbo 2.1.5 to reproduce the committed JPEG encoder references. Additional JPEG oracle tests use a C compiler and libjpeg development headers, with Pillow and ImageMagick for independent decoding. External corpora are optional; their download procedure is described in [plan.md](../plan.md).

## What the tests verify

In version 0.4.7, all 4018 tests pass, including the Oracle tests against libwebp 1.6.0, Pillow and ImageMagick.

- **Decoders:** pixels match `dwebp` and Pillow exactly (SHA-256 of RGBA in `tests/assets/reference.json`). The WebP decoder ports originated from libwebp 1.3.2; recomputing the 248 corpus references with 1.6.0 changed none.
- **Lossless WebP:** the nine encoder source images round-trip exactly through both decoders, and the VP8L bitstream is byte-identical to `cwebp` 1.6.0 in 72 configurations (methods 0–6, qualities 50–100, near-lossless, `-exact`).
- **Lossy WebP:** checked across 252 corpus configurations (nine images, four qualities, all seven methods). Opaque outputs are byte-identical to `cwebp`; where alpha compression differs, decoded pixels match exactly and the size stays within +1%. Presets, filters, alpha settings, SharpYuv, target size/PSNR and token partitions also have native oracle checks.
- **Animated WebP:** 46 encoder tests, including 38 external checks through Pillow and webpmux 1.6.0 comparing decoded canvases, durations, loop counts, background color and EXIF extraction across lossy, lossless, mixed, blend/dispose and keyframe cases.
- **JPEG encoder:** reference vectors are byte-identical to Pillow 10.2.0 / libjpeg-turbo 2.1.5.
- **GIF:** decoded frames of the 30 corpus files match `anim_dump` 1.6.0 (or `gif2webp` where `anim_util.c` restores the wrong canvas, Pillow for files libwebp rejects); 147 quantization results are identical to Pillow 10.2.0 (median cut, octree, Floyd–Steinberg); RasterSharp's GIF files decode identically in `anim_dump` and Pillow; `WebPConverter.Convert` matches `gif2webp` frame for frame, and GIF → lossless WebP → GIF round-trips exactly.
- **PCX:** the Pillow corpus (`tests/assets/corpus/pcx/`) matches Pillow. Stored RGBA hashes in `reference.json` verify decoding without external tools; corpus coverage and identification are also checked. Encoded black-and-white 1-bit, two-plane, four-plane, 256-color and 24-bit files match Pillow; 32-bit files, padded planes and RGB widths 1/3 match ImageMagick (Pillow 10.2 misreads the padding at those RGB widths). RLE tests cover all byte values, the 63-byte run boundary and bounded input spans. A potential VGA palette marker must follow the complete raster; a grayscale sample with the same value is not a palette marker. RasterSharp keeps a colored 1-bit palette; Pillow's mode `1` does not.
- **SIMD:** every vector path is compared with its scalar version on random and extreme inputs and must give identical results; other CPUs keep the scalar code. Release 0.4.1 covers the VP8 codec (SSE4.1/AVX2 transforms, quantization, distortion, loop filters, YUV to RGB with fancy upsampling). Release 0.4.2 covers VP8L predictors 0–4 and 8–9, the green and color transforms, ARGB to RGBA, PNG Sub/Up unfiltering and the WebP alpha horizontal and vertical filters (SSE2/AVX2).
- **Threads:** WebP, PNG and JPEG encoding and decoding give the same bytes and pixels for any `MaxDegreeOfParallelism`.
- **VP8L predictors in 0.4.3:** modes 10–12 add SSE2/AVX2 paths. Tests cover tile bits 2–9, vector and tile boundaries, nested rounding, selection ties, clamping and residual wraparound. The predictor and decoder tests also run with AVX2 disabled and with all hardware intrinsics disabled. See [Performance](performance.md) for timings and benchmark commands.
- **VP8L decoding in 0.4.4:** word-based bit-window refill, two-level Huffman lookup and short repeating backward references are optimized; predictor modes 5–7 and 13 use SSE2. Tests cover bit reads across window/slice boundaries, overlapping copies, negative division rounding, saturation and pixel wraparound. Core/scalar routines are kept separate from SIMD implementations in partial files. The same separation now covers VP8 TrueMotion and last-coefficient lookup.
- **VP8L encoding in 0.4.5:** predictor residuals, cross-color histograms, combined entropy, histogram population costs and the hash chain are optimized while every output stays byte-identical. Tests compare `PredictorSub` (all 14 modes), the cross-color collectors and the non-zero mask with scalar code, the prepared combined entropy and `PopulationCost` with reference implementations, the bit writer with a bit-by-bit writer, and the parallel hash chain search with the single-threaded one for 2–16 threads. See [Performance](performance.md).
- **ARM64 in 0.4.8:** the 128-bit SSE2/SSSE3/SSE4.1 bodies go through `Internal/Simd` helpers that emit the same instruction on x86 and the equivalent AdvSimd sequence on ARM64 (zip, sqxtn/sqxtun, widening multiplies, ext, tbl, uaddlp, uhadd), so one vector body serves both architectures and gives identical results. AVX2-only code has ARM64 versions in `*.Arm.cs` partial files: VP8 loop filters, `Disto` and quantization, the JPEG forward and inverse DCTs (8×8 blocks as pairs of 128-bit halves), CRC-32 with the ARMv8 CRC32 instructions and Adler-32 with NEON. The SIMD-versus-scalar tests run unchanged on ARM64; verify on real hardware with the full suite, with `DOTNET_EnableHWIntrinsic=0`, and on x64 also with `DOTNET_EnableAVX2=0`.
- **Robustness:** every corpus file is truncated at over a hundred lengths and mutated 120 times (bit flips, extreme length fields, inserted and removed blocks); decoding and identification may only throw `ImageFormatException` or `NotSupportedException`, and `MaxPixels` is enforced for every file.
- **Hot paths in 0.4.6:** deflate (sequential and segmented parallel parse), PNG filters, CRC-32, Adler-32, resampling, premultiplication and the JPEG DCTs, quantization, coefficient masks and bit writer are checked against scalar or reference implementations; the inverse DCT tests include blocks outside the 32-bit vector range and 16-bit quantization tables. Parallel deflate, PNG and JPEG encoding and pipelined PNG decoding match the single-threaded results, and damaged PNG data decoded through the pipeline only throws `ImageFormatException`. The 40 outputs of the 0.4.6 benchmark matrix are identical to 0.4.5 with AVX2 and with all hardware intrinsics disabled. See [Performance](performance.md).
- **Image processing:** 140 tests. The Pillow 10.2.0 oracle (`tests/assets/processing_reference.py`) checks 569 results for exact pixel equality: resizing with all filters, modes and anchors, compositing, background flattening, grayscale, enhancement, blur and sharpening, rotation, sepia and EXIF orientation. The oracle runs on x64 only: Pillow 10.2.0 built for ARM64 rounds some enhancement and rotation results differently, while RasterSharp gives the same pixels on both. `ProcessingDeterminismTests` pins those float-sensitive results on every architecture without external tools.

## Public API snapshot

`PublicApiTests` describes every public type and member by reflection and compares the result with [tests/assets/PublicApi.approved.txt](../tests/assets/PublicApi.approved.txt). When the API changes, the test fails, writes `tests/assets/PublicApi.received.txt` (ignored by Git) and lists the differences. After reviewing an intended change:

```bash
mv tests/assets/PublicApi.received.txt tests/assets/PublicApi.approved.txt
```

## Documentation samples

`DocsExamplesTests` compiles and runs the README quick start and the main samples of each guide in this folder against corpus files. Update it together with the guides so the documented code keeps working.
