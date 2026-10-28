# Image processing

All operations run inside `Mutate` (changes the image) or `Clone` (returns a new image):

```csharp
using System.Drawing;
using PictureSharp;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;

using var photo = Image.Load("photo.jpg");
using var logo = Image.Load("logo.png");
photo.Mutate(x => x
    .AutoOrient()
    .Resize(new ResizeOptions { Size = new(1200, 800), Mode = ResizeMode.Crop, Position = AnchorPosition.Top })
    .Contrast(1.1f)
    .DrawImage(logo, AnchorPosition.BottomRight, opacity: 0.8f));
photo.SaveAsJpeg("card.jpg", new() { Quality = 88 });
```

- The lambda receives an `ImageProcessingContext` (a `ref struct` that cannot outlive the lambda). Calls chain and run
  in order; `x.Size` is the size reached so far and `x.FrameCount` the frame count. Operations apply to every frame.
- `Clone(x => ...)` copies pixels only when an operation must change them in place; resize, crop and rotate read the
  source directly.
- If an operation throws inside `Mutate`, earlier ones stay applied; `Clone` discards the partial result.
- An HDR gain map follows resizing, cropping, quarter turns and flips; other operations remove it. See
  [HDR photos](hdr.md#what-keeps-or-removes-the-map).

## Resizing

```csharp
using PictureSharp.Processing;

image.Mutate(x => x.Resize(800, 600));                                   // exactly 800×600 (stretches)
image.Mutate(x => x.Resize(800, 0));                                     // width 800, height from the aspect ratio
image.Mutate(x => x.Resize(400, 400, ResizeMode.Max));                   // fit inside 400×400
image.Mutate(x => x.Thumbnail(1920, 1080));                              // shrink to fit, never enlarge
```

### Resize modes

| Mode | Result for a 400×200 image resized to 100×100 | Like |
| --- | --- | --- |
| `Stretch` (default) | 100×100, aspect ratio ignored | `Image.resize` |
| `Max` | 100×50: fits inside the box | CSS `contain`, `ImageOps.contain` |
| `Crop` | 100×100: covers the box, the excess is cropped at `Position` | CSS `cover`, `ImageOps.fit` |
| `Pad` | 100×100: fits inside, then centered (at `Position`) on `PadColor` | `ImageOps.pad` |
| `Min` | 200×100: covers the box without cropping | sharp `fit: outside` |

A 0 for one side derives it from the aspect ratio in every mode.

### `ResizeOptions`

```csharp
using System.Drawing;
using PictureSharp.Processing;

image.Mutate(x => x.Resize(new ResizeOptions
{
    Size = new(1080, 1080),
    Mode = ResizeMode.Pad,
    Position = AnchorPosition.Bottom,     // where Pad places the image / which part Crop keeps
    PadColor = Color.White,               // default: transparent black
    Filter = ResamplingFilter.Lanczos3,
    AllowUpscale = false,                 // never enlarge
}));
```

| Property | Default | Meaning |
| --- | --- | --- |
| `Size` | (required) | Target size; one side may be 0. |
| `Mode` | `Stretch` | See the table above. |
| `Filter` | `Lanczos3` | `Nearest`, `Bilinear`, `Bicubic` (Catmull-Rom), `Lanczos3`, `Box` (area average), `Hamming`. |
| `Position` | `Center` | `AnchorPosition`: center, an edge or a corner. |
| `PadColor` | transparent | Border color for `Pad`. |
| `AllowUpscale` | true | When false the scale is capped at 1: `Crop` only crops, `Pad` only pads, other modes keep smaller images. |

`Thumbnail(maxWidth, maxHeight)` is `Max` with `AllowUpscale = false`. Resampling runs on premultiplied alpha, so
transparent pixels never bleed color. `Nearest` keeps exact colors (pixel art); `Box` and `Lanczos3` downscale best.

## Geometry

```csharp
using System.Drawing;
using PictureSharp.Processing;

image.Mutate(x => x.Crop(new Rectangle(10, 10, 200, 100)));      // or Crop(10, 10, 200, 100)
image.Mutate(x => x.Crop(300, 300));                             // centered 300×300 region
image.Mutate(x => x.Crop(300, 300, AnchorPosition.TopRight));    // keep the top-right corner
image.Mutate(x => x.Pad(1000, 1000, Color.Black));               // bigger canvas, image centered, no scaling
image.Mutate(x => x.Rotate(RotateMode.Rotate90));                // clockwise quarter turns: lossless
image.Mutate(x => x.Flip(FlipMode.Horizontal));                  // mirror
image.Mutate(x => x.AutoOrient());                               // apply EXIF orientation 1–8, reset the tag
```

`Crop` rectangles must lie inside the image. `Pad` to a smaller size cuts at the anchor. Quarter turns swap the
horizontal and vertical resolution.

### Trimming margins

```csharp
image.Mutate(x => x.AutoCrop());                                                    // remove fully transparent margins
image.Mutate(x => x.AutoCrop(new AutoCropOptions { AlphaThreshold = 16, Padding = 4 })); // also drop faint halos, keep 4 px
image.Mutate(x => x.AutoCrop(new AutoCropOptions
{
    Mode = AutoCropMode.Background,   // margins of the top-left pixel's color
    Tolerance = 12,                   // allow JPEG noise
}));

if (image.FindTrimBounds() is { } box)                                              // the box only, image unchanged
{
    Console.WriteLine($"content at {box}");
}
```

- `Alpha` mode (default) treats alpha at or below `AlphaThreshold` as margin; with 0 it matches Pillow's `getbbox()`.
- `Background` mode treats a pixel as margin when every channel, alpha included, is within `Tolerance` of the background
  (largest channel difference, like ImageMagick's `-trim -fuzz`).
- `Padding` keeps extra pixels around the content, within the canvas.
- An image without content is left unchanged and `FindTrimBounds` returns `null`.
- An animation is cropped to the union of all frames' content, keeping a common canvas.

### Rotation by any angle

```csharp
using System.Drawing;
using PictureSharp.Processing;

image.Mutate(x => x.Rotate(15));                                               // clockwise, canvas grows, corners transparent
image.Mutate(x => x.Rotate(-30, ResamplingFilter.Bilinear, Color.White));      // counter-clockwise on white
image.Mutate(x => x.Rotate(45, ResamplingFilter.Nearest, expand: false));      // keep the size, cut the corners
```

`Rotate(degrees, filter = Bicubic, background = transparent, expand = true)` supports `Nearest`, `Bilinear` and
`Bicubic`, and matches Pillow's `Image.rotate(-degrees)`. Multiples of 90° are exact quarter turns.

## Color

```csharp
image.Mutate(x => x.Brightness(1.2f));      // 0 = black, 1 = unchanged, >1 brighter
image.Mutate(x => x.Contrast(0.8f));        // 0 = flat gray (mean luma), 1 = unchanged
image.Mutate(x => x.Saturate(1.5f));        // 0 = grayscale, 1 = unchanged
image.Mutate(x => x.Grayscale());           // BT.601 luma
image.Mutate(x => x.Sepia());               // classic 0.393/0.769/0.189 matrix
image.Mutate(x => x.Invert());
```

All keep alpha. `Brightness`, `Contrast` and `Saturate` are Pillow's `ImageEnhance.Brightness`, `Contrast` and
`Color`; values outside 0–1 extrapolate.

### Levels and gamma

```csharp
image.Mutate(x => x.Levels(new LevelsOptions { InputBlack = 20, InputWhite = 230 }));     // stretch 20..230 to 0..255
image.Mutate(x => x.Levels(new LevelsOptions { Gamma = 1.4f, OutputBlack = 16, OutputWhite = 235 }));
image.Mutate(x => x.Gamma(2.2f));                                                         // >1 brightens, <1 darkens
```

`Levels` maps each selected channel value x to
`OutputBlack + (OutputWhite − OutputBlack) · clamp((x − InputBlack) / (InputWhite − InputBlack), 0, 1)^(1 / Gamma)`,
like ImageMagick's `-level`/`+level`, on encoded sRGB values in double precision, rounding half up. `OutputBlack` above
`OutputWhite` inverts. `Gamma(g)` is `Levels` with only the gamma set. Every pixel is mapped and alpha is kept.

### Curves

```csharp
image.Mutate(x => x.Curves([new(0, 0), new(64, 52), new(192, 206), new(255, 255)]));   // gentle S on R, G and B
image.Mutate(x => x.Curves([new(0, 12), new(255, 255)], ColorChannels.Blue));          // lift the blue shadows only
```

`Curves` maps the selected channels through a smooth curve that passes through the points (input, output; 0–255,
input strictly increasing, at least two). Between points it is the shape-preserving piecewise cubic (PCHIP: the
derivatives of Fritsch and Butland with Moler's end conditions, as SciPy's `PchipInterpolator`), so it never
overshoots: a curve through rising points rises everywhere. Before the first and after the last point the output is
constant. The table is computed in double precision and rounded half up; for separate channel curves, call `Curves`
once per channel.

### Automatic contrast and equalization

```csharp
image.Mutate(x => x.AutoContrast());                       // darkest value → 0, lightest → 255, per channel
image.Mutate(x => x.AutoContrast(1f, 5f, preserveTone: true)); // one mapping from luminance: keeps the hue
image.Mutate(x => x.Equalize());                           // spread each channel's values evenly
```

Both reproduce Pillow's `ImageOps.autocontrast` and `ImageOps.equalize` on RGB bit for bit, counting every pixel and
keeping alpha. An animation is analysed as a whole and every frame gets the same mapping, so nothing flickers.

## Photo adjustments

```csharp
image.Mutate(x => x
    .Exposure(0.5f)                     // stops (EV): +1 doubles the light
    .WhiteBalance(0.3f, -0.1f)          // temperature and tint, −1..1
    .ShadowsHighlights(0.4f, -0.3f)     // lift shadows, recover highlights, −1..1
    .Clarity(0.3f)                      // local contrast, −1..1
    .Vibrance(0.4f)                     // saturation of muted colors, −1..1
    .Vignette(0.3f));                   // darken the edges, −1..1
image.Mutate(x => x.Vignette(new VignetteOptions { Amount = 0.6f, Midpoint = 0.3f, Feather = 0.8f }));
image.Mutate(x => x.Denoise(0.5f));     // edge-preserving smoothing, 0..1, window radius 2
image.Mutate(x => x.WhiteBalance(new Rgba32(212, 196, 170, 255)));   // this picked color becomes gray
image.Mutate(x => x.AutoWhiteBalance(0.7f));                         // gray-world on the midtones, 0..1
```

0 leaves the image unchanged. Alpha is kept, every frame is processed, and the results are identical on every
architecture and thread count. Luma uses Rec. 709 weights; "linear" means sRGB decoded to linear light.

| Operation | Formula |
| --- | --- |
| `Exposure(stops)` | linear · 2^stops, clipped at white |
| `WhiteBalance(t, tint)` | linear R · 2^(0.5t), G · 2^(−0.5 tint), B · 2^(−0.5t), all divided by their luma so gray keeps its brightness |
| `Vibrance(a)` | chroma around the luma scaled by 1 + a · (1 − s) for a > 0, s = (max − min) / max; by 1 + a for a < 0 (−1 is grayscale) |
| `Vignette` | linear · 2^(−2 · Amount · w); w is a smoothstep of the distance from the center (relative to half the diagonal, so 1 is a corner) from `Midpoint` to `Midpoint + Feather · (1 − Midpoint)`; defaults 0.5 and 1 |
| `ShadowsHighlights(s, h, radius)` | linear · 2^(1.5 · (s · (1 − B)² + h · B²)), B a Gaussian blur of the luma (0–1) |
| `Clarity(a, radius)` | each channel + 255 · a · (P − B) · 4P(1 − P), P the luma (0–1), B its Gaussian blur |
| `Denoise(strength, radius)` | guided filter (He et al., 2010) per premultiplied channel, windows (2r + 1)², ε = (0.05 · strength)² |
| `WhiteBalance(neutral)` | linear channel · Y / N, N the neutral's linear channel and Y its luma: the neutral becomes gray of the same luminance |
| `AutoWhiteBalance(s)` | linear channel · (Y / M)^s, scaled so that M keeps its luminance; M is the mean linear color of the measured pixels, Y its luma |

`ShadowsHighlights` and `Clarity` take the blur's standard deviation in pixels; 0 (the default) uses 2% of the shorter
side. The Gaussian is three box passes whose windows are clipped at the image edges. Like any local tone operator,
a large amount next to a strong edge can leave a halo. `Denoise` flattens areas whose variation is small compared with
ε and keeps edges; fully transparent pixels keep their color. `AutoWhiteBalance` measures only the pixels that are
not fully transparent, have no channel at 0–2 or 253–255 (clipped) and whose integer luma
(2126 R + 7152 G + 722 B + 5000) / 10000 lies between its 10th and 90th percentile; an animation is measured as a
whole, so every frame gets the same gains, and an image without such pixels is kept. A scene that is really dominated
by one color (a lawn, a sunset) is pulled towards gray as well, which `strength` below 1 tempers. These operations
have no Pillow counterpart: `tests/assets/photo_adjust_reference.py` implements the formulas above and below with
numpy, and the results match it within one level.

### Dust and color noise

```csharp
image.Mutate(x => x
    .RemoveDust(size: 6)                // specks up to 6 pixels across, threshold 0.1 of the luma range
    .ReduceColorNoise());               // chroma smoothing guided by the luma, window radius 1% of the shorter side
```

Both are made for scans and photographs of film and prints, and keep alpha.

`RemoveDust(size, threshold)` finds specks in the integer luma above:
- A pixel is a speck when it is brighter (or darker) by more than a limit than the median of each of the four
  segments of 2 · size + 1 pixels through it: horizontal, vertical and both diagonals. The median of n values is
  the element (n − 1) / 2 after sorting, and segments and windows are clipped at the image edges.
- The limit is the threshold · 255, or 3 · 1.4826 · the median absolute deviation of the 32 × 32 tile around the
  pixel, whichever is larger, so grain is not taken for dust.
- A speck at most `size` pixels across is a minority on every segment and is found. An edge or a thin line always has
  a segment along itself, so it stays.
- The soft rim joins in at most `size` rounds. In each round, a neighbour (8-connected) of a speck joins when its luma
  differs from the median of its (2 · size + 1)² window in the same direction by more than half the limit.
- Detail that runs between the four directions, such as a stroke of a slanted letter, is found as well, so each
  connected group of speck pixels (one direction, bright or dark) is checked. It is dust only when it spans at most
  `size` + 2 pixels, and when no pixel around it differs from the median of its own window in the same direction by
  more than half the largest difference of the group's core: otherwise it continues into larger detail and stays.
- Every pixel of a speck, its rim and the 1-pixel ring around it gets the per-channel median of its window without
  those pixels, so no outline of the soft rim stays.

`RemoveDust` looks only at the luma, so it has two limits. A faint speck that stands out no more than the grain
stays. Isolated detail no larger than `size`, such as a catchlight, the dot of an i or the hole of a small 0, is taken
for dust: keep `size` near the size of the dust and below the size of fine lettering. A scanner's infrared channel
separates dust reliably.

`ReduceColorNoise(strength, radius)` smooths only the color, on premultiplied values (0–1):
- The guide is the luma Y = 0.2126 R + 0.7152 G + 0.0722 B. The color differences P = B − Y and P = R − Y are each
  filtered with a guided filter: a = cov(Y, P) / (var(Y) + 0.01²) and b = mean(P) − a · mean(Y) over windows of
  (2r + 1)², and Q = mean(a) · Y + mean(b).
- The result is P + strength · (Q − P). Then R = Y + Cr, B = Y + Cb and G = (Y − 0.2126 R − 0.0722 B) / 0.7152, so
  the luma, and with it detail and grain, is kept up to rounding.
- Color blotches in flat areas, such as the block noise of a dense negative's sky, average out. Color edges that are
  also luma edges stay sharp; an edge between two colors of equal brightness softens.
- Fully transparent pixels keep their color.

## Film negatives

A color or black-and-white negative photographed on a light box, or scanned without inversion, becomes a positive in
one call:

```csharp
using var image = Image.Load("strip.jpg");
image.Mutate(x => x.DevelopNegative());                  // finds, straightens and crops the frame
// Check the photograph first, and develop without searching again:
NegativeAnalysis analysis = image.AnalyzeNegative();
if (analysis.Warnings.HasFlag(NegativeWarnings.ChannelClipped)) Console.WriteLine("Use a bluish backlight.");
if (analysis.Warnings.HasFlag(NegativeWarnings.BacklightTexture)) Console.WriteLine("Put a diffuser under the film.");
image.Mutate(x => x.DevelopNegative(new NegativeOptions { Frame = analysis.Frame, Calibration = analysis.Calibration }));
// Only the frame: straighten and cut it out of the negative.
if (image.FindFilmFrame() is { } frame) image.Mutate(x => x.Rotate(frame.Angle).Crop(frame.Bounds));
// One look for a roll: combine the calibrations of several frames.
NegativeCalibration roll = NegativeCalibration.Combine(frames.Select(f => f.AnalyzeNegative().Calibration));
// A frame without perforation: give the regions by hand (this turns the search off).
var picture = new Rectangle(160, 1120, 2780, 1790);      // the picture, without rebate, perforation or light box
var rebate = new Rectangle(830, 850, 140, 160);          // bare film beside the picture
image.Mutate(x => x.DevelopNegative(new NegativeOptions { Region = picture, FilmBaseRegion = rebate }).Crop(picture));
```

`DevelopNegative`:
1. Finds the frame (`FindFilmFrame`, below) unless `NegativeOptions.Frame` is given or `Region` or `FilmBaseRegion` is
   set.
2. Measures it (unless `NegativeOptions.Calibration` is given), with the frame's rebate as the film base.
3. Straightens the strip and crops it to the frame in one bicubic resampling, the same as
   `Rotate(frame.Angle).Crop(frame.Bounds)` (`Crop`, default true).
4. Inverts it with `InvertNegative`, evening out the backlight measured on the rebate (`CorrectIllumination`, default
   true).
5. Removes dust when `DustSize` asks for it, and for color film runs `ReduceColorNoise` (`ColorNoiseReduction`,
   default 1).

Without a frame (a frame that is already cropped, or 120 film without perforation) the whole image is measured and
developed. The positive keeps the strip's direction; turn it upright with `Rotate(RotateMode)`. Only single-frame
images are accepted.

Dust removal is off by default (`DustSize` 0), because `RemoveDust` cannot tell a speck from isolated detail of the
same size. Set `DustSize` close to the size of the dust; `DustSize = null` selects 0.5% of the shorter side. In 0.7.11
that automatic size erased the slanted letters of a licence plate; since 0.7.12 `RemoveDust` keeps detail larger than
a speck, but small letters below the dust size can still go.

### Finding the frame

`FindFilmFrame` looks for 35 mm film with perforation that shows the backlight. It works on a preview whose longer side
is about 1024 pixels, so it takes a few tens of milliseconds at any size, and its result does not depend on the thread
count:
- **Holes**: blobs of backlight, at least 85% as blue as the brightest 0.5% and not redder than blue, that nearly fill
  their bounding box. Holes cut by the photograph's edge count too.
- **Rows**: the vectors to the nearest holes give the strip's direction and the perforation pitch P. Holes 0.5 P
  apart across the strip form a row, which needs three steps of 0.8–1.25 P and holes of 0.1–0.4 P². Two rows
  5.3–6.6 P apart are one strip; one fitted slope through both gives the angle.
- **Frame across the strip**: the strongest step from the bright rebate into the picture, 0.25–1.4 P inside each row of
  holes. Without the second row, the far edge 4.6–5.7 P away counts only when the rebate beyond it is bright.
- **Frame along the strip**: steps into and out of the picture with a gap outside at least 70% as bright as the rebate,
  paired 1.5 frame heights apart (±10%). The frame over the photograph's center wins. An edge that is not found must
  lie past the photograph, and it is reported in `CutEdges`.
- **Lens distortion**: each edge is looked for again in eight pieces, and the innermost clear step bounds the picture.
- **Bounds**: 0.5% inside the edges, in the image rotated by `Angle` (expanded, as `Rotate` does), and inside the
  photograph at cut edges.

Without a row of perforation it returns `null`, and so it does for a frame that is already cropped.

The rebate between the holes and the picture (holes, edge printing and strays removed by 3 MADs of luma and color)
gives the film base and the backlight. log10 of each channel is fitted with a plane: along the strip when the samples
span half a frame, and across it only with rebate on both sides. `CorrectIllumination` divides the film base by that
plane at every pixel; the print of t is then tabulated at 4096 steps and interpolated. The texture of the backlight is
the median standard deviation of the luma in small blocks of the rebate (`NegativeAnalysis.FilmBaseUniformity`): below
2 for a diffused lamp, about 20–25 for a monitor's pixel grid.

The negative is inverted in its own color space: values are decoded with the sRGB curve, and the ICC profile or color
description stays, so a Display P3 photo becomes a Display P3 positive. Converting the negative to sRGB first would
clip its strongly colored film base. Call `ConvertToSrgb` on the positive when sRGB is needed.

The measurement (`AnalyzeNegative`):
- It uses a regular sample of about one million pixels of `Region`, or of the largest upright rectangle inside the
  found frame (every k-th row and column), without fully
  transparent pixels or pixels with a channel at 0–2 or 253–255. Percentiles are the nearest rank of integer
  histograms, so the result does not depend on the thread count.
- **Film base B**: the median color of `FilmBaseRegion`, the rebate of the found frame, or else the 99.7th percentile
  of each channel, which reports `FilmBaseEstimated`.
- **Density** of a value with linear light L: d = log10(B / max(L, 1/1024)), the optical density above the base.
- **Density range R**: the density at the `HighlightPercentile` (default 99.7) of each channel; it prints white.
- **Midtone gamma γ**: each channel's median share of its range, m = d / R, moves towards the geometric mean of the
  three medians. γ is (ln mean / ln m)^`MidtoneBalance`, default 0.6. This balances the dye layers, whose
  contrast differs, and removes the green or cyan cast that a plain inversion leaves. 0 turns it off for a scene that
  is not neutral on average.
- **Warnings**: `ChannelClipped` when more than 1% of the measured pixels are clipped in a channel, `FilmBaseEstimated`
  without a film-base region or rebate, and `LowDensityRange` when a channel spans less than 0.3. With a found
  frame: `BacklightTexture` when `FilmBaseUniformity` is above 6, `FrameCut` when the photograph cuts the frame, and
  `UnevenIllumination` when the backlight is more than 25% brighter at one corner of the frame than at another.

The print (`InvertNegative`, `NegativePrintOptions`) is one lookup table per channel, in double precision:

| Step | Formula |
| --- | --- |
| Share of the range | t = clamp(d / R, 0, 1.05)^γ |
| Paper | linear L' = 10^(`Contrast` · (t − 1)) · 2^`Exposure`; `Contrast` 1.6 decades by default, so the base prints at 2.5% |
| Encoding | s = sRGB(L'), then s + `Curve` · s(1 − s)(2s − 1), `Curve` 0.3 by default, rounded half up |

A black-and-white calibration first replaces each pixel by its integer Rec. 709 luma, so the result is gray.
`tests/assets/photo_adjust_reference.py` implements the measurement and the print with numpy: calibrations match to
1e-5 and pixels exactly.

For the photograph itself:
- Keep the perforation and the rebate on both sides in the picture: they give the frame, the film base and the light.
- Use a diffuse light. On a monitor, keep the film 1–3 cm above the screen or put a diffuser between them, or the
  subpixel grid shows.
- Choose a light bluish enough that the film base looks almost gray; then no channel clips (`ChannelClipped`).

## Histogram

```csharp
ImageHistogram histogram = image.GetHistogram();
byte median = histogram.Percentile(HistogramChannel.Luminance, 50);
double meanRed = histogram.Mean(HistogramChannel.Red);

ImageHistogram visible = image.GetHistogram(new HistogramOptions
{
    Transparency = HistogramTransparency.Exclude, // skip pixels with alpha 0
    Region = new Rectangle(0, 0, 100, 100),       // must lie inside the image
    AllFrames = true,                             // count every frame of an animation
});
```

`GetHistogram` counts red, green, blue, alpha and BT.601 luminance values 0–255 over the first frame by default, like
Pillow's `Image.histogram()`, and returns a snapshot. `Percentile` uses the nearest rank (0 minimum, 50 median, 100
maximum). With nothing counted, `Mean` returns `NaN` and `Percentile` throws.

### Transparency and fills

```csharp
image.Mutate(x => x.BackgroundColor(Color.White));                    // composite over white: removes transparency
image.Mutate(x => x.Opacity(0.5f));                                   // scale alpha
image.Mutate(x => x.MakeTransparent(Color.Magenta));                  // exact color match → alpha 0
image.Mutate(x => x.Fill(Color.Black));                               // overwrite every pixel
```

`Fill` overwrites without blending and clips to the image. Before saving transparency as JPEG, use `BackgroundColor`
(or `JpegEncoderOptions.BackgroundColor`) if white is not the background you want.

## Blur and sharpen

```csharp
image.Mutate(x => x.GaussianBlur(2.5f));                    // sigma (Pillow's GaussianBlur radius)
image.Mutate(x => x.BoxBlur(3));                            // box radius; fractional values allowed
image.Mutate(x => x.UnsharpMask());                         // radius 2, 150 %, threshold 3
image.Mutate(x => x.Sharpen());                             // 3×3 sharpen kernel
```

Filters run on premultiplied alpha, so transparent edges get no dark fringe. `GaussianBlur` uses three box passes
(linear time for any sigma).

## Drawing images

```csharp
using System.Drawing;
using PictureSharp.Processing;

using var watermark = Image.Load("logo.png");
image.Mutate(x => x.DrawImage(watermark, AnchorPosition.BottomRight, opacity: 0.6f));
image.Mutate(x => x.DrawImage(watermark, new Point(20, 20)));
```

`DrawImage` composites the source's first frame over every frame (source-over, like `Image.alpha_composite`), clipped to
the image. The location may be negative and the source may be the image itself.

### Blend modes

```csharp
using var texture = Image.Load("paper.png");
image.Mutate(x => x.DrawImage(texture, Point.Empty, new DrawImageOptions { BlendMode = BlendMode.Multiply }));
image.Mutate(x => x.DrawImage(texture, AnchorPosition.Center, new DrawImageOptions
{
    BlendMode = BlendMode.SoftLight,
    Opacity = 0.7f,
    LinearLight = true,               // blend in linear-light sRGB
}));
```

`BlendMode` offers `Normal`, `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`, `Difference`, `HardLight`,
`SoftLight`, `ColorDodge`, `ColorBurn` and `Exclusion`: the separable modes of
[W3C Compositing and Blending Level 1](https://www.w3.org/TR/compositing-1/), as CSS `mix-blend-mode`. Over a
transparent backdrop the source shows unchanged; the mixed color is composited source-over with alpha scaled by
`Opacity`. `Normal` keeps the Pillow-compatible 8-bit blend; other modes compute in float on straight alpha. With
`LinearLight` they decode sRGB first, which changes the look; image editors usually blend encoded values (the default).

## Alpha masks

```csharp
using var shape = Image.Load("rounded-corners.png");
image.Mutate(x => x.ApplyMask(shape));                                         // mask alpha, top-left corners aligned

using var vignette = Image.Load("vignette.png");
image.Mutate(x => x.ApplyMask(vignette, AnchorPosition.Center, new MaskOptions
{
    Channel = MaskChannel.Luminance,   // white keeps, black removes
    Outside = MaskOutside.Transparent, // remove what the mask does not cover
}));
```

`ApplyMask` multiplies each frame's alpha by the mask value, `A' = round(A · v / 255)`, where v is the mask's alpha or
the BT.601 luma of its color (`Invert` uses `255 − v`). Colors are never changed. The mask's first frame applies to
every frame, may be placed anywhere and is clipped; uncovered pixels are kept, or cleared with
`MaskOutside.Transparent`.

## Custom operations

`Apply` gives direct access to each frame's pixels:

```csharp
using PictureSharp;

image.Mutate(x => x.Apply(frame =>
{
    foreach (ref Rgba32 p in frame.GetPixelSpan<Rgba32>())
    {
        p.B = 0;                                      // drop the blue channel
    }
}));
```

With C# 14 extension members, custom operations chain like built-in ones:

```csharp
using System.Drawing;
using PictureSharp;
using PictureSharp.Processing;

public static class MyOperations
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext Polaroid() =>
            x.Pad(x.Size.Width + 40, x.Size.Height + 120, Color.White, AnchorPosition.Top);
    }
}

// usage
image.Mutate(x => x.Resize(600, 0).Polaroid());
```

An operation producing new buffers, possibly of another size, implements `IFrameProcessor` and runs through
`ApplyProcessor`: all frames are prepared first and published together, so an exception or cancellation leaves the image
unchanged. See [Extending PictureSharp](extending-picturesharp.md#custom-frame-processors).

## Compatibility with Pillow

Operations with a Pillow counterpart reproduce Pillow 10.2.0 pixel for pixel: `Image.resize`,
`ImageOps.contain`/`fit`/`pad`, `Image.alpha_composite`, `ImageEnhance`, `ImageFilter.BoxBlur`/`GaussianBlur`/
`UnsharpMask`/`SHARPEN`, `Image.rotate` and `ImageOps.exif_transpose`. Blur, sharpening and interpolated rotation match
Pillow on an `RGBa` (premultiplied) image, so semi-transparent colors may shift slightly while opaque ones are exact.
Operations without a Pillow counterpart follow the formulas in their
sections.
