# Getting started

## Installation

PictureSharp targets **.NET 10** (C# 14) and depends only on the base class library:

```bash
dotnet add package PictureSharp --version 0.7.1
```

Changes are listed in the [release notes](releases/); RasterSharp 0.5.0 code is covered by the
[migration guide](migration-from-0.5.md).

## Namespaces

| Namespace | Contents |
| --- | --- |
| `PictureSharp` | `Image` and its members (`Load`, `Identify`, `Mutate`, `Clone`, `Save`, …), `Rgba32`, `DecoderOptions`, `ImageInfo`, `ImageMetadata`, `ImageConfiguration` |
| `PictureSharp.Processing` | Processing modes and options: `ResizeMode`, `ResizeOptions`, `RotateMode`, `AnchorPosition`, … |
| `PictureSharp.Formats.<Format>` | Encoder options and the `SaveAs*`/`To*` shortcuts, e.g. `PictureSharp.Formats.WebP` |
| `PictureSharp.Formats` | The interfaces of format plugins |

`Size`, `Rectangle`, `Point` and `Color` come from `System.Drawing.Primitives`, part of the shared framework, so
`using System.Drawing;` works everywhere without `System.Drawing.Common`.

With `<ImplicitUsings>enable</ImplicitUsings>` the package adds `global using PictureSharp;`, except in Windows Forms,
WPF and MAUI projects, which have their own `Image` types. To choose yourself:

```xml
<PropertyGroup>
  <PictureSharpImplicitUsings>false</PictureSharpImplicitUsings> <!-- or true to opt in without ImplicitUsings -->
</PropertyGroup>
```

## The first program

```csharp
using PictureSharp;

using var image = Image.Load("photo.jpg");
Console.WriteLine($"{image.Width}x{image.Height}, decoded from {image.SourceFormat}");

image.Mutate(x => x.AutoOrient().Resize(800, 0).Grayscale());
image.Save("photo-small.webp");                 // the extension selects WebP with default options
```

1. **Load** with `Image.Load` (or `LoadAsync`); the format is detected from the content, not the file name.
2. **Process** with `Mutate` or `Clone`; operations chain and run in order.
3. **Save** with `Save(path)` or `SaveAsWebP`/`SaveAsPng`/`SaveAsJpeg`/`SaveAsBmp`/`SaveAsGif`/`SaveAsPcx`.

## `Mutate` or `Clone`

```csharp
using PictureSharp;
using PictureSharp.Processing;

using var original = Image.Load("photo.jpg");

// Changes `original` itself.
original.Mutate(x => x.AutoOrient());

// Returns new images; `original` stays as it is.
using Image small = original.Clone(x => x.Resize(640, 0));
using Image square = original.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
using Image copy = original.Clone();            // plain deep copy
```

`Clone` copies pixels only when an operation must change them in place, so `Clone(x => x.Resize(...))` never duplicates
the full image first. Operations apply to every frame; see [Image processing](image-processing.md).

## Disposal

`Image` owns its pixel buffers and implements `IDisposable`. Wrap images in `using`; use after `Dispose` throws
`ObjectDisposedException`.

## Pixels

Pixels are 8-bit `Rgba32` with straight (not premultiplied) alpha; higher bit depths are converted when decoding.
`System.Drawing.Color` converts to and from `Rgba32` implicitly:

```csharp
using System.Drawing;
using PictureSharp;

using var canvas = new Image(320, 200, Color.White);
canvas[10, 10] = Color.Red;
Rgba32 pixel = canvas[10, 10];                  // Rgba32(255, 0, 0, 255)
```

See [Pixels and interop](pixels-and-interop.md) for spans, raw buffers and conversions.

## Errors and limits

| Situation | Exception |
| --- | --- |
| Corrupt, truncated or unrecognized image data | `ImageFormatException` (a `FormatException`; `Format` tells which decoder failed) |
| Valid file using an unsupported feature (e.g. arithmetic-coded JPEG) | `NotSupportedException` |
| Saving to an unknown extension, e.g. `Save("x.tiff")` | `NotSupportedException` |
| Out-of-range option value, e.g. `new WebPEncoderOptions { Quality = 120 }` | `ArgumentOutOfRangeException`, thrown where the value is set |
| Image larger than `DecoderOptions.MaxPixels` (default 2^28 pixels), or input, frames or metadata beyond `MaxInputBytes`, `MaxFrames` or `MaxMetadataBytes` | `ImageFormatException` |
| Canceled `CancellationToken` passed to a load, save or processing call | `OperationCanceledException` |

```csharp
try
{
    using var image = Image.Load(upload, new DecoderOptions { MaxPixels = 50_000_000 });
}
catch (ImageFormatException ex)
{
    Console.WriteLine($"Not a valid image: {ex.Message}");
}
```

Malicious input never surfaces index errors; anything unexpected becomes `ImageFormatException`. For untrusted uploads
see [Limits and cancellation](limits-and-cancellation.md). Next: [Loading and saving](loading-and-saving.md),
[Encoder options](encoder-options.md) and [Image processing](image-processing.md).
