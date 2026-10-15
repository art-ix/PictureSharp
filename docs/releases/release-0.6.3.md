# PictureSharp 0.6.3

Graphics operations: tonal adjustments, histograms, trimming, alpha masks and blend modes. Default results are unchanged.

- **Levels and gamma:** `Levels(LevelsOptions)` maps input black/white, a midtone gamma and output black/white, as an
  image editor's Levels dialog and ImageMagick's `-level`/`+level`. `Gamma(g, ColorChannels)` applies a power curve
  (above 1 brightens). Both work on all color channels or selected ones (`ColorChannels`), keep alpha, and compute an
  exact double-precision lookup table.
- **AutoContrast and Equalize:** `AutoContrast(cutoff, preserveTone)` (or separate low/high cutoffs) and `Equalize()`
  reproduce Pillow's `ImageOps.autocontrast` and `ImageOps.equalize` bit for bit on the RGB channels. An animation is
  analysed as a whole and gets one mapping, so frames do not flicker.
- **Histograms:** `image.GetHistogram(HistogramOptions)` returns an `ImageHistogram` snapshot with red, green, blue,
  alpha and BT.601 luminance counts, `Mean` and nearest-rank `Percentile`. By default it counts every pixel of the
  first frame, as Pillow's `Image.histogram()`. Options exclude fully transparent pixels, restrict it to a region or
  count every frame.
- **Trimming:** `AutoCrop(AutoCropOptions)` removes transparent margins (`AlphaThreshold`) or margins of a background
  color. That color is the top-left pixel by default or `Background`, matched within `Tolerance` on every RGBA channel,
  like `-trim -fuzz`; fully transparent pixels match a transparent background. `Padding` keeps a margin.
  `image.FindTrimBounds()` returns the box without changing the image. Animations are cropped to the union of all
  frames; an image without content is left unchanged.
- **Alpha masks:** `ApplyMask(mask, location or anchor, MaskOptions)` multiplies alpha by the mask's alpha or
  luminance, optionally inverted, and leaves pixels outside the mask unchanged or makes them transparent. Color
  channels are never changed.
- **Blend modes:** `DrawImageOptions.BlendMode` adds `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`,
  `Difference`, `HardLight`, `SoftLight`, `ColorDodge`, `ColorBurn` and `Exclusion`. These are the separable modes of
  W3C Compositing and Blending Level 1 (CSS `mix-blend-mode`), composited source-over with `Opacity` and optionally
  in linear light. `Normal` keeps the existing Pillow-compatible blend byte for byte.
- **CLI:** `picturesharp-cli trim` with `-mode`, `-color`, `-threshold`, `-tolerance` and `-padding`.

References were fixed before implementation in `tests/assets/graphics_reference.py`. Levels, masks and trim bounds
use exact integer or double-precision models with tolerance 0. `AutoContrast` and `Equalize` match Pillow 10.2.0
exactly. Blend modes are within 1 per 8-bit channel of a double-precision W3C model, which agrees with Pillow
`ImageChops` within 1. Trim bounds match Pillow `getbbox`, and levels match ImageMagick 6 within 1.

Local validation: Release build with zero warnings; **5261/5261 full tests**, **155/155 Oracle** without skips,
**113/113 SIMD fallback tests** and **110/110 graphics tests without intrinsics**. The package consumer checks
contents, full trimming and linux-x64 Native AOT, including every new operation. A 12 MP probe measures `Levels`
at 10.5 ms, `GetHistogram` at 26 ms and `Multiply` blending at 75 ms. The 80-case regression matrix against 0.6.2
(ABBA order, alternating confirmations in separate load contexts) found no repeatable slowdown above 5%. No new SIMD
paths were added. Actual ARM64 execution remains a separate check.

See [image processing](../image-processing.md), [command-line sample](../cli.md) and
[measurements](../performance.md#graphics-operations-063).
