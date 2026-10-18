# PictureSharp 0.7.6

A plugin SPI addition requested by three plugins (TIFF pages, DDS mip levels and layers, HEIF items). No breaking API
changes since 0.7.5; built-in formats decode exactly as before.

- **Plugin decoder options:** a plugin derives its settings from `FormatDecoderOptions`; callers pass them in
  `DecoderOptions.FormatOptions` and the plugin reads its own record with `DecoderOptions.GetFormatOptions<T>()` in
  `Identify`, `Decode` and its metadata codec. One `DecoderOptions` can carry settings for several plugins: each plugin
  ignores the others' records and built-in formats ignore them all. The list holds at most one record per type and is
  copied when set. No reflection is involved, so trimming and Native AOT are unaffected. See
  [Extending PictureSharp](../extending-picturesharp.md#decoder-options).

  ```csharp
  using var page = Image.Load(path, new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = 2 }] });
  ```

  `TiffDecoderOptions` stands for a plugin's own record; plugins adopt the mechanism in their next versions.

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle and slow tests, trimming and Native AOT). Not run
without AVX2, without hardware intrinsics or on ARM64; the change adds no platform-specific code.
