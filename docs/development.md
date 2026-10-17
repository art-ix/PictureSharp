# Development

## Building and testing

Use the .NET 10 SDK or a compatible later SDK:

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"   # comparisons with external tools; skipped when missing
```

The library version comes from the [VERSION](../VERSION) file, read by `Directory.Build.props`. Contributor rules (no
dependencies, no `unsafe`, porting conventions) are in [AGENTS.md](../AGENTS.md), and the implementation phases with
their completion criteria in [plan.md](../plan.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/PictureSharp/` | Layered library: `Domain/`, `Application/`, `Infrastructure/`, `Algorithms/`, `Primitives/`, `PublicApi/`, `Composition/`; see [architecture](architecture.md) |
| `src/PictureSharp.Drawing/`, `src/PictureSharp.Skia/`, `src/PictureSharp.AspNetCore/` | [Adapter packages](adapters.md) with their own dependencies, outside the library |
| `tests/PictureSharp.Tests/` | TUnit tests on Microsoft.Testing.Platform |
| `tests/PictureSharp.Adapters.Tests/` | Adapter tests: pixel round trips, a `TestServer` integration and dependency checks; GDI+ tests run on Windows only |
| `tests/assets/` | Corpus, reference hashes, oracle scripts, public API snapshot |
| `benchmarks/` | BenchmarkDotNet |
| `samples/PictureSharp.Cli/` | [Command-line sample](cli.md) |
| `docs/` | These guides |

## Reference tools

The committed synthetic corpus and reference hashes let the tests run without native image tools. `Category=Oracle`
tests compare results with external implementations when the tools are available.

New WebP ports and encoder comparisons use **libwebp 1.6.0**. Build its pinned sources and command-line tools with Git,
CMake and a C compiler (with the giflib headers, `libgif-dev`, the script also builds the GIF oracles `gif2webp`,
`anim_dump` and `anim_diff`):

