# PictureSharp 0.8.5

`Image.Identify` now describes the colour layout of a file without decoding it: colour model, channels, sample type,
alpha mode, ICC presence and H.273 (CICP) colour description, plus whether it is lossless. To give `ImageInfo` one
member per fact, **this release breaks the plugin SPI and `ImageInfo` despite the patch number**: plugins need a new
build against 0.8.5, and older plugin builds are rejected at registration.

- **Colour description in `Identify`.** `ImageInfo.Color` (`ImageColorInfo`) reports the model after the format's own
  colour transform (`Gray`, `Rgb`, `Palette`, `Cmyk`, `Lab`, `Other`, so a YCbCr JPEG is `Rgb` and a YCCK JPEG
  `Cmyk`), `ChannelCount`, `BitsPerSample`, `SampleFormat` (unsigned, signed, float), `AlphaMode` (`None`, `Straight`,
  `Premultiplied`), `HasIccProfile` and `Cicp` (`CicpColorDescription`, with `IsHdrTransfer` for PQ and HLG). Built-in
  formats read them from headers they already parse: PNG cICP, sRGB and iCCP, the BMP V4/V5 colour space and profile,
  the GIF ICC extension, JPEG and WebP ICC chunks. They are filled with `IgnoreMetadata` too and copy nothing, so they
  reserve no metadata (JPEG `Identify` keeps counting its ICC chunks, as before). `Load` does not apply CICP.

  ```csharp
  ImageInfo info = Image.Identify("photo.png");
  if (info.Color.Cicp is { IsHdrTransfer: true })
  {
      Console.WriteLine($"HDR {info.Color.Model}, {info.Color.BitsPerSample}-bit");
  }
  ```

- **`ImageInfo` without duplicates or pretend defaults.** `Compression` (`Unknown`, `Lossless`, `Lossy`, `Mixed`)
  replaces the WebP-only `WebPCompression`; `Resolution` is null when the file declares none instead of 96 DPI;
  `FrameCount` is null instead of 0 when only decoding could tell; the gain map members drop "Hdr", which next to PQ
  and HLG meant something else. `BitsPerSample` reports the stored depth, so it changes from 0.8.4's `BitsPerChannel`
  for GIF (index depth of the largest colour table), BMP (palette depth; widest mask, e.g. 6 for 5-6-5), ICO DIB entries
  (palette depth) and PCX palettes below 8 bits.

  | 0.8.4 | 0.8.5 |
  | --- | --- |
  | `info.HasAlpha` | `info.Color.HasAlpha` (from `Color.AlphaMode`) |
  | `info.BitsPerChannel` | `info.Color.BitsPerSample` |
  | `info.WebPCompression`, `PictureSharp.Formats.WebP.WebPCompression` | `info.Compression`, `ImageCompression` |
  | `info.HorizontalResolution`, `info.VerticalResolution` | `info.Resolution?.Horizontal`, `.Vertical` (null: not declared) |
  | `info.FrameCount` 0 | `info.FrameCount` null |
  | `info.Hdr`, `image.Hdr`, `HdrInfo` | `info.GainMap`, `image.GainMap`, `GainMapInfo` |
  | `GainMapMetadata.ToHdrInfo(size)` | `GainMapMetadata.ToGainMapInfo(size)` |

  `ImageMetadata.HorizontalResolution`/`VerticalResolution` and `Image.HasAlpha` are unchanged.

- **Plugins.** `ImageInfo.Color` and `Compression` are `required`, so the compiler shows a plugin what to report. A
  plugin compiled against an older PictureSharp would fail inside `Identify` with `MissingMethodException`, so
  `ImageConfigurationBuilder.AddFormat` and `ReplaceFormat` now throw `NotSupportedException` naming the plugin and
  the version it was built against. [Stable plugin SPI](../plugin-spi.md) has the migration table and restates the
  promise from 0.8.5; [Extending PictureSharp](../extending-picturesharp.md#rules-for-codecs) the rules for `Identify`.

- **CLI.** `info` prints the colour description and the compression.

- **Plugin releases.** PictureSharp.Tiff 0.7.3 is released together with this version. PictureSharp.Heif and
  PictureSharp.Formats.Extra follow in their own releases; until then, applications using them stay on 0.8.4, since
  0.8.5 rejects their current builds at registration.

Tests: a synthetic corpus in `tests/assets/corpus/identify/` (67 files: every PNG colour type, cICP, sRGB and iCCP
combinations, JPEG Adobe, YCCK and incomplete ICC, WebP mixed animations, GIF table depths, BMP bitfields and V4/V5
colour spaces, ICO DIB depths, PCX planes) with expectations in `identify_reference.json`, cross-checked with Pillow.
Pixel results are unchanged.
