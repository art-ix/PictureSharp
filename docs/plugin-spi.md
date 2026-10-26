# Stable plugin SPI

From PictureSharp 0.8.5 the types plugins build on are stable: until 2.0 they only grow. A plugin built against 0.8.5
keeps compiling and running on every later 0.x and 1.x release, so a plugin can make its own 1.0 promise on top of it.
0.8.5 itself broke the promise made in 0.8.2 once, for `ImageInfo` (see [0.8.5: one-time change](#085-one-time-change)).
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

The model plugins create and fill: `Image`, `ImageFrame`, `ImageFrameCollection` and `Rgba32`.

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

A plugin depends on the lowest PictureSharp version whose API it uses (`PictureSharp >= 0.8.5` in its package) and
raises it only when it starts using something newer. 0.8.5 is the first version a plugin can build on: `AddFormat` and
`ReplaceFormat` throw `NotSupportedException` for a module whose assembly was built against an older PictureSharp,
because it would fail inside `Identify` or report an `ImageInfo` without its required members.

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
