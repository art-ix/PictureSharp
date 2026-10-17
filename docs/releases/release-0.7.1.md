# PictureSharp 0.7.1

A plugin SPI addition requested by the HEIF plugin. No breaking API changes since 0.7.0.

- **Gain map models:** `ImageDecodeContext.AttachGainMap(metadata, map, detected)` reports every gain map model a file
  declares for the attached map. A HEIC file from iOS 18 (ISO 21496-1 and Apple metadata for one map) now gets the same
  `Image.Hdr.Detected` as the JPEG of the same photo. `detected` must include `metadata.Kind`; the existing overload is
  unchanged. See [Extending PictureSharp](../extending-picturesharp.md#hdr-gain-maps).

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle, no-AVX2 and scalar runs, trimming and Native AOT). Not
run on ARM64 or against Apple's ImageIO; the change adds no platform-specific code.
