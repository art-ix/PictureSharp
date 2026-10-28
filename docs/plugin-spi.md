# Stable plugin SPI

From PictureSharp 0.9.0 the types plugins build on only grow until 2.0. Before 1.0 a change that gives a clearly
better design may still break them, with a raised minimum version and a migration note: 0.8.5 did so for `ImageInfo`
(see [0.8.5](#085-one-time-change)) and 0.9.0 for 16-bit pixels (see [0.9.0](#090-16-bit-pixels)).
[Extending PictureSharp](extending-picturesharp.md) shows how the types are used.

## What is stable

The plugin contracts:

| Area | Types |
| --- | --- |
| Registration | `IImageFormatModule`, `ImageFormat` (`Create`), `ImageConfigurationBuilder` (`AddFormat`, `ReplaceFormat`) |
| Decoding | `IImageFormatDecoder`, `ImageDecodeContext`, `DecoderOptions`, `FormatDecoderOptions`, `EmbeddedPartOptions` |
| Identification | `ImageInfo`, `ImageColorInfo`, `ImageColorModel`, `ImageSampleFormat`, `ImageAlphaMode`, `ImageCompression`, `CicpColorDescription`, `ImageResolution` |
| Encoding | `IImageFormatEncoder`, `IImageFormatEncoder<TOptions>`, `EncoderOptions` |
| Metadata | `IImageFormatMetadataCodec`, `ImageMetadata`, `FormatMetadata`, `MetadataKinds` |
| HDR gain maps | `GainMapMetadata`, `GainMapImage`, `EncodableGainMap`, `GainMapExtensions.GetGainMap`, `GainMapInfo`, `HdrGainMapKinds`, `HdrColorSpace` |
| Errors | `ImageFormatException`; the exception rules below |

The model plugins create and fill: `Image`, `ImageFrame`, `ImageFrameCollection`, `Rgba32`, `Rgba64` and `PixelType`.

Every public member of these types is covered. Types that appear only in their signatures, such as
`JpegDecoderOptions` in `EmbeddedPartOptions` or `ResamplingFilter` in `DecoderOptions`, stay as long as a stable
member uses them; their other members follow the general rules for the rest of the API.

## What "only grows" means

- New types, members and overloads may be added. Existing members keep their names, parameters, return types,
  nullability and accessibility.
- Interfaces that plugins implement get new members only with a default implementation, so existing plugins need no
  change. A new requirement comes as a new interface.
- New parameters come as new overloads, not as optional parameters, which would break compiled plugins.
- What the library does around a plugin keeps its meaning: limits and cancellation, `ReservePixels` and
  `ReserveMetadata`, `TargetSize`, EXIF orientation and color conversion after `Decode`, the nesting limit of embedded
  images, and gain map handling. It changes only to fix a bug, called out in the release notes.
- Exceptions: corrupt data is reported as `ImageFormatException`, a valid but unsupported variant as
  `NotSupportedException` and cancellation as `OperationCanceledException`. `IndexOutOfRangeException`,
  `ArgumentException`, `InvalidOperationException`, `ArithmeticException` (with `OverflowException`) and
  `InvalidCastException` thrown by a decoder keep being reported to callers as `ImageFormatException`.

Breaking changes to these types wait for 2.0 and come with a migration note. Nothing is marked experimental: every
stable type already has a plugin using it.

## The rest of the API

Processing (`Mutate`, `Clone` and their operations), the built-in formats' options, adapters, frame readers and
writers follow the general rules: before 1.0 they may change in a minor version, with a migration note in the release
notes.

## Minimum PictureSharp version

A plugin depends on the lowest PictureSharp version whose API it uses (`PictureSharp >= 0.9.0` in its package) and
raises it only when it starts using something newer. 0.9.0 is the first version a plugin can build on: `AddFormat` and
`ReplaceFormat` throw `NotSupportedException` for a module whose assembly was built against an older PictureSharp,
because it would ignore `DecoderOptions.PreserveBitDepth` or fail inside a decode with `MissingMethodException`.

## 0.9.0: 16-bit pixels

An `Image` stores `Rgba32` or, for 16 bits per channel, `Rgba64` pixels, chosen by `Image.PixelType` and shared by all
frames (design: [docs/proposals/high-bit-depth.md](proposals/high-bit-depth.md)). Plugins need a new build against
0.9.0:

| 0.8.5 | 0.9.0 |
| --- | --- |
| `ReservePixels(width, height, frames)` | `ReservePixels(width, height, pixelType, frames)`; a 16-bit pixel counts twice against `MaxPixels`, and the returned image must have been reserved with its pixel type (or come from nested decodes) |
| `GetPixelSpan()`, `GetRowSpan(y)`, `GetPixelMemory()`, `CopyPixelDataTo/From(Span<Rgba32>)`, `LoadPixelData(ReadOnlySpan<Rgba32>, …)` | generic forms with the type argument: `GetPixelSpan<Rgba32>()`, `LoadPixelData<TPixel>(…)`; a mismatched type throws `InvalidOperationException` |
| — | `DecoderOptions.PreserveBitDepth`: decode sources with more than 8 bits per sample to `new Image(width, height, PixelType.Rgba64)` or `LoadPixelData<Rgba64>` |
| — | `IImageFormatEncoder.SupportsPixelType(PixelType)`, default `Rgba32` only: other images reach the encoder as an 8-bit copy rounded to the nearest value |
| `GainMapImage(size)` | `GainMapImage(size, pixelType = PixelType.Rgba32)` and `GainMapImage.PixelType`: a map stored with more than 8 bits can decode to `Rgba64`; it is charged with that type when attached, and a decoded map of another type is reported as corrupt |
| `ColorConversion = ToSrgb` decoded at the requested depth | with `PreserveBitDepth` off, the decoder receives `PreserveBitDepth = true`; the library converts the 16-bit result and rounds it to `Rgba32` once. A decoder that renders to sRGB itself reports no profile afterwards and is not converted again |

## 0.8.5: one-time change

0.8.5 replaced `ImageInfo` members that duplicated each other or used a default to mean "unknown". Plugins need a new
build against 0.8.5:

| 0.8.4 | 0.8.5 |
| --- | --- |
| `ImageInfo.HasAlpha` | `Color.HasAlpha`, computed from `Color.AlphaMode` |
| `ImageInfo.BitsPerChannel` (8 by default) | `Color.BitsPerSample`, required; the index depth for palettes |
| `ImageInfo.WebPCompression`, `enum WebPCompression` | `ImageInfo.Compression` (`Unknown`, `Lossless`, `Lossy`, `Mixed`), required |
| `ImageInfo.HorizontalResolution`, `VerticalResolution` (96 by default) | `ImageInfo.Resolution`, null when the file declares none |
| `ImageInfo.FrameCount` 0 for "unknown" | `FrameCount` null for "unknown"; 0 is rejected |
| `ImageInfo.Hdr`, `Image.Hdr`, `HdrInfo` | `ImageInfo.GainMap`, `Image.GainMap`, `GainMapInfo` |
| `GainMapMetadata.ToHdrInfo(Size)` | `GainMapMetadata.ToGainMapInfo(Size)` |
| — | `ImageInfo.Color` (`ImageColorInfo`): model, channels, sample format, alpha mode, ICC presence, CICP; required |

## How the promise is kept

`tests/assets/PluginSpi.approved.txt` lists every stable type and member. `PluginSpiSnapshotTests` fails when a line
disappears or changes, or when an interface gets a new member without a default implementation. An addition fails too,
until it is reviewed and copied into the approved file.
