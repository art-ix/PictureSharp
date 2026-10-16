# Working on PictureSharp

PictureSharp is a .NET image-processing library written entirely in C#. It reads and writes WebP, PNG, BMP, JPEG, GIF, PCX and ICO.

Before starting, read [plan.md](plan.md) to identify the current phase and its definition of done (DoD), then read the latest entries in [progres.md](progres.md). Append progress after significant steps and before ending a session: changes, decisions, test and benchmark results, remaining work, and the next step. Preserve earlier entries. `plan.md` tracks scope and phase status; `progres.md` is a local log and must stay out of commits.

## Required constraints

- Target `net10.0` with C# 14 (`LangVersion=14.0`); support .NET 10 and later.
- Keep `src/PictureSharp` dependency-free: BCL only, no `PackageReference`, native libraries or P/Invoke. `System.Drawing.Primitives` (`Size`, `Rectangle`, `Point`, `Color`) is allowed. Tests, benchmarks and samples may use packages.
- Do not use `System.IO.Compression` in the library: `ZLibStream`/`DeflateStream` call native zlib. Use our deflate/inflate implementation in `Algorithms/Compression/`.
- Do not use the `unsafe` keyword. Use `Span<T>`, `ref`, `Unsafe.Add` and `MemoryMarshal`; use `GCHandle` for the memory pinning required by `BitmapData.Scan0`.
- `Image` is the single entry point: model types, options (`DecoderOptions`, `EncoderOptions`, `ImageInfo`, `ImageConfiguration`) and the `Image`/`ImageFormat` extension members (`Load`, `Identify`, `Save`, `Mutate`, …) use `PictureSharp`. Declare those operations as C# 14 extension blocks in `PublicApi`, never as `Image` members, and do not add static facades. Processing modes and options use `PictureSharp.Processing`, the plugin SPI `PictureSharp.Formats`, and format-specific API `PictureSharp.Formats.<Format>`. Codecs and implementation details are `internal`.
- Store `Image`/`ImageFrame` pixels as `Rgba32` with straight (unpremultiplied) alpha.
- Enforce decoder limits, including `DecoderOptions.MaxPixels`. Corrupt or unrecognized input must throw `ImageFormatException`; valid input with unsupported features must throw `NotSupportedException`. Malicious input must never expose unchecked exceptions such as `IndexOutOfRangeException`.

## Code and layout

Follow `.editorconfig`. Enable nullable annotations, `TreatWarningsAsErrors` and XML documentation for public API (`GenerateDocumentationFile`). Use English for identifiers, comments and XML docs; project documents may use Polish.

- Keep types focused. Use SOLID and Clean Code where they simplify the design: small interfaces at real boundaries and shared configuration. Add interfaces, factories or layers only for a concrete need.
- Match filenames to the main type. Give public enums separate files; name partial files `Type.Responsibility.cs`. Small, closely related internal port types may share a file.
- Follow the layers and dependency rules in `docs/architecture.md`. Public namespaces describe functionality; internal namespaces describe their layer. Keep the model independent of I/O, orchestration, codecs and processing algorithms. Architecture tests inspect compiled signatures and IL.
- Separate SIMD methods into partial files such as `Type.Simd.cs` or `Type.Responsibility.X86.cs` (and ARM equivalents). Keep core and scalar code readable without intrinsics; short dispatch calls are fine. Keep architecture selection and vector helpers on the SIMD side, without adding indirection in hot loops solely for this separation.
- Prefer pattern matching, collection expressions, records and `with`, validated properties using `field`, and `extension` blocks when they improve clarity without changing semantics, allocations or port fidelity.
- Keep hot loops allocation-free. Rent temporary buffers from `ArrayPool<T>.Shared`; allocate image pixels with `GC.AllocateUninitializedArray`. Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for short hot-path methods.
- Write sparse comments that explain why or identify the port source. Declare constant tables as `static ReadOnlySpan<T> Name => [ ... ];`.

| Path | Purpose |
| --- | --- |
| `src/PictureSharp/` | Library: `Image`, codecs, compression, processing and internal helpers |
| `src/PictureSharp.Drawing/`, `src/PictureSharp.Skia/`, `src/PictureSharp.AspNetCore/` | Adapter packages; each depends only on PictureSharp and its target API, through the public API |
| `tests/PictureSharp.Tests/` | TUnit tests |
| `tests/PictureSharp.Adapters.Tests/` | Adapter tests; GDI+ tests run on Windows only |
| `tests/assets/` | Corpus, reference data and generation scripts |
| `benchmarks/` | BenchmarkDotNet |
| `docs/` | User guides; examples checked by `DocsExamplesTests` |
| `docs/releases/` | Release notes (`release-x.y.z.md`) |
| `docs/measurements/` | Raw benchmark and verification data (`.csv`, `.tsv`, `.json`) |
| `samples/PictureSharp.Cli/` | CLI: `convert`, `info`, `resize` |
| `samples/PictureSharp.AdaptersSmoke/` | Adapter package consumer, trimmed and Native AOT by `verify-package-consumer.sh` |
| `reference/` | Uncommitted reference sources and tools, outside the build |

Register each built-in format in `Composition/CodecRegistry.cs` as a `FormatRegistration`; `ImageConfiguration` builds resolvers and services from registrations, and `Composition/LibraryServices.cs` composes the default configuration and supplies concrete adapters. Public plugins go through `IImageFormatModule` and the adapters in `Application/Extensibility`; keep built-in codecs on the internal contracts. Decode, identify and encode services invoke codecs; shared errors and thumbnail policies do not depend on orchestration. Keep algorithm ports unchanged during structural refactoring. Split large codecs into partial files, for example `PngDecoder.Chunks.cs`.

