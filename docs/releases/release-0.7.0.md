# PictureSharp 0.7.0

Format plugins can save through their own configuration and supply HDR gain maps and metadata. No breaking API changes
since 0.6.9.

- **Plugin SPI:** `EncoderOptions.Configuration` selects the configuration used for saving. Plugins can attach gain
  maps (`GainMapMetadata`, `GainMapImage`, `ImageDecodeContext.AttachGainMap`), so `ToHdr` and `ToneMap` work for
  their files, and can read or rewrite metadata without pixels (`IImageFormatMetadataCodec`).
  See [Extending PictureSharp](../extending-picturesharp.md).
- **CLI sample:** new `metadata` command, `resize -stream` for long animations, and `-srgb`, `-max-pixels`, `-threads`.
- **Docs:** [migration guide from RasterSharp 0.5.0](../migration-from-0.5.md) and
  [integration measurements](../performance.md#integration-measurements-070), with no regressions against 0.6.9.

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle, no-AVX2 and scalar runs, trimming and Native AOT) and on
ARM64. Not verified against Apple's ImageIO.
