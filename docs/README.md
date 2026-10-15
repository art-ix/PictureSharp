# PictureSharp documentation

| Guide | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | Installation, the first program, `Mutate` vs `Clone`, disposal, exceptions and limits |
| [Loading and saving](loading-and-saving.md) | `ImageReader.Load`/`LoadAsync`, `Identify`, `DetectFormat`, `DecoderOptions`, `Save` and `SaveAs*`, streams and web responses |
| [Resource limits and cancellation](limits-and-cancellation.md) | `MaxInputBytes`, `MaxPixels`, `MaxFrames`, `MaxMetadataBytes`, cancellation tokens and what a canceled call leaves behind |
| [Encoder options](encoder-options.md) | `WebPEncoderOptions`, `PngEncoderOptions`, `JpegEncoderOptions`, `BmpEncoderOptions`, `GifEncoderOptions`, `PcxEncoderOptions` and common recipes |
| [Image processing](image-processing.md) | Every operation available in `Mutate`/`Clone`: resize, crop, pad, rotate, color, filters, compositing, custom operations |
| [Pixels and interop](pixels-and-interop.md) | `Rgba32`, pixel spans, `PixelFormat` conversions, WPF/SkiaSharp/WinForms, raw buffer encoding and decoding |
| [Animation](animation.md) | Frames, durations, loops, animated WebP, APNG and GIF, GIF ↔ WebP conversion |
| [Metadata](metadata.md) | Resolution, ICC/EXIF/XMP, EXIF orientation |
| [Command-line sample](cli.md) | The `samples/PictureSharp.Cli` tool |
| [Architecture](architecture.md) | Layers, model boundaries, composition and compiled dependency checks |
| [Domain verification](ddd-verification.md) | Domain hardening, current tests, CI, package consumers and measurements |
| [Refactoring verification](refactoring-verification.md) | Tests, package consumers, AOT/trimming and before/after measurements |
| [API migration](migration-layered-api.md) | Breaking API and namespace changes after 0.5.4 |
| [Extending PictureSharp](extending-picturesharp.md) | Adding formats and operations, domain rules and C# 14 checks |
| [Development](development.md) | Building, tests, oracles, corpus regeneration and the public API snapshot |
| [Performance](performance.md) | VP8L measurements, benchmark commands, allocation costs and comparison limits |

All code samples in these guides use the public API listed in [tests/assets/PublicApi.approved.txt](../tests/assets/PublicApi.approved.txt). The key samples are compiled and run by `DocsExamplesTests`.