## Porting reference code

Use **libwebp v1.6.0** in `reference/libwebp-1.6.0/` for new ports. Treat the sources as read-only. Build the reference tools separately:

```bash
./scripts/build-webp-reference.sh
```

The script fetches and verifies the pinned tag/commit, then builds `cwebp`, `dwebp`, `webpmux` and `img2webp` in `reference/libwebp-1.6.0-build/`. With giflib headers (`libgif-dev`), it also builds `gif2webp`, `anim_dump` and `anim_diff`. Tests and corpus scripts prefer these tools; override the directory with `PICTURESHARP_WEBP_TOOLS`. Keep the system 1.3.2 tools and `reference/libwebp/` for historical comparisons. Phase 1–3 decoder ports originated from 1.3.2; their corpus also passes against 1.6.0.

- Port function by function. Add a source comment above each port, for example `// port: src/dec/vp8_dec.c GetCoeffsFast`. Preserve source names where practical, using PascalCase.
- Start with scalar code from `src/dsp/*.c`; add `Vector128`/`Vector256` only afterward. Tests must prove SIMD and scalar results are identical.
- Preserve C semantics: integer promotions, `clip_8b` rather than a `(uint8_t)` cast, arithmetic shifts, signed/unsigned mixing and overflow behavior.
- VP8L encoder costs use 23 fractional bits, as in 1.6.0. Keep source `float` calculations in `float` with the same operation order; byte equality with `cwebp` depends on it.
- Maintain `THIRD-PARTY-NOTICES.txt` for ported sources, including libwebp (BSD-3), libjpeg-turbo (IJG/BSD) and zlib. PictureSharp uses MIT; NuGet must include both `LICENSE` and the notices.

## Build and verification

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

Tests use TUnit on Microsoft.Testing.Platform (`global.json` selects it for `dotnet test`), so filters use `--treenode-filter` and `[Property("Category", …)]`. Oracle tests compare with external tools and report as skipped (`Skip.Unless`) when those tools are missing. `Category=Slow` marks fuzzing, thread-count and in-flight cancellation tests; CI runs them, and a quick local loop may exclude them with `--treenode-filter "/*/*/*/*[(Category!=Slow)&(Category!=Oracle)]"`. Collections compare with `IsEquivalentTo(expected, CollectionOrdering.Matching)` (`IsEqualTo` compares array references), tests that replace process-wide state or measure time use `[NotInParallel]`, and allocation tests share the `"Allocations"` key. `ProcessorCountLimit` caps running tests at the core count. Close a phase only when its tests pass and its DoD is met.

- Commit the synthetic corpus in `tests/assets/corpus/`, including variants from `cwebp`, `img2webp`, `webpmux`, ImageMagick and Pillow. Regenerate with:

  ```bash
  python3 -I tests/assets/generate_corpus.py
  python3 -I tests/assets/make_reference.py
  ```

- Decoder pixels must match `reference.json` exactly. `make_reference.py` records SHA-256 hashes of RGBA from `dwebp -pam` and Pillow; run it with `python3 -I`.
- Lossless encoding must round-trip with identical pixels. Lossy encoding targets identical bytes to `cwebp` 1.6.0; fallback PSNR and size criteria are in [plan.md](plan.md).
- GIF quantization follows Pillow 10.2.0, with C sources in `reference/pillow-10.2.0/` (tag `10.2.0`). Regenerate `gif_quantize_reference.json` with `python3 -I tests/assets/gif_reference.py`.
- Private photos in `samples/*.jpg` and `samples/*.webp` may contain EXIF GPS data. Keep them ignored and uncommitted; use them only for `Category=Local` tests and local benchmarks.
- Obtain user approval before downloading external corpora through `fetch-external.sh` (libwebp-test-data, PngSuite, Kodak and BMP Suite).

## Git and versioning

- Commit and push only when the user explicitly asks.
- `VERSION` contains the SemVer version; `Directory.Build.props` applies it to all projects.
- Never commit `progres.md`, `reference/`, `tests/assets/external/`, `bin/` or `obj/`.

## Domain changes and C# 14

- `Image` owns common-canvas validation and complete buffer publication. Application prepares outputs and handles cancellation/pooling. Call `ProcessingState.ReplaceFrames`; do not call `ImageFrame.Publish` outside the aggregate. Use `ReplaceFramePixels` only for same-canvas copy-on-write.
- Attach/detach frames only through the collection or sequential read session using owner tokens. Negative durations are rejected by the model; format limits stay in codecs. ICO variants remain independent images.
- Public metadata arrays are copied snapshots. Internal codecs use owned blocks; `GetBlockLength`/`CopyBlockTo` avoid read allocations. Preserve unknown EXIF data and failed-edit atomicity.
- Use C# 14 `field` and extension blocks where appropriate; records/with are for immutable options. Keep public Size/Rectangle/TimeSpan and test span overload resolution with `ConsumerCompilationTests`.
- Run `bash scripts/verify-package-consumer.sh` to verify the local package under full trimming and linux-x64 Native AOT. See docs/extending-picturesharp.md. CI gates push/PR and release; actual ARM64 testing remains required separately.
