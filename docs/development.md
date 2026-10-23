# Development

## Building and testing

Use the .NET 10 SDK or later:

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"   # comparisons with external tools; skipped when missing
```

The version comes from [VERSION](../VERSION) through `Directory.Build.props`. Contributor rules are in
[AGENTS.md](../AGENTS.md), the phases and their completion criteria in [plan.md](../plan.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/PictureSharp/` | Layered library: `Domain/`, `Application/`, `Infrastructure/`, `Algorithms/`, `Primitives/`, `PublicApi/`, `Composition/`; see [architecture](architecture.md) |
| `src/PictureSharp.Drawing/`, `src/PictureSharp.Skia/`, `src/PictureSharp.AspNetCore/` | [Adapter packages](adapters.md) with their own dependencies |
| `tests/PictureSharp.Tests/` | TUnit tests on Microsoft.Testing.Platform |
| `tests/PictureSharp.Adapters.Tests/` | Adapter tests; GDI+ tests run on Windows only |
| `tests/assets/` | Corpus, reference hashes, oracle scripts, public API snapshot |
| `benchmarks/` | BenchmarkDotNet |
| `samples/PictureSharp.Cli/` | [Command-line sample](cli.md) |
| `docs/` | These guides |

## Reference tools

The committed corpus and reference hashes let the tests run without native tools; `Category=Oracle` tests compare with
external implementations when available. Native sources and binaries stay in the Git-ignored `reference/` directory.

WebP ports and encoder comparisons use **libwebp 1.6.0**. The script needs Git, CMake and a C compiler; with
`libgif-dev` it also builds `gif2webp`, `anim_dump` and `anim_diff`. Tests use `reference/libwebp-1.6.0-build/` or
`PICTURESHARP_WEBP_TOOLS`.

HDR reconstruction is compared with **libultrahdr v2.0.2** (commit `e5f5a022fe96fc4dc2ee35c19f733a50df807abe`). The
script builds `ultrahdr_app` and `ultrahdr_oracle` (`tests/assets/ultrahdr_oracle.cpp`), which runs libultrahdr's gain
map functions on PictureSharp's decoded pixels, and `ultrahdr_iso_oracle` (`tests/assets/ultrahdr_iso_oracle.cpp`),
which writes ISO 21496-1 metadata for comparison with `GainMapMetadata.ToIso()`. It needs CMake, Ninja, a C++17 compiler and libjpeg headers;
`PICTURESHARP_ULTRAHDR_TOOLS` selects another directory. The HDR corpus is regenerated with
`python3 -I tests/assets/hdr_reference.py`.

```bash
./scripts/build-webp-reference.sh
./scripts/build-ultrahdr-reference.sh
```

Corpus regeneration needs the libwebp tools, ImageMagick, Python, NumPy and Pillow 10.2.0 (libjpeg-turbo 2.1.5 for the
JPEG encoder references):

```bash
python3 -I tests/assets/generate_corpus.py
python3 -I tests/assets/make_reference.py
python3 -I tests/assets/make_jpeg_encoder_reference.py
python3 -I tests/assets/gif_reference.py
```

`generate_corpus.py` accepts format names (`webp`, `png`, `jpeg`, `bmp`, `gif`, `pcx`) to regenerate only those. The GIF
quantizer follows the Pillow 10.2.0 C sources in `reference/pillow-10.2.0/`. Optional external corpora are described in
[plan.md](../plan.md).

JPEG encoder changes that leave byte equality with libjpeg-turbo are judged at equal file size with the libjxl metric
tools `ssimulacra2` and `butteraugli_main` (on `PATH` or in `--tools-dir`) and Pillow with libjpeg-turbo:

```bash
python3 -I scripts/compare_jpeg_quality.py samples/*.jpg --max-side 1024 --tools-dir ~/Projekty/ai
```

The script encodes each image with both encoders over a quality range, decodes both with Pillow and reports SSIMULACRA2
and Butteraugli (max, 3-norm) differences at equal size and the BD-rate (negative: PictureSharp files are smaller at
equal quality). References are converted to untagged sRGB first. Output goes to `reference/jpeg-quality/`.

## What the tests verify

Categories: `Oracle` (external tools, skipped when missing), `Slow` (fuzzing, thread counts, in-flight cancellation; CI
runs them), `Local` (private photos in `samples/`) and `External` (downloaded corpora). A quick local loop:

```bash
dotnet test -c Release --treenode-filter "/*/*/*/*[(Category!=Slow)&(Category!=Oracle)]"
```

- **Decoders:** pixels match `dwebp` and Pillow exactly (SHA-256 in `tests/assets/reference.json`); JPEG matches
  libjpeg-turbo including reduced decodes, GIF frames match `anim_dump`.
- **WebP encoder:** VP8L and opaque lossy output are byte-identical to `cwebp` 1.6.0 in every tested setting; with alpha
  the decoded pixels match. Animations are checked against Pillow and `webpmux`, GIF conversion against `gif2webp`.
- **Other encoders:** JPEG is byte-identical to Pillow 10.2.0; GIF quantization matches Pillow; PCX matches Pillow and
  ImageMagick; lossless formats round-trip exactly.
- **Processing:** Pillow oracles check operations pixel for pixel; `ProcessingDeterminismTests` pins float-sensitive
  results on every architecture.
- **Color, HDR and metadata:** ICC conversion against LittleCMS, gain maps against libultrahdr and a double-precision
  model, edited EXIF read back with Pillow and image payloads compared byte for byte.
- **SIMD and threads:** vector paths equal scalar ones, also with `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0`
  and on ARM64; output does not depend on `MaxDegreeOfParallelism`.
- **Robustness:** every corpus file is truncated and mutated; decoding may only throw `ImageFormatException` or
  `NotSupportedException`, and `MaxPixels` always holds.
- **Architecture and API:** IL layer checks, the API snapshot, a C# 14 consumer compilation test and the documentation
  samples (`DocsExamplesTests`, to be updated with the guides).

## Public API snapshot

`PublicApiTests` compares every public member with
[tests/assets/PublicApi.approved.txt](../tests/assets/PublicApi.approved.txt). On a change it fails and writes
`tests/assets/PublicApi.received.txt`; after review:

```bash
mv tests/assets/PublicApi.received.txt tests/assets/PublicApi.approved.txt
```

## Releasing

Set `VERSION`, add `docs/releases/release-<VERSION>.md`, commit and push a `v<VERSION>` tag. A GitHub Release for the
tag runs `.github/workflows/release.yml`: it checks the tag against `VERSION`, runs the pull request validation, packs
the library and adapters, publishes them to nuget.org through Trusted Publishing and attaches the `.nupkg` files. The
README is packed, so keep its links absolute.

## Trimming and Native AOT

`scripts/verify-package-consumer.sh` (also run by CI) packs the library into an isolated local feed and runs a consumer
with full trimming and Native AOT on linux-x64, covering every codec, processing, async I/O, ICO, animation, reduced
JPEG decoding, a plugin, a frame processor and the implicit `global using`. It then checks that each adapter package has
one assembly and depends only on PictureSharp and its target, and runs `samples/PictureSharp.AdaptersSmoke` the same
way.

```bash
bash scripts/verify-package-consumer.sh
dotnet publish samples/PictureSharp.AotSmoke -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o artifacts/aot
./artifacts/aot/PictureSharp.AotSmoke
```

ARM64 is verified separately on real hardware: the full suite, `DOTNET_EnableHWIntrinsic=0` and the smoke consumer.
