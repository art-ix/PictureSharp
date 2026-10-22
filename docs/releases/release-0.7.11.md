# PictureSharp 0.7.11

> **Not published to NuGet.** Its dust removal erased slanted lettering; [0.7.12](release-0.7.12.md) keeps detail
> larger than a speck, turns dust removal in `DevelopNegative` off by default and contains everything below.

Develops photographed film negatives, and adds the tools that this needs to `Mutate` and `Clone`. No breaking API
changes since 0.7.10; existing operations produce exactly the same pixels.

- **Film negatives:** `DevelopNegative(options)` turns a color or black-and-white negative, photographed on a light
  box or scanned without inversion, into a positive. It measures the negative, inverts it in optical density, then
  removes dust and color noise. `image.AnalyzeNegative(options)` does only the measurement. It returns a
  `NegativeCalibration` and `NegativeWarnings`: a clipped channel, an estimated film base or a very thin negative.
  `InvertNegative(calibration, print)` inverts with a known calibration, and `NegativeCalibration.Combine` gives a
  whole roll one look. `NegativeOptions` holds the settings:
  - `Region`: the frame that is measured.
  - `FilmBaseRegion`: bare film, used as the black point.
  - `FilmType`, `HighlightPercentile`, `MidtoneBalance`.
  - `DustSize` and `ColorNoiseReduction`: the cleanup.
  - `Print` (`NegativePrintOptions`): `Contrast`, `Curve`, `Exposure`.

  ```csharp
  NegativeAnalysis analysis = image.AnalyzeNegative(new NegativeOptions { Region = frame, FilmBaseRegion = rebate });
  image.Mutate(x => x.DevelopNegative(new NegativeOptions { Calibration = analysis.Calibration }).Crop(frame));
  ```

  The negative is inverted in its own color space and keeps its ICC profile. Converting a Display P3 negative to sRGB
  first would clip its orange film base. This release does not find the frame or straighten it yet: crop first, or
  pass `Region`. See [Film negatives](../image-processing.md#film-negatives).
- **Curves:** `Curves(points, channels)` maps channels through a smooth curve through control points. The curve is
  shape-preserving piecewise cubic (PCHIP, as SciPy's `PchipInterpolator`), so it never overshoots between points.
  See [Curves](../image-processing.md#curves).
- **White balance from a neutral and automatic:** `WhiteBalance(Rgba32 neutral)` makes a picked color gray.
  `AutoWhiteBalance(strength)` assumes the midtones average to gray, leaving out clipped and transparent pixels, and
  measures an animation as a whole.
- **Dust and color noise:**
  - `RemoveDust(size, threshold)` replaces specks that stand out from the median of all four directions through
    them. Edges and thin lines are kept, and the limit rises with the local grain.
  - `ReduceColorNoise(strength, radius)` smooths only the chroma, with a guided filter that follows the luma. Color
    blotches in a dense sky disappear, while detail and grain stay.

  See [Dust and color noise](../image-processing.md#dust-and-color-noise).

```csharp
image.Mutate(x => x
    .Curves([new(0, 0), new(64, 52), new(192, 206), new(255, 255)])
    .AutoWhiteBalance(0.7f)
    .RemoveDust(size: 6)
    .ReduceColorNoise());
```

Alpha is kept, and the results are identical on every architecture and thread count. A numpy implementation of the
documented formulas (`tests/assets/photo_adjust_reference.py`) serves as the oracle:
- 147 of 148 adjustment cases match it bit for bit, and the last one is within one level.
- Every negative calibration matches within 1e-5, and every inverted and developed image matches bit for bit.

**Performance (12 MP, 8 threads):**

| Operation | Time |
| --- | ---: |
| `Curves`, `WhiteBalance(neutral)` | about 11 ms |
| `AutoWhiteBalance` | 60 ms |
| `RemoveDust(6)` | 0.41 s |
| `ReduceColorNoise` | 0.27 s |

`DevelopNegative` on a 5 MP photographed negative takes about 0.3 s, including a measurement of 11–22 ms. See
[Performance](../performance.md#film-negatives-and-cleanup-0711).

The adapter packages are released at the same version without changes.

**Validation:**
- All tests passed on linux-x64, including the oracle and slow tests, trimming and Native AOT.
- Hashes of the new operations are the same with one thread and eight.
- Synthetic negatives developed with a print matching their film come back above 30 dB PSNR with a neutral gray
  wedge.
- Not run on ARM64. The new code is scalar, with no platform-specific paths.
