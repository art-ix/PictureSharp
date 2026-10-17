# PictureSharp documentation

## Using the library

| Guide | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | Installation, namespaces, the first program, `Mutate` and `Clone`, disposal, errors |
| [Loading and saving](loading-and-saving.md) | `Load`, `Identify`, `DetectFormat`, decoder options, thumbnails while loading, `Save`, `ImageConfiguration` |
| [Limits and cancellation](limits-and-cancellation.md) | Limits for untrusted input, cancellation tokens and what a canceled call leaves behind |
| [Encoder options](encoder-options.md) | Options of the WebP, PNG, JPEG, BMP, GIF and PCX encoders, with recipes |
| [Image processing](image-processing.md) | Every `Mutate`/`Clone` operation: resize, geometry, trimming, color, levels, histograms, filters, compositing, masks, custom operations |
| [Pixels and interop](pixels-and-interop.md) | `Rgba32`, pixel spans, raw buffers in other pixel formats, WPF, SkiaSharp and WinForms |
| [Animation](animation.md) | Frames, durations and loops, animated WebP, APNG and GIF, frame-by-frame reading and writing |
| [Metadata](metadata.md) | Resolution, ICC/EXIF/XMP blocks, typed EXIF and GPS, editing files without recompression |
| [Color management](color-management.md) | ICC and PNG color descriptions, conversion to sRGB, linear-light resizing and compositing |
| [HDR photos](hdr.md) | JPEG gain maps (ISO 21496-1, Apple): `ToHdr`, `ToneMap`, editing and saving with the map |
| [ICO icons](ico.md) | Multi-size icons: selecting, listing and writing variants |
| [Adapters](adapters.md) | The `PictureSharp.Drawing`, `PictureSharp.Skia` and `PictureSharp.AspNetCore` packages |
| [Command-line sample](cli.md) | The `samples/PictureSharp.Cli` tool |
| [Migrating from 0.5.0](migration-from-0.5.md) | From the RasterSharp 0.5.0 package to the current API |

## Extending and contributing

| Guide | What it covers |
| --- | --- |
| [Extending PictureSharp](extending-picturesharp.md) | Format plugins (with gain maps and metadata), custom frame processors and shortcuts; adding built-in formats and operations |
| [Architecture](architecture.md) | Layers, model boundaries, data flow and the compiled dependency checks |
| [Development](development.md) | Building, tests and oracles, corpus regeneration, API snapshot, releasing, trimming and Native AOT |
| [Performance](performance.md) | Benchmark commands and measurements by area |
| [Release notes](releases/) | Changes in each version |

The samples use the public API listed in [tests/assets/PublicApi.approved.txt](../tests/assets/PublicApi.approved.txt).
Complete samples list the namespaces they need; short fragments without `using` directives assume an existing `image`
and the namespaces of the section's earlier samples. `DocsExamplesTests` compiles and runs the main samples against
corpus files.
