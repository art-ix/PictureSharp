# PictureSharp documentation

| Guide | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | Installation, the first program, `Mutate` vs `Clone`, disposal, exceptions and limits |
| [Loading and saving](loading-and-saving.md) | `Image.Load`/`LoadAsync`, `Identify`, `DetectFormat`, `DecoderOptions`, `ImageConfiguration`, `Save` and `SaveAs*`, streams and web responses |
| [Resource limits and cancellation](limits-and-cancellation.md) | `MaxInputBytes`, `MaxPixels`, `MaxFrames`, `MaxMetadataBytes`, cancellation tokens and what a canceled call leaves behind |
| [Encoder options](encoder-options.md) | `WebPEncoderOptions`, `PngEncoderOptions`, `JpegEncoderOptions`, `BmpEncoderOptions`, `GifEncoderOptions`, `PcxEncoderOptions` and common recipes |
| [Image processing](image-processing.md) | Every operation available in `Mutate`/`Clone`: resize, crop, trim, pad, rotate, color, levels, histograms, filters, compositing with blend modes, alpha masks, custom operations |
| [Pixels and interop](pixels-and-interop.md) | `Rgba32`, pixel spans, `PixelFormat` conversions, WPF/SkiaSharp/WinForms, raw buffer encoding and decoding |
| [Adapters](adapters.md) | The `PictureSharp.Drawing`, `PictureSharp.Skia` and `PictureSharp.AspNetCore` packages: `Bitmap`/`SKBitmap` conversions, `ImageResult` with `Accept` negotiation, uploads with limits |
| [Animation](animation.md) | Frames, durations, loops, animated WebP, APNG and GIF, GIF ↔ WebP conversion |
| [Metadata](metadata.md) | Resolution, typed EXIF, ICC/XMP and metadata editing without recompression |
| [Color management](color-management.md) | ICC/PNG color descriptions, conversion to sRGB, linear-light resize and compositing |
| [Command-line sample](cli.md) | The `samples/PictureSharp.Cli` tool |
| [Architecture](architecture.md) | Layers, model boundaries, composition and compiled dependency checks |
| [Domain verification](ddd-verification.md) | Domain hardening, current tests, CI, package consumers and measurements |
| [Refactoring verification](refactoring-verification.md) | Tests, package consumers, AOT/trimming and before/after measurements |
| [Extending PictureSharp](extending-picturesharp.md) | Format plugins, configurations, custom frame processors and shortcuts; contributing built-in formats and operations |
| [Development](development.md) | Building, tests, oracles, corpus regeneration and the public API snapshot |
| [Performance](performance.md) | VP8L measurements, benchmark commands, allocation costs and comparison limits |

All code samples in these guides use the public API listed in [tests/assets/PublicApi.approved.txt](../tests/assets/PublicApi.approved.txt). The key samples are compiled and run by `DocsExamplesTests`.
