# PictureSharp 0.7.12

A fix to the dust removal of 0.7.11, which was tagged but not published to NuGet. This release contains everything
described in [0.7.11](release-0.7.11.md): `AnalyzeNegative`, `DevelopNegative`, `InvertNegative`,
`NegativeCalibration`, `Curves`, `WhiteBalance(Rgba32)`, `AutoWhiteBalance`, `RemoveDust` and `ReduceColorNoise`. No
breaking API changes since 0.7.10; existing operations produce exactly the same pixels.

- **`RemoveDust` keeps detail larger than a speck:** in 0.7.11 it erased the slanted letters of a licence plate. A
  speck is a pixel that stands out from the median of four segments through it (horizontal, vertical and both
  diagonals), and a stroke slanted between those directions stands out from all four. Now each connected group of
  such pixels is removed only when it spans at most `size` + 2 pixels and nothing around it continues it: no
  neighbouring pixel differs from its surroundings in the same direction by more than half as much as the speck. The
  1-pixel ring around a removed speck is replaced too, so no outline of its soft rim stays.

  | Photograph | `size` | Before | 0.7.12 |
  | --- | ---: | --- | --- |
  | Licence plate on a phone photo of a negative | 11 | 29% of the letters' bright pixels stay | all stay |
  | Licence plate on a 22 MP scan | 19 | stroke ends cut | unchanged |
  | Dust on a black-and-white negative | 10 | removed, with a visible ring | removed, no ring |

  Isolated detail no larger than `size`, such as a catchlight, a small letter or the hole of a small 0, is still
  taken for dust: keep `size` near the size of the dust.
- **Dust removal is off by default in `DevelopNegative`:** `NegativeOptions.DustSize` defaults to 0 instead of
  `null`. `DustSize = null` still selects the automatic size (0.5% of the shorter side).

  ```csharp
  image.Mutate(x => x.DevelopNegative());                                     // no dust removal
  image.Mutate(x => x.DevelopNegative(new NegativeOptions { DustSize = 4 })); // specks up to 4 pixels
  ```

  See [Dust and color noise](../image-processing.md#dust-and-color-noise) and
  [Film negatives](../image-processing.md#film-negatives).

`RemoveDust(6)` on 12 MP takes 0.44 s on 8 threads (0.41 s in 0.7.11); `DevelopNegative` without dust removal takes
about 0.22 s on an 8 MP photographed negative.

The adapter packages are released at the same version without changes.

**Validation:**
- All tests passed on linux-x64, including the oracle and slow tests, trimming and Native AOT.
- `RemoveDust` matches the numpy reference bit for bit, including a new image with lettering and a slanted bar.
- Synthetic lettering with rings, bars and a bar slanted by 20° keeps every stroke pixel at sizes 4, 8 and 11 while
  its specks go; 0.7.11 kept 1361 and 325 of 1827.
- Local tests on private photographed negatives and scans: the plate test above, and dust removed on a
  black-and-white negative.
- A timing-sensitive cancellation test, which stopped the NuGet publication of 0.7.11, now measures a warmed-up run.
- Not run on ARM64. The change adds no platform-specific code.
