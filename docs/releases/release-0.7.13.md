# PictureSharp 0.7.13

Film negatives without manual regions: `DevelopNegative()` finds the frame in a photographed 35 mm strip, straightens
and crops it, takes the film base from the rebate and evens out the backlight. No breaking API changes since 0.7.12;
existing operations produce exactly the same pixels, except `DevelopNegative()` on a photograph with perforation (see
below).

- **`FindFilmFrame()`** finds the frame nearest the photograph's center from its perforation, which must show the
  backlight. It returns a `FilmFrame`: `Angle` straightens the strip, `Bounds` is the picture in the image rotated by
  that angle, `PerforationsVertical` tells the strip's direction and `CutEdges` lists the edges the photograph cuts
  off. It works on a preview of about 1024 pixels: 37 ms for 11 MP on 8 threads. Without perforation it returns
  `null`.

  ```csharp
  if (image.FindFilmFrame() is { } frame) image.Mutate(x => x.Rotate(frame.Angle).Crop(frame.Bounds));
  ```

- **`DevelopNegative()` is fully automatic:** it finds the frame, measures it with the rebate beside the perforation
  as the film base, straightens and crops it in one bicubic pass (exactly `Rotate(Angle).Crop(Bounds)`), and divides
  the film base by the backlight's gradient measured on the rebate. 140 ms for an 11 MP strip on 8 threads.

  ```csharp
  image.Mutate(x => x.DevelopNegative());
  NegativeAnalysis analysis = image.AnalyzeNegative();   // the same measurement, with analysis.Frame
  image.Mutate(x => x.DevelopNegative(new NegativeOptions { Frame = analysis.Frame, Calibration = analysis.Calibration }));
  ```

  New options: `Frame` (a frame found before), `Crop` (default true) and `CorrectIllumination` (default true).
  Setting `Region` or `FilmBaseRegion` turns the search off and develops the whole image, as in 0.7.12. A photograph
  without perforation (a frame that is already cropped, 120 film) is developed whole, as before.

  **Behavior change:** on a photograph with perforation, `DevelopNegative()` now returns the cropped frame, where
  0.7.12 returned the whole photograph with the rebate and light box inverted. Set `Crop = false` to keep the size.
- **New warnings and measurements:** `NegativeAnalysis.Frame` and `FilmBaseUniformity` (the texture of the backlight
  through the rebate). `BacklightTexture` reports a monitor's pixel grid showing through the film, `FrameCut` a frame
  cut off by the photograph, and `UnevenIllumination` a backlight more than 25% brighter at one corner of the frame
  than at another. `FilmBaseEstimated` now appears only when neither a region nor a rebate gave the base.
- **Faster bicubic rotation:** the bicubic filter of `Rotate(degrees)` evaluates the four channels in one
  `Vector256<double>` with the same operations, and its rows run in parallel: identical pixels, 2× faster on one
  thread.

See [Film negatives](https://github.com/art-ix/PictureSharp/blob/v0.7.13/docs/image-processing.md#film-negatives) and
[Film frames (0.7.13)](https://github.com/art-ix/PictureSharp/blob/v0.7.13/docs/performance.md#film-frames-0713).

The adapter packages are released at the same version without changes.

**Validation:**
- All tests passed on linux-x64, including the oracle and slow tests, trimming and Native AOT (the smoke test finds
  and develops a strip).
- Synthetic strips: straight, turned by −1.3° to 6°, vertical, cut off on each side and lit unevenly; the frame is
  found inside its picture and covering at least 95% of it, with no rebate in the crop, the angle within 0.1°, and the straightened crop equals
  `Rotate(Angle).Crop(Bounds)` pixel for pixel. Results are identical on 1 and 8 threads.
- Local tests on 15 private iPhone photographs of a Kodak Gold strip on a light box and a monitor: all 14 frames with
  perforation are found as checked by eye, including close-ups whose frame the photograph cuts; the monitor
  photographs report `BacklightTexture`, and a cropped frame and scans without perforation give no frame.
- The vector bicubic filter equals the scalar one on random images and angles.
- Not run on ARM64. The vector filter needs `Vector256` and otherwise falls back to the scalar code.
