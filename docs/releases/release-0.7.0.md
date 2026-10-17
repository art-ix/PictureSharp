# PictureSharp 0.7.0

Format plugins can now save through their own configuration and can supply HDR gain maps and metadata. This release
also adds end-to-end tests across all areas, new CLI commands, a migration guide from 0.5.0 and integration
measurements. There are no breaking changes to the public API since 0.6.9.

- **Saving with a plugin configuration:** `EncoderOptions.Configuration` selects the configuration whose encoder
  writes the file, as `DecoderOptions.Configuration` already does for loading. `Save`, `SaveAsync`, `Encode`, the
  `SaveAs*`/`To*` shortcuts and `CreateFrameWriter` use it. A plugin's `SaveAsXxx` therefore works without replacing
  `ImageConfiguration.Default`. When it is null, as by default, `ImageConfiguration.Default` is used as before.
- **HDR gain maps from plugins:** `GainMapMetadata.ParseIso` and `ParseApple` parse ISO 21496-1 and Apple gain map
  metadata independently of JPEG. `ImageDecodeContext.AttachGainMap` attaches the map as a lazily decoded
  `GainMapImage`, and `DeclareGainMap` reports a map that cannot be used. `ToHdr`, `ToneMap`, `Identify`
  (`HdrInfo.Headroom`) and the geometric edits from 0.6.9 then work for the plugin's files as for JPEG. The base color
  comes from the returned image's ICC profile, or from its chromaticities and gamma (an nclx description without ICC).
- **Metadata from plugins:** `IImageFormatModule.MetadataCodec` (`IImageFormatMetadataCodec`) makes `ReadMetadata`
  read ICC, EXIF and XMP without decoding pixels, and optionally makes `EditMetadata` rewrite them. The library still
  checks limits and cancellation, applies the edit and writes files atomically. The member has a default
  implementation, so existing plugins compile and run unchanged.
- **Plugin contract:** [Extending PictureSharp](../extending-picturesharp.md#orientation-color-and-threads) now
  describes how container orientation, color descriptions and thread limits interact with the library, so a plugin
  does not rotate an image twice. These SPI additions were requested by the planned `PictureSharp.Heif` plugin.
- **CLI sample:** the new `metadata` command prints or edits metadata without recompression (`-strip-gps`, `-strip`,
  `-orientation`). `resize -stream` converts long GIF, APNG and WebP animations one frame at a time. `convert`,
  `resize`, `thumbnail` and `trim` take `-srgb`, `-max-pixels` and `-threads`. See [the CLI](../cli.md).
- **Documentation:** a [migration guide from RasterSharp 0.5.0](../migration-from-0.5.md) covers the package and
  namespace rename, the moved decoder options and the behavior changes since 0.5.0. The [HDR guide](../hdr.md) has a
  section on gain maps from plugins.

**Measurements.** Complete paths on 12 MP inputs (load, thumbnail, orientation with ICC conversion and resize to WebP,
metadata read and edit, HDR tone mapping, HDR resize and save) take the same time and allocate the same memory as in
0.6.9. Cancellation latency stays at or below 0.3 ms (median) and peak memory is unchanged. Retained memory when writing
or reading 10,000-frame animations stays bounded. Frame readers apply `MaxPixels` to the whole sequence, so long
animations need a higher limit ([measurements](../performance.md#integration-measurements-070)).

The adapter packages are released at the same version; their implementation is unchanged.

**Validation.** On linux-x64, 7,206 of 7,213 tests passed; the 7 skipped are Windows-only GDI+ tests. All 192 oracle
tests against cwebp/dwebp 1.6.0, libultrahdr v2.0.2, Pillow and ImageMagick ran and passed. The library tests also
passed with AVX2 disabled and with all intrinsics disabled. Local package consumers passed full trimming and Native
AOT for the library and adapters, including a plugin that uses the new SPI. On ARM64 (RK3588, Linux), 6,895 of 7,075
tests passed with 180 skipped (no oracle tools on the board, GDI+), and the library tests also passed with all
intrinsics disabled. HDR results have not been compared with Apple's ImageIO or Core Image, so the Apple gain map
model remains verified against the Linux references only.
