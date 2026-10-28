# PictureSharp 0.9.0

Edit and save 16-bit images without reducing them to 8 bits along the way. This works across all image
operations, from resizing and color correction to film negatives and HDR gain maps.

**Requires .NET 10. This release changes the pixel API and plugin API.** Applications using 0.8.5 need a few code
changes and updated plugins. Follow the [migration guide](../migration-from-0.8.md).

## Keep 16-bit precision

Loading still returns an 8-bit image by default. Set `PreserveBitDepth = true` to keep deeper source samples:

```csharp
using PictureSharp;
using PictureSharp.Formats.Png;

using Image scan = Image.Load("scan.png", new DecoderOptions { PreserveBitDepth = true });
scan.Mutate(x => x.AutoContrast().Resize(1600, 0));
scan.SaveAsPng("edited.png", new PngEncoderOptions { BitDepth = PngBitDepth.Sixteen });
```

PNG supports 16-bit grayscale, RGB and alpha, including interlaced and animated files. Its output depth can be
chosen automatically or set to 8 or 16 bits. PNG `sBIT` metadata describes the source precision without changing
pixel values.

## Other changes

- `Rgba64` stores 16-bit channels. `Image.PixelType` tells you whether an image uses `Rgba32` or `Rgba64`.
  Direct pixel access now includes the pixel type, for example `GetRowSpan<Rgba64>(y)`.
- Levels, curves and sample thresholds use values from 0 to 1. Related members have `Normalized` in their names.
  Histograms can count all 65536 sample values in a 16-bit image.
- Color conversion processes deeper inputs at 16-bit precision before reducing them, if requested.
  HDR tone mapping can also return a 16-bit image.
- The Skia adapter supports integer 16-bit RGBA. System.Drawing output remains 8-bit per channel.
- The command-line converter adds `--depth auto|8|16`; its default is still `8`.
- A 16-bit pixel counts twice against `MaxPixels`. GIF also checks oversized frame buffers before allocating them.

Built-in formats other than PNG save 8-bit output. For deeper output, use **TIFF 0.7.4**, **HEIF 0.7.0** (10-bit HEVC)
or **JPEG XL 0.7.8**. These plugin versions require core 0.9.0; earlier builds are rejected when registered.
Formats.Extra is outside this update.

## Verification

The x64 test suite passes with and without hardware acceleration: 9398 passed, 8 Windows-only tests skipped.
All 261 reference-tool tests pass. Package consumers pass full trimming and Native AOT checks for the core and adapters.

On ARM64, 9295 tests pass and the same 8 Windows-only tests are skipped, both with and without hardware acceleration.
All 138 SIMD comparisons pass. Large private 48 MP HDR tests were checked on x64 because they exceed the ARM device's
4 GiB test heap. Detailed results and comparison tolerances are in the [verification record](../measurements/release-0.9.0-gate.json).
