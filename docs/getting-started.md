# Getting started

## Installation

PictureSharp targets **.NET 10** (C# 14) and has no dependencies beyond the base class library. Install version 0.5.5 from NuGet:

```bash
dotnet add package PictureSharp --version 0.5.5
```

The model lives in `PictureSharp`, I/O in `PictureSharp.IO`, processing in `PictureSharp.Processing`, and format options and shortcuts in `PictureSharp.Formats.<Format>`. Version 0.5.5 changes the API from 0.5.4; see the [migration guide](migration-layered-api.md). Geometry types (`Size`, `Rectangle`, `Point`) and `Color` come from `System.Drawing.Primitives`, which is part of the shared framework, so `using System.Drawing;` works on every platform without `System.Drawing.Common`.

## The first program

```csharp
using PictureSharp;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Processing;

using var image = ImageReader.Load("photo.jpg");
Console.WriteLine($"{image.Width}x{image.Height}, decoded from {image.SourceFormat}");

image.Mutate(x => x.AutoOrient().Resize(800, 0).Grayscale());
ImageWriter.Save(image, "photo-small.webp");                 // the extension selects WebP with default options
```

Three steps cover almost every task:

1. **Load** an image with `ImageReader.Load` (or `LoadAsync`). The format is detected from the content, not the file name.
2. **Process** it with `Mutate` or `Clone`. The lambda receives an `ImageProcessingContext`; its operations chain and run in order.
3. **Save** it with `Save(path)` or one of the `SaveAsWebP`/`SaveAsPng`/`SaveAsJpeg`/`SaveAsBmp`/`SaveAsGif`/`SaveAsPcx` methods.

## `Mutate` or `Clone`

```csharp
using PictureSharp;
using PictureSharp.IO;
using PictureSharp.Processing;

using var original = ImageReader.Load("photo.jpg");

// Changes `original` itself.
original.Mutate(x => x.AutoOrient());

// Returns new images; `original` stays as it is.
using Image small = original.Clone(x => x.Resize(640, 0));
using Image square = original.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
using Image copy = original.Clone();            // plain deep copy
```

Use `Mutate` when you no longer need the input, and `Clone` when you produce several outputs from one source. `Clone` copies the source pixels only when an operation must change them in place, so `Clone(x => x.Resize(...))` never duplicates the full-size image first.

Every operation applies to all frames of an animated image. See [Image processing](image-processing.md) for the full list.

## Disposal

`Image` owns its pixel buffers and implements `IDisposable`. Wrap images in `using` so memory is released as soon as you are done; any use after `Dispose` throws `ObjectDisposedException`.

## Pixels

Pixels are 8-bit `Rgba32` values with straight (not premultiplied) alpha. Higher bit depths (16-bit PNG, for example) are converted when decoding. `System.Drawing.Color` converts to and from `Rgba32` implicitly:

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
using PictureSharp.IO;

try
{
    using var image = ImageReader.Load(upload, new DecoderOptions { MaxPixels = 50_000_000 });
}
catch (ImageFormatException ex)
{
    Console.WriteLine($"Not a valid image: {ex.Message}");
}
```

Decoders never let index errors escape on malicious input; anything unexpected is reported as `ImageFormatException`. For untrusted uploads see [Resource limits and cancellation](limits-and-cancellation.md).

## Next steps

- [Loading and saving](loading-and-saving.md)
- [Encoder options](encoder-options.md)
- [Image processing](image-processing.md)
