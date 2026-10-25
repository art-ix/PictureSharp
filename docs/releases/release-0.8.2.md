# PictureSharp 0.8.2

Three requests from the plugins: embedded images can no longer recurse without end, plugins keep metadata only they
understand, and the plugin SPI is declared stable until 2.0. No breaking API changes since 0.8.1; results for valid
files are unchanged.

- **Nesting limit for embedded images.** Codecs for embedded images are chosen by signature, so a corrupt container
  could embed itself (a TIFF strip at offset 0, found by fuzzing the TIFF plugin) and decode itself again until the
  stack overflowed and the process ended. Images now nest at most 8 levels below the loaded file, through
  `ImageDecodeContext.DecodeEmbedded`, `DecodeEmbeddedPart` and the built-in containers (ICO, BMP with JPEG or PNG);
  a deeper one throws `ImageFormatException` naming the format that asked for it. The limit is fixed, not an option.
  See [Limits and cancellation](../limits-and-cancellation.md).
- **Format metadata.** A plugin keeps data only its format understands, such as an XBM or cursor hotspot, in
  `ImageMetadata` as a record derived from `PictureSharp.Formats.FormatMetadata`, at most one per type:
  `GetFormatMetadata<T>()`, `SetFormatMetadata(value)`, `RemoveFormatMetadata<T>()` and the read-only list
  `FormatMetadata`. Operations that move pixels (resize, crop, pad, rotate, flip, `AutoOrient` when it rotates,
  `AutoCrop`, a custom processor with another output size, `DecoderOptions.TargetSize`) remove the entries, since the
  library cannot adjust them; color and pixel operations keep them. `IsEmpty`, `Clear()` and `Clone()` include them,
  `IgnoreMetadata` is honoured by the plugin. See [Metadata](../metadata.md#format-metadata) and
  [Extending PictureSharp](../extending-picturesharp.md#format-metadata).

  ```csharp
  public sealed record XbmMetadata((int X, int Y) Hotspot) : FormatMetadata;

  image.Metadata.SetFormatMetadata(new XbmMetadata((3, 1)));                      // in the decoder
  (int X, int Y)? hotspot = image.Metadata.GetFormatMetadata<XbmMetadata>()?.Hotspot; // in the encoder
  ```

- **Stable plugin SPI.** From 0.8.2 the plugin contracts (`IImageFormatModule`, `IImageFormatDecoder`,
  `IImageFormatEncoder<TOptions>`, `IImageFormatMetadataCodec`, `ImageDecodeContext`, the gain map types and others)
  and the model plugins fill (`Image`, `ImageFrame`, `Rgba32`, `ImageMetadata`, `DecoderOptions`, `EncoderOptions`,
  `ImageInfo`) only grow until 2.0: interface members are added only with a default implementation and new parameters
  as overloads. A plugin can depend on PictureSharp 0.8.2 for its own 1.0. The list, the rules and the minimum-version
  policy are in [Stable plugin SPI](../plugin-spi.md); `tests/assets/PluginSpi.approved.txt` and
  `PluginSpiSnapshotTests` enforce them. The earlier note that optional parameters were additive is corrected: they
  break compiled plugins.

Checked with the plugins on a local 0.8.2 package: PictureSharp.Tiff 3878/3878 tests and PictureSharp.Heif build
unchanged; PictureSharp.Formats.Extra keeps the XBM hotspot from load to save through `FormatMetadata`.

Verification (linux-x64): Release build without warnings; 7652 tests passed, 7 skipped (GDI+). No ARM64 run; the
changes contain no platform-specific code.