```bash
./scripts/build-webp-reference.sh
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

Tests and corpus scripts automatically use `reference/libwebp-1.6.0-build/`. Set `PICTURESHARP_WEBP_TOOLS` to select
another directory containing the 1.6.0 tools. Native sources and binaries stay in the Git-ignored `reference/` directory
and are used only for verification.

HDR gain map reconstruction (ISO 21496-1) is compared with **libultrahdr v2.0.2** (commit
`e5f5a022fe96fc4dc2ee35c19f733a50df807abe`). The script builds `ultrahdr_app` without optional HEIF/AVIF codecs and
`ultrahdr_oracle` (`tests/assets/ultrahdr_oracle.cpp`), a harness that runs libultrahdr's gain map functions on
PictureSharp's decoded pixels, so the math is checked separately from libultrahdr's own JPEG and YUV decoding. It needs
CMake, Ninja, a C++17 compiler and libjpeg headers; `PICTURESHARP_ULTRAHDR_TOOLS` selects another directory. The HDR
corpus and its double-precision model are regenerated with `python3 -I tests/assets/hdr_reference.py`.

```bash
./scripts/build-ultrahdr-reference.sh
```

Corpus regeneration requires libwebp 1.6.0 tools, ImageMagick, Python, NumPy and Pillow:

```bash
python3 -I tests/assets/generate_corpus.py
python3 -I tests/assets/make_reference.py
python3 -I tests/assets/make_jpeg_encoder_reference.py
python3 -I tests/assets/gif_reference.py
```

`generate_corpus.py` accepts format names (`webp`, `png`, `jpeg`, `bmp`, `gif`, `pcx`) to regenerate only those
directories; without arguments it regenerates all formats, including PCX. `gif_reference.py` stores Pillow's
quantization results; the GIF quantizer port follows the Pillow 10.2.0 C sources (`reference/pillow-10.2.0/`, fetched
separately).

Use Pillow 10.2.0 / libjpeg-turbo 2.1.5 to reproduce the committed JPEG encoder references. Additional JPEG oracle tests
use a C compiler and libjpeg development headers, with Pillow and ImageMagick for independent decoding. External corpora
are optional; their download procedure is described in [plan.md](../plan.md).

## What the tests verify

Test categories: `Oracle` compares with external tools and is skipped when they are missing; `Slow` marks fuzzing,
thread-count and in-flight cancellation tests (CI runs them); `Local` uses private photos in `samples/`; `External`
needs the optional downloaded corpora. A quick local loop can exclude `Slow` and `Oracle`:

```bash
dotnet test -c Release --treenode-filter "/*/*/*/*[(Category!=Slow)&(Category!=Oracle)]"
```

- **Decoders:** pixels match `dwebp` and Pillow exactly (SHA-256 of RGBA in `tests/assets/reference.json`); JPEG also
  matches libjpeg-turbo, including the reduced 1/2–1/8 decodes, and GIF frames match libwebp's `anim_dump`.
- **WebP encoder:** the VP8L bitstream is byte-identical to `cwebp` 1.6.0 in every method, quality, near-lossless and
  `-exact` setting tested; lossy output of opaque images is byte-identical too, and with alpha the decoded pixels match
  exactly. Animated WebP is checked against Pillow and `webpmux`, GIF → WebP conversion against `gif2webp`.
- **Other encoders:** JPEG reference vectors are byte-identical to Pillow 10.2.0 / libjpeg-turbo 2.1.5; GIF quantization
  matches Pillow's median cut, octree and Floyd–Steinberg; PCX matches Pillow and ImageMagick; lossless formats
  round-trip exactly.
- **Processing:** the Pillow 10.2.0 oracle (`tests/assets/processing_reference.py`, `graphics_reference.py`) checks
  resizing, compositing, enhancement, filters, rotation, autocontrast and equalization pixel for pixel;
  `ProcessingDeterminismTests` pins the float-sensitive results on every architecture without external tools.
- **Color and HDR:** ICC conversions are compared with LittleCMS through Pillow; gain map reconstruction with
  libultrahdr's own functions on identical decoded pixels and with a double-precision model.
- **Metadata:** edited EXIF is read back with Pillow, and the compressed image data of edited files is compared byte for
  byte with the original.
- **SIMD and threads:** every vector path is compared with its scalar version on random and extreme inputs; the suite
  also runs with `DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0`, and on ARM64 hardware. Encoders and decoders
  give the same bytes and pixels for any `MaxDegreeOfParallelism`.
- **Robustness:** every corpus file is truncated at over a hundred lengths and mutated (bit flips, extreme length
  fields, inserted and removed blocks); decoding and identification may only throw `ImageFormatException` or
  `NotSupportedException`, and `MaxPixels` holds for every file.
- **Architecture and API:** compiled-IL layer checks, the public API snapshot, a C# 14 consumer compilation test and the
  documentation samples.

## Public API snapshot

`PublicApiTests` describes every public type and member by reflection and compares the result with
[tests/assets/PublicApi.approved.txt](../tests/assets/PublicApi.approved.txt). When the API changes, the test fails,
writes `tests/assets/PublicApi.received.txt` (ignored by Git) and lists the differences. After reviewing an intended
change:

```bash
mv tests/assets/PublicApi.received.txt tests/assets/PublicApi.approved.txt
```

## Documentation samples

`DocsExamplesTests` compiles and runs the README quick start and the main samples of each guide in this folder against
corpus files. Update it together with the guides so the documented code keeps working.

## Releasing

Set the version in `VERSION`, add `docs/releases/release-<VERSION>.md`, commit and push a `v<VERSION>` tag. Publishing a
GitHub Release for that tag runs `.github/workflows/release.yml`: it checks that the tag matches `VERSION`, runs the
same validation as pull requests, packs the library and the adapter packages, pushes them to nuget.org through Trusted
Publishing and attaches the `.nupkg` files to the release. The README is packed into the package, so keep its links
absolute.

## Trimming and Native AOT

`scripts/verify-package-consumer.sh` checks the packages as a consumer sees them, on linux-x64 (CI runs it too):

```bash
bash scripts/verify-package-consumer.sh
```

It packs the library, restores it from a local feed in an isolated package cache and runs a consumer with full trimming
and with Native AOT: every codec, processing, async I/O, ICO variants, animation, reduced JPEG decoding, a format
plugin, a frame processor and the package's `global using PictureSharp;` (added under `ImplicitUsings`, left out with
`PictureSharpImplicitUsings=false` and for Windows Forms, WPF and MAUI). It then packs the adapters, checks that each
has one assembly and depends only on PictureSharp and its target API, and runs `samples/PictureSharp.AdaptersSmoke` the
same way.

The library's smoke consumer can also be published by hand; Native AOT needs the platform linker:

```bash
dotnet publish samples/PictureSharp.AotSmoke -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o artifacts/aot
./artifacts/aot/PictureSharp.AotSmoke
```

ARM64 is verified separately on real hardware: the full suite, `DOTNET_EnableHWIntrinsic=0` and the smoke consumer.
