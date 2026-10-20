# PictureSharp documentation

## Using the library

| Guide | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | Installation, namespaces, the first program, `Mutate` and `Clone`, errors |
| [Loading and saving](loading-and-saving.md) | `Load`, `Identify`, decoder options, thumbnails while loading, `Save`, `ImageConfiguration` |
| [Limits and cancellation](limits-and-cancellation.md) | Limits for untrusted input and cancellation tokens |
| [Encoder options](encoder-options.md) | WebP, PNG, JPEG, BMP, GIF and PCX encoder options |
| [Image processing](image-processing.md) | Every `Mutate`/`Clone` operation, photo adjustments and custom operations |
| [Pixels and interop](pixels-and-interop.md) | `Rgba32`, pixel spans, raw buffers, WPF, SkiaSharp and WinForms |
| [Animation](animation.md) | Frames, animated WebP, APNG and GIF, frame-by-frame I/O |
| [Metadata](metadata.md) | ICC/EXIF/XMP, typed EXIF and GPS, editing without recompression |
| [Color management](color-management.md) | Conversion to sRGB, linear-light resizing and compositing |
| [HDR photos](hdr.md) | JPEG gain maps: `ToHdr`, `ToneMap`, editing and saving with the map |
| [ICO icons](ico.md) | Selecting, listing and writing icon variants |
| [Adapters](adapters.md) | `PictureSharp.Drawing`, `PictureSharp.Skia` and `PictureSharp.AspNetCore` |
| [Command-line sample](cli.md) | The `samples/PictureSharp.Cli` tool |
| [Migrating from 0.5.0](migration-from-0.5.md) | From RasterSharp 0.5.0 to the current API |

## Extending and contributing

| Guide | What it covers |
| --- | --- |
| [Extending PictureSharp](extending-picturesharp.md) | Format plugins, frame processors, shortcuts; adding built-in formats and operations |
| [Architecture](architecture.md) | Layers, model boundaries, data flow and compiled dependency checks |
| [Development](development.md) | Building, tests and oracles, corpus, API snapshot, releasing, Native AOT |
| [Performance](performance.md) | Benchmark commands and measurements |
| [Release notes](releases/) | Changes in each version |

Samples use the public API in [tests/assets/PublicApi.approved.txt](../tests/assets/PublicApi.approved.txt). Fragments
without `using` directives assume an existing `image` and the namespaces of earlier samples in the section.
`DocsExamplesTests` compiles and runs the main samples.
