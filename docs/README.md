# RasterSharp documentation

| Guide | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | Installation, the first program, `Mutate` vs `Clone`, disposal, exceptions and limits |
| [Loading and saving](loading-and-saving.md) | `Image.Load`/`LoadAsync`, `Identify`, `DetectFormat`, `DecoderOptions`, `Save` and `SaveAs*`, streams and web responses |
| [Encoder options](encoder-options.md) | `WebPEncoderOptions`, `PngEncoderOptions`, `JpegEncoderOptions`, `BmpEncoderOptions` and common recipes |
| [Image processing](image-processing.md) | Every operation available in `Mutate`/`Clone`: resize, crop, pad, rotate, color, filters, compositing, custom operations |
| [Pixels and interop](pixels-and-interop.md) | `Rgba32`, pixel spans, `PixelFormat` conversions, WPF/SkiaSharp/WinForms, the `WebP` facade |
| [Animation](animation.md) | Frames, durations, loops, animated WebP and APNG |
| [Metadata](metadata.md) | Resolution, ICC/EXIF/XMP, EXIF orientation |
| [Command-line sample](cli.md) | The `samples/RasterSharp.Cli` tool |
| [Development](development.md) | Building, tests, oracles, corpus regeneration and the public API snapshot |

All code samples in these guides use the public API listed in [tests/assets/PublicApi.approved.txt](../tests/assets/PublicApi.approved.txt). The key samples are compiled and run by `DocsExamplesTests`.
