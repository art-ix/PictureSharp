# PictureSharp 0.6.2

Explicit color management to sRGB and optional linear-light resizing and compositing. Default results are unchanged.

- `image.Mutate(x => x.ConvertToSrgb())` and `DecoderOptions.ColorConversion = ColorConversionMode.ToSrgb` convert
  pixels to sRGB and then remove the profile and color description, so nothing is converted twice. Every frame of an
  animation is converted, alpha is kept, and a canceled conversion commits nothing. `IcoFile.LoadAll` honors the
  option too.
- Supported: ICC v2 and v4 RGB and Gray **matrix/TRC** profiles (`curv` identity/gamma/table, `para` types 0–4) with
  the relative colorimetric intent, no black-point compensation and clipping to sRGB. LUT-based, CMYK, Lab,
  device-link and other unsupported profiles throw `NotSupportedException`; malformed profiles throw
  `ImageFormatException`. `ColorConversion` with `IgnoreMetadata` throws `ArgumentException`; frame readers do not
  support it.
- PNG `gAMA`, `cHRM` and `sRGB` are read into `ImageMetadata.Gamma`, `Chromaticities` (`CieChromaticities`) and
  `SrgbRenderingIntent` (`RenderingIntent`), written by the PNG/APNG encoders with the ICC selection, and edited by
  `Image.EditMetadata` without recompression. Precedence follows libpng and browsers: ICC, then sRGB, then
  gAMA/cHRM; no description means sRGB. JPEG and WebP reject edits of these PNG-only fields.
- `ResizeOptions.LinearLight` and `DrawImageOptions { Opacity, LinearLight }` resample and blend linear-light sRGB
  with premultiplied alpha in float buffers. A black-and-white checkerboard halves to sRGB 188 instead of 128.

Accuracy was checked before implementation against LittleCMS 2.14 (Pillow `ImageCms`) on synthetic P3, Adobe RGB,
ProPhoto, Rec. 2020, sRGB, linear and Gray profiles. RGB results are within 1 per 8-bit channel, Gray within 2
(LittleCMS's own gray path deviates by 2 near black), and at most 7% of channels differ. Linear-light resizing matches
Pillow's 32-bit float resampling of premultiplied linear channels within 1. Existing Pillow references for the
default modes still match exactly.

Local validation: Release build with zero warnings; **4974/4974 full tests**, **155/155 Oracle** without skips,
**113/113 SIMD fallback tests** and **60/60 color tests without intrinsics**. The package consumer checks contents,
full trimming and linux-x64 Native AOT. The 80-case regression matrix and alternating confirmation runs against
0.6.1 found no repeatable slowdown above 5%. Actual ARM64 execution remains a separate check.

See [color management](../color-management.md), [architecture](../architecture.md#data-flow) and
[measurements](../performance.md#color-management-and-linear-light-062).
