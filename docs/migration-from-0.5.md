# Migrating from RasterSharp 0.5.0

This guide takes code written for **RasterSharp 0.5.0** to **PictureSharp 0.7**. The image model and the `Load` →
`Mutate`/`Clone` → `Save` flow are the same. What changes is the package name, the namespaces, a few decoder options and
some defaults. Releases 0.5.5 and 0.5.6 changed the entry points and then changed them back. If you go straight from
0.5.0 to 0.7, you can skip the 0.5.5 layered API (`ImageReader`/`ImageWriter`).

## 1. Package and namespace

| 0.5.0 | 0.7 |
| --- | --- |
| `<PackageReference Include="RasterSharp" … />` | `<PackageReference Include="PictureSharp" Version="0.7.1" />` |
| `using RasterSharp;` | `using PictureSharp;` (added automatically with `ImplicitUsings`, see below) |

The library was renamed in 0.5.2. The `RasterSharp` package receives no further versions.

## 2. Namespaces of options and processing types

In 0.5.0 every public type lived in `RasterSharp`. Now `PictureSharp` holds the model, the options shared by all formats
and the `Image` entry points. Processing and format-specific types have their own namespaces:

| Types | Namespace |
| --- | --- |
| `Image`, `ImageFrame`, `Rgba32`, `ImageFormat`, `ImageInfo`, `DecoderOptions`, `EncoderOptions`, `ImageMetadata`, `ExifOrientation`, `PixelFormat` | `PictureSharp` |
| `ImageProcessingContext`, `ResizeOptions`, `ResizeMode`, `ResamplingFilter`, `RotateMode`, `FlipMode`, `AnchorPosition` and other operation options | `PictureSharp.Processing` |
| `WebPEncoderOptions`, `WebPPreset`, `WebPAlphaFilter`, `WebPCompression`, `WebPConverter`, `SaveAsWebP`/`ToWebP` | `PictureSharp.Formats.WebP` |
| `PngEncoderOptions`, `PngColorType`, `PngFilterStrategy`, `SaveAsPng`/`ToPng` | `PictureSharp.Formats.Png` |
| `JpegEncoderOptions`, `JpegSubsampling`, `SaveAsJpeg`/`ToJpeg` | `PictureSharp.Formats.Jpeg` |
| `GifEncoderOptions`, `GifQuantizer`, `GifDither`, `SaveAsGif`/`ToGif` | `PictureSharp.Formats.Gif` |
| `BmpEncoderOptions`, `PcxEncoderOptions` and their `SaveAs*`/`To*` | `PictureSharp.Formats.Bmp`, `PictureSharp.Formats.Pcx` |

```csharp
using PictureSharp;
using PictureSharp.Formats.WebP;   // SaveAsWebPAsync, WebPEncoderOptions
using PictureSharp.Processing;     // ResizeMode

using var image = await Image.LoadAsync("photo.jpg");
image.Mutate(x => x.AutoOrient().Resize(1600, 0).UnsharpMask());
await image.SaveAsWebPAsync("photo.webp", new() { Quality = 82 });
using Image thumbnail = image.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
```

The package adds `global using PictureSharp;` to projects with `ImplicitUsings`. To turn it off, set
`<PictureSharpImplicitUsings>false</PictureSharpImplicitUsings>`. Windows Forms, WPF and MAUI projects have it off by
default, because their own `Image` types would be ambiguous.

## 3. Entry points are extension members

`Image.Load`, `LoadAsync`, `Identify`, `IdentifyAsync`, `DetectFormat`, `Image.CreateAnimation`, `image.Save`,
`SaveAsync`, `Encode`, `Mutate` and `Clone`, and the members of `ImageFormat` such as `All`, `FromExtension`,
`FromMimeType`, `CanDecode` and `CanEncode`, are now C# 14 extension members in `PictureSharp`. Code that calls them
compiles unchanged once the namespace is imported. Assemblies compiled against 0.5.0 must be **recompiled**, because
binary compatibility was not kept.

New overloads take `DecoderOptions` with `Identify`, and a `CancellationToken` with the synchronous calls. A `byte[]`
still binds to the non-copying `ReadOnlyMemory<byte>` overload.

## 4. Decoder options moved to format sub-options

| 0.5.0 | 0.7 |
| --- | --- |
| `new DecoderOptions { StrictChecksums = true }` | `new DecoderOptions { Png = new() { StrictChecksums = true } }` |
| `new DecoderOptions { WebPBypassFiltering = true }` | `new DecoderOptions { WebP = new() { BypassFiltering = true } }` |
| `new DecoderOptions { WebPFancyUpsampling = false }` | `new DecoderOptions { WebP = new() { FancyUpsampling = false } }` |

ICO options are in `DecoderOptions.Ico`, which was added in 0.5.1.

## 5. Behavior changes to check

- **Limits (0.5.3).** Loading rejects more than 64 MiB of decompressed metadata (`MaxMetadataBytes`) and animations with
  more than 65,536 frames (`MaxFrames`), and throws `ImageFormatException`. `MaxFrames` counts stored frames, including
  with `FirstFrameOnly`. Raise these limits for unusual inputs. `MaxInputBytes` is also new, and it has no default
  limit. PNG now keeps the first `iCCP`/`eXIf` chunk.
- **Cancellation (0.5.3).** Tokens reach the CPU work of codecs and processing. A canceled `Clone` leaves the source
  unchanged, and a save to a path writes the file only after encoding has finished; `EditMetadata` replaces files
  atomically. See [what a canceled call leaves behind](limits-and-cancellation.md#what-a-canceled-call-leaves-behind).
- **HDR (0.6.9).** `image.Hdr` stays set after crops, resizes, rotations and flips. In 0.6.5–0.6.8 it became null after
  any `Mutate`. Gain maps are read only with `DecoderOptions.LoadGainMap`, so code that does not set it is unaffected.
- **Results.** Default decoding and encoding results are unchanged. Linear-light processing (`LinearLight`) and color
  conversion (`ColorConversion`, `ConvertToSrgb`) are opt-in. In 0.6.7 HDR reconstruction moved to deterministic `float`
  functions, which differ from 0.6.5 by a few ulp.

## 6. What 0.5.0 code can now use

- [ICO](ico.md) with multiple sizes, and [thumbnails while loading](loading-and-saving.md#thumbnails-while-loading)
  (`TargetSize`, with reduced JPEG decoding).
- [Limits and cancellation](limits-and-cancellation.md), and [sequential animation I/O](animation.md)
  (`OpenFrameReaderAsync`, `CreateFrameWriter`).
- [Typed EXIF and metadata editing without recompression](metadata.md) (`ReadMetadata`, `EditMetadata`).
- [Color management](color-management.md), and graphics operations: trimming, levels, histograms, blend modes and masks.
- [Adapter packages](adapters.md) for System.Drawing, SkiaSharp and ASP.NET Core.
- [HDR gain maps](hdr.md) in JPEG.
- [Format plugins](extending-picturesharp.md), including gain maps and metadata in plugin formats.
