# Migrating from RasterSharp 0.5.0

The image model and the `Load` → `Mutate`/`Clone` → `Save` flow of **RasterSharp 0.5.0** are unchanged in
**PictureSharp 0.7**; the package name, namespaces, a few decoder options and some defaults differ. Going straight from
0.5.0, skip the short-lived 0.5.5 layered API (`ImageReader`/`ImageWriter`).

## 1. Package and namespace

| 0.5.0 | 0.7 |
| --- | --- |
| `<PackageReference Include="RasterSharp" … />` | `<PackageReference Include="PictureSharp" Version="0.7.1" />` |
| `using RasterSharp;` | `using PictureSharp;` (added automatically with `ImplicitUsings`) |

The library was renamed in 0.5.2; `RasterSharp` gets no further versions.

## 2. Namespaces of options and processing types

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

The implicit `global using PictureSharp;` is off in Windows Forms, WPF and MAUI projects and can be disabled with
`<PictureSharpImplicitUsings>false</PictureSharpImplicitUsings>`.

## 3. Entry points are extension members

`Load`, `LoadAsync`, `Identify`, `DetectFormat`, `CreateAnimation`, `Save`, `Encode`, `Mutate`, `Clone` and the
`ImageFormat` lookups are C# 14 extension members in `PictureSharp`. Source compiles unchanged, but assemblies built
against 0.5.0 must be **recompiled**. New overloads add `DecoderOptions` to `Identify` and a `CancellationToken` to the
synchronous calls; `byte[]` still binds to the non-copying `ReadOnlyMemory<byte>` overload.

## 4. Decoder options moved to format sub-options

| 0.5.0 | 0.7 |
| --- | --- |
| `new DecoderOptions { StrictChecksums = true }` | `new DecoderOptions { Png = new() { StrictChecksums = true } }` |
| `new DecoderOptions { WebPBypassFiltering = true }` | `new DecoderOptions { WebP = new() { BypassFiltering = true } }` |
| `new DecoderOptions { WebPFancyUpsampling = false }` | `new DecoderOptions { WebP = new() { FancyUpsampling = false } }` |

## 5. Behavior changes to check

- **Limits (0.5.3).** More than 64 MiB of decompressed metadata (`MaxMetadataBytes`) or 65,536 stored frames
  (`MaxFrames`, also with `FirstFrameOnly`) throws `ImageFormatException`. `MaxInputBytes` is new and unlimited by
  default. PNG keeps the first `iCCP`/`eXIf` chunk.
- **Cancellation (0.5.3).** Tokens reach codec and processing work; a canceled `Clone` leaves the source unchanged and
  path saves write only after encoding. See
  [what a canceled call leaves behind](limits-and-cancellation.md#what-a-canceled-call-leaves-behind).
- **HDR (0.6.9).** `image.Hdr` survives crops, resizes, rotations and flips (0.6.5–0.6.8 dropped it on any `Mutate`).
  Gain maps load only with `LoadGainMap`.
- **Results.** Default decoding and encoding output is unchanged; linear light and color conversion are opt-in. HDR
  reconstruction differs from 0.6.5 by a few ulp since 0.6.7.

## 6. What 0.5.0 code can now use

[ICO](ico.md), [thumbnails while loading](loading-and-saving.md#thumbnails-while-loading),
[limits and cancellation](limits-and-cancellation.md), [frame-by-frame animation](animation.md),
[metadata editing without recompression](metadata.md), [color management](color-management.md), graphics operations
(trimming, levels, histograms, blend modes, masks), [adapter packages](adapters.md), [HDR gain maps](hdr.md) and
[format plugins](extending-picturesharp.md).
