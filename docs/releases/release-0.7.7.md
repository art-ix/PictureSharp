# PictureSharp 0.7.7

Photographic adjustments for `Mutate` and `Clone`. No breaking API changes since 0.7.6; existing operations produce
exactly the same pixels.

- **Exposure and white balance:** `Exposure(stops)` multiplies linear light by 2^stops. `WhiteBalance(temperature,
  tint)` scales the channels in linear light (−1 to 1 each) and keeps the brightness of gray. Both are channel lookup
  tables (about 12 ms for 12 MP).
- **Vibrance and vignette:** `Vibrance(amount)` raises the saturation of muted colors more than of saturated ones;
  −1 gives grayscale. `Vignette(amount)` darkens or brightens the edges in linear light, and `VignetteOptions` sets
  where the vignette starts (`Midpoint`) and how gradually it reaches the corners (`Feather`).
- **Local tone:** `ShadowsHighlights(shadows, highlights, radius)` lifts or deepens areas by the brightness of their
  surroundings, and `Clarity(amount, radius)` changes local contrast in the midtones. Both compare each pixel with a
  Gaussian blur of the luma (2% of the shorter side by default).
- **Denoise:** `Denoise(strength, radius)` is an edge-preserving guided filter (He et al.) on premultiplied channels:
  it flattens small variations and keeps edges, and fully transparent pixels keep their color.

```csharp
image.Mutate(x => x
    .Exposure(0.5f)
    .WhiteBalance(0.3f, -0.1f)
    .ShadowsHighlights(0.4f, -0.3f)
    .Clarity(0.3f)
    .Vibrance(0.4f)
    .Vignette(0.3f)
    .Denoise(0.5f));
```

0 leaves the image unchanged. Alpha is kept, every frame of an animation is processed, and cancellation is checked
every row or 64 Ki pixels. The formulas are listed in [Image processing](../image-processing.md#photo-adjustments). These operations
have no Pillow counterpart: a numpy implementation of the documented formulas (`tests/assets/photo_adjust_reference.py`)
serves as the oracle, and the results match it within one level. Powers use PictureSharp's deterministic `Exp2`, so
the pixels are identical on every architecture and thread count.

**Performance (12 MP, 8 threads):** vibrance and vignette about 35 ms, shadows/highlights 99 ms, clarity 83 ms,
denoise 298 ms; on one thread 3.5 to 5 times slower. See [Performance](../performance.md#photo-adjustments-077).

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle and slow tests, trimming and Native AOT), and the
photo-adjustment hashes are the same with one thread and eight. Not run on ARM64; the new code is scalar, with no
platform-specific paths.
