# Migrating from PictureSharp 0.8.5 to 0.9.0

0.9.0 adds 16-bit image processing and changes direct pixel access and several processing settings.
Use the steps below when updating an application from 0.8.5.

## 1. Update packages together

Update `PictureSharp` and any Drawing, Skia or ASP.NET Core adapters to **0.9.0**. If you use format plugins,
update those too:

| Plugin | Version for core 0.9.0 |
| --- | --- |
| PictureSharp.Tiff | 0.7.4 |
| PictureSharp.Heif | 0.7.0 |
| PictureSharp.Jxl | 0.7.8 |

Earlier plugin builds are rejected when registered. Formats.Extra has not been migrated in this release.
Rebuild the application after updating the packages.

## 2. Update direct pixel access

`Image` remains non-generic. `image.PixelType` is `Rgba32` (four bytes per pixel) or `Rgba64` (eight bytes), shared by
all frames. Both use straight alpha. `Load` still returns `Rgba32` by default; opt into the stored precision:

```csharp
using PictureSharp;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;

using Image image = Image.Load("input.png", new DecoderOptions { PreserveBitDepth = true });
image.Mutate(x => x.Exposure(0.25f).Resize(1600, 0));
image.SaveAsPng("output.png", new PngEncoderOptions { BitDepth = PngBitDepth.Auto });
```

An 8-bit source stays `Rgba32` even with `PreserveBitDepth`. To select storage explicitly, use
`new Image(width, height, PixelType.Rgba64)` or `image.Mutate(x => x.ConvertPixelType(PixelType.Rgba64))`.
Widening multiplies samples by 257; narrowing rounds `(v + 128) / 257`. An 8→16→8 round-trip is exact.
`ConvertPixelType(PixelType.Rgba32, dither: true)` optionally dithers RGB; alpha is rounded. Encoders do not dither.

| Before | 0.9.0 |
| --- | --- |
| `image.GetRowSpan(y)` | `image.GetRowSpan<Rgba32>(y)` or `GetRowSpan<Rgba64>(y)` |
| `GetPixelSpan()`, `GetPixelMemory()` | `GetPixelSpan<TPixel>()`, `GetPixelMemory<TPixel>()` |
| Pixel-struct `CopyPixelDataTo/From` and `LoadPixelData` | Generic forms with `Rgba32` or `Rgba64` (type inference works for typed spans) |
| `image[x, y]` | Still valid on `Rgba32`; use `GetPixel<Rgba64>(x, y)` / `SetPixel(x, y, pixel)` on `Rgba64` |
| `IFrameProcessor` | `IFrameProcessor<TPixel>` and `ApplyProcessor<TPixel>` |

Requesting the wrong span/pixel type throws `InvalidOperationException`; an unsupported unmanaged type throws
`NotSupportedException`. Buffer replacement detaches previously obtained spans and memories. Raw byte APIs also
support `Rgb48`, `Rgba64`, `Gray16` and `Rgba64Premultiplied` layouts; see
[Pixels and interop](pixels-and-interop.md). Raw byte layouts use little-endian 16-bit samples; typed `Rgba64` spans use native endianness.

Adding an 8-bit frame to a 16-bit image widens it. Adding a 16-bit frame to an 8-bit image requires explicit conversion
first, including `AddClone`. Colors in processing options are now `Rgba64`; `Rgba32` and `System.Drawing.Color`
convert implicitly. The three-argument `Image(width, height, Rgba32)` constructor still creates an 8-bit image; for a
16-bit fill use `new Image(width, height, PixelType.Rgba64, color)`.

## 3. Convert sample settings to 0–1

Sample-level settings now use 0–1 and names that make the unit explicit. Divide an old 8-bit value by **255f**, not
255, to keep the former result. Percentages, radii, exposure and pixel counts keep their units.

| Before | 0.9.0 |
| --- | --- |
| `LevelsOptions.InputBlack`, `InputWhite`, `OutputBlack`, `OutputWhite` | Corresponding `*Normalized` properties; e.g. `InputBlackNormalized = 20 / 255f` |
| `Curves(points)` with 0–255 coordinates | `CurvesNormalized(points)` with 0–1 coordinates |
| `AutoCropOptions.AlphaThreshold`, `Tolerance` | `AlphaThresholdNormalized`, `ToleranceNormalized` |
| `UnsharpMask(threshold: 3)` | `UnsharpMask(thresholdNormalized: 3 / 255f)` |
| `histogram.Mean(channel)` | `MeanNormalized(channel)` returning 0–1 |
| `histogram.Percentile(channel, percent)` | `PercentileNormalized(channel, percent)` returning 0–1; percentile argument remains 0–100 |

Histograms still default to 256 bins. `new HistogramOptions { FullResolution = true }` uses 65536 bins for `Rgba64`
and 256 for `Rgba32`; inspect `BinCount`. Analysis and automatic corrections count 16-bit samples at full precision.

## 4. Choose whether to keep 16 bits

PNG supports 16-bit grayscale, gray/alpha, RGB and RGBA, Adam7 and APNG. `PngEncoderOptions.BitDepth` selects:

- `Auto`: write 16 bits if a 16-bit sample needs them; widened 8-bit content can be stored at 8 bits.
- `UpTo8`: round to 8 bits and allow the existing palette choices.
- `Sixteen`: write 16-bit samples, widening an 8-bit image exactly. Explicit palette output is incompatible.

PNG `sBIT` is `PngSignificantBits` format metadata. It never rescales samples or changes `PreserveBitDepth`. It is
written only if sample depth and color type match the output. Remove it after edits if the source-precision claim
no longer applies: `image.Metadata.RemoveFormatMetadata<PngSignificantBits>()`.

Other built-in encoders round `Rgba64` to 8-bit output, including ICO. Plugins can support deeper output through
`IImageFormatEncoder.SupportsPixelType`. Use the plugin versions listed above for TIFF, HEIF Main 10 and JPEG XL output.

`ColorConversion = ToSrgb` converts a deeper source at 16-bit precision before narrowing once if the caller requested
`Rgba32`. This opt-in path can change the previous 8-bit result for deeper sources. `ConvertToSrgb` preserves a
16-bit image's storage and computes RGB in float; alpha is unchanged. Integer precision does not imply PQ/HLG or
floating-point HDR support. Use `HdrImage` for HDR ranges; `ToneMappingOptions.PixelType` selects tone-map output.

Skia supports integer `Rgba16161616` with straight or premultiplied alpha; F16 and BGRA64 are unsupported.
System.Drawing exports 32bpp and rounds a 16-bit image. See [Adapters](adapters.md).

## 5. Review memory limits and command-line options

`MaxPixels` counts a 16-bit pixel twice, including animation frames and gain maps. A default `ToSrgb` load of a deeper
source is also charged at 16 bits during conversion. Revisit limits for workloads that previously decoded to 8 bits.

If you maintain a format plugin, follow the separate [plugin migration guide](plugin-spi.md#090-16-bit-pixels).
It covers pixel reservations, encoder support and embedded image parts.

The CLI adds `convert input.png output.png --depth auto|8|16`, default `8`. `auto` preserves source depth and lets PNG
choose its output depth; `16` also widens an 8-bit input and forces 16-bit PNG output. Other output formats follow
their encoder's supported precision.
