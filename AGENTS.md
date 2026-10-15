# Working on PictureSharp

PictureSharp is a .NET image-processing library written entirely in C#. It reads and writes WebP, PNG, BMP, JPEG and GIF.

Before starting, read [plan.md](plan.md) to identify the current phase and its definition of done (DoD), then read the latest entries in [progres.md](progres.md). Append progress after significant steps and before ending a session: changes, decisions, test and benchmark results, remaining work, and the next step. Preserve earlier entries. `plan.md` tracks scope and phase status; `progres.md` is a local log and must stay out of commits.

## Required constraints

- Target `net10.0` with C# 14 (`LangVersion=14.0`); support .NET 10 and later.
- Keep `src/PictureSharp` dependency-free: BCL only, no `PackageReference`, native libraries or P/Invoke. `System.Drawing.Primitives` (`Size`, `Rectangle`, `Point`, `Color`) is allowed. Tests, benchmarks and samples may use packages.
- Do not use `System.IO.Compression` in the library: `ZLibStream`/`DeflateStream` call native zlib. Use our deflate/inflate implementation in `Compression/`.
- Do not use the `unsafe` keyword. Use `Span<T>`, `ref`, `Unsafe.Add` and `MemoryMarshal`; use `GCHandle` for the memory pinning required by `BitmapData.Scan0`.
- Keep all public API in the `PictureSharp` namespace. Codecs and implementation details are `internal`.
- Store `Image`/`ImageFrame` pixels as `Rgba32` with straight (unpremultiplied) alpha.
- Enforce decoder limits, including `DecoderOptions.MaxPixels`. Corrupt or unrecognized input must throw `ImageFormatException`; valid input with unsupported features must throw `NotSupportedException`. Malicious input must never expose unchecked exceptions such as `IndexOutOfRangeException`.

## Code and layout

Follow `.editorconfig`. Enable nullable annotations, `TreatWarningsAsErrors` and XML documentation for public API (`GenerateDocumentationFile`). Use English for identifiers, comments and XML docs; project documents may use Polish.

- Keep types focused. Use SOLID and Clean Code where they simplify the design: small interfaces at real boundaries and shared configuration. Add interfaces, factories or layers only for a concrete need.
- Match filenames to the main type. Give public enums separate files; name partial files `Type.Responsibility.cs`. Small, closely related internal port types may share a file.
- Put format options, enums and extensions in `Formats/<Format>/`, processing contexts and options in `Processing/`, and I/O helpers in `Internal/`. Public types still use the `PictureSharp` namespace.
- Separate SIMD methods into partial files such as `Type.Simd.cs` or `Type.Responsibility.X86.cs` (and ARM equivalents). Keep core and scalar code readable without intrinsics; short dispatch calls are fine. Keep architecture selection and vector helpers on the SIMD side, without adding indirection in hot loops solely for this separation.
- Prefer pattern matching, collection expressions, records and `with`, validated properties using `field`, and `extension` blocks when they improve clarity without changing semantics, allocations or port fidelity.
- Keep hot loops allocation-free. Rent temporary buffers from `ArrayPool<T>.Shared`; allocate image pixels with `GC.AllocateUninitializedArray`. Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for short hot-path methods.
- Write sparse comments that explain why or identify the port source. Declare constant tables as `static ReadOnlySpan<T> Name => [ ... ];`.

| Path | Purpose |
| --- | --- |
| `src/PictureSharp/` | Library: `Image`, codecs, compression, processing and internal helpers |
| `tests/PictureSharp.Tests/` | xUnit tests |
| `tests/assets/` | Corpus, reference data and generation scripts |
| `benchmarks/` | BenchmarkDotNet |
| `docs/` | User guides; examples checked by `DocsExamplesTests` |
| `samples/PictureSharp.Cli/` | CLI: `convert`, `info`, `resize` |
| `reference/` | Uncommitted reference sources and tools, outside the build |

Register each format in `Formats/CodecRegistry.cs` with an `IImageFormatDetector`, `IImageDecoder` and `IImageEncoder<TOptions>` plus its options record. `CodecRegistry` selects codecs and lists formats; `ImageCodec` invokes them, normalizes exceptions and applies EXIF orientation. Split large codecs into partial files, for example `PngDecoder.Chunks.cs`.

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
dotnet test -c Release --filter Category=Oracle
```

Oracle tests compare with external tools and skip when those tools are missing. Close a phase only when its tests pass and its DoD is met.

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
