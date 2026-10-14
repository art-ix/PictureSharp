# Encoder options

Every format has an options record: `WebPEncoderOptions`, `PngEncoderOptions`, `JpegEncoderOptions`, `BmpEncoderOptions` and `GifEncoderOptions`, all deriving from `EncoderOptions`. Pass them to `Save`, `SaveAsync`, `SaveAs*`, `Encode` or `To*`:

```csharp
image.SaveAsWebP("photo.webp", new() { Quality = 85, Method = 6 });
image.Save("photo.jpg", new JpegEncoderOptions { Quality = 90 });
```

- **Checked when set.** An out-of-range value throws `ArgumentOutOfRangeException` on the line that sets it, not later during encoding.
- **Records.** Options compare by value and `with` creates variants: `var hq = options with { Quality = 95 };`.
- **Metadata.** Every options type has `Metadata` (`MetadataKinds.All` by default) to choose which of ICC, EXIF and XMP are written; see [Metadata](metadata.md).

## WebP

```csharp
image.SaveAsWebP("lossy.webp");                                              // quality 75, method 4
image.SaveAsWebP("small.webp", new() { Quality = 70, Method = 6 });
image.SaveAsWebP("exact.webp", new() { Lossless = true, Exact = true });
image.SaveAsWebP("near.webp", new() { Lossless = true, NearLossless = 60 });
image.SaveAsWebP("target.webp", new() { TargetSize = 50_000, Passes = 6 });
```

| Property | Default | Meaning |
| --- | --- | --- |
| `Lossless` | false | Use the lossless VP8L bitstream instead of lossy VP8. |
| `Quality` | 75 | 0–100. Lossy: visual quality. Lossless: compression effort (higher is smaller and slower). |
| `Method` | 4 | 0 (fast) – 6 (slowest, smallest). |
| `Preset` | `Default` | Content tuning as in `cwebp -preset`: `Picture`, `Photo`, `Drawing`, `Icon`, `Text`. |
| `Exact` | false | Keep RGB values under fully transparent pixels (otherwise they may be changed to compress better). |
| `NearLossless` | 100 | Lossless only: 0–100, lower values quantize pixels for smaller files; 100 is off. |
| `AlphaQuality` | 100 | Lossy only: 0–100 quality of the alpha plane; 100 keeps alpha exact. |
| `AlphaCompression` | true | Lossy only: compress the alpha plane losslessly (otherwise stored raw). |
| `AlphaFilter` | `Fast` | Lossy only: `None`, `Fast` or `Best` predictive filter for alpha. |
| `SnsStrength` | preset (50) | 0–100 spatial noise shaping. |
| `FilterStrength` | preset (60) | 0–100 deblocking filter strength; 0 is off. |
| `FilterSharpness` | preset (0) | 0 (sharpest) – 7. |
| `AutoFilter` | false | Choose the filter strength automatically. |
| `Segments` | preset (4) | 1–4 segments. |
| `Partitions` | 0 | log2 of token partitions (0–3); as in libwebp, methods 3–6 always use one. |
| `TargetSize` | 0 | Target file size in bytes; works with `Passes`. Takes precedence over `Quality`. |
| `TargetPsnr` | 0 | Target PSNR in dB; works with `Passes`. |
| `Passes` | 1 | 1–10 entropy-analysis passes. |
| `UseSharpYuv` | false | Slower, more accurate RGB→YUV conversion (sharper color edges). |
| `MaxDegreeOfParallelism` | -1 | Threads to use; -1 is automatic, 1 is single-threaded. Output does not depend on it. |
| `AllowMixed`, `MinimizeSize`, `KeyframeMin`, `KeyframeMax` | false, false, 9, 17 | Animation only; see [Animation](animation.md#saving-animated-webp). |

Preset values (`SnsStrength`/`FilterStrength`/`FilterSharpness`): `Picture` 80/35/4, `Photo` 80/30/3, `Drawing` 25/10/6, `Icon` 0/0/0, `Text` 0/0/0 with 2 segments. Explicit values override the preset.

Across the test corpus, lossy output of opaque images and lossless output (every method, quality, `NearLossless` and `Exact`) are byte-identical to `cwebp` 1.6.0 with the same settings. Encoding uses several threads by default; the bytes are the same for any `MaxDegreeOfParallelism`. Reaching `TargetSize`/`TargetPsnr` exactly depends on the image.

## PNG

```csharp
image.SaveAsPng("out.png");                                                  // smallest lossless color type
image.SaveAsPng("fast.png", new() { CompressionLevel = 1 });
image.SaveAsPng("indexed.png", new() { ColorType = PngColorType.Palette });
image.SaveAsPng("interlaced.png", new() { Interlace = true });
```

| Property | Default | Meaning |
| --- | --- | --- |
| `CompressionLevel` | 6 | Deflate level 0 (store) – 9 (best). |
| `ColorType` | `Auto` | `Auto` picks palette, grayscale, gray+alpha, RGB or RGBA, whichever represents the pixels losslessly and smallest. `Grayscale`/`GrayscaleAlpha` convert to luma, `Rgb` drops alpha, `Palette` fails with `InvalidOperationException` above 256 colors. |
| `FilterStrategy` | `Adaptive` | Per-row filter choice; or a fixed `None`, `Sub`, `Up`, `Average`, `Paeth`. |
| `Interlace` | false | Adam7 interlacing. |
| `SaveAnimation` | true | Write animated images as APNG (otherwise only the first frame). |

## JPEG

```csharp
image.SaveAsJpeg("photo.jpg", new() { Quality = 88 });
image.SaveAsJpeg("web.jpg", new() { Quality = 80, Progressive = true });
image.SaveAsJpeg("sharp-color.jpg", new() { Subsampling = JpegSubsampling.Ratio444 });
image.SaveAsJpeg("on-black.jpg", new() { BackgroundColor = Color.Black });   // transparent areas become black
```

| Property | Default | Meaning |
| --- | --- | --- |
| `Quality` | 75 | 1–100 (libjpeg quality scaling). |
| `Subsampling` | `Ratio420` | Chroma sampling: `Ratio444`, `Ratio422`, `Ratio420`, `Ratio440`. |
| `Progressive` | false | Progressive scans (implies optimized Huffman tables). |
| `OptimizeCoding` | false | Optimal Huffman tables computed in an extra pass. |
| `RestartInterval` | 0 | MCUs between restart markers (0–65535); 0 disables them. |
| `BackgroundColor` | white | Opaque color under transparent pixels; JPEG has no alpha. |

JPEG writes the first frame only. Grayscale images are detected and written with a single component. The encoder reference vectors are byte-identical to Pillow 10.2.0 / libjpeg-turbo 2.1.5.

## BMP

```csharp
image.SaveAsBmp("out.bmp");                                                  // automatic bit depth
image.SaveAsBmp("rgb.bmp", new() { BitsPerPixel = 24 });
```

`BitsPerPixel` is 0 (automatic), 1, 4, 8, 24 or 32. Automatic chooses a palette when the image is opaque with at most 256 colors, 24 bits when it is opaque, and 32 bits (with alpha) otherwise. Palette and 24-bit output drop alpha. BMP is written uncompressed and stores the first frame; an ICC profile is embedded in a `BITMAPV5HEADER`.

## GIF

```csharp
image.SaveAsGif("out.gif");                                                  // exact colors when they fit, else median cut
image.SaveAsGif("small.gif", new() { ColorCount = 64, Dither = GifDither.FloydSteinberg });
image.SaveAsGif("fast.gif", new() { Quantizer = GifQuantizer.Octree });
image.SaveAsGif("soft-edges.gif", new() { TransparencyThreshold = 32 });     // keep faint pixels opaque
```

| Property | Default | Meaning |
| --- | --- | --- |
| `ColorCount` | 256 | 2–256 palette entries per frame, including the transparent entry when one is needed. |
| `Quantizer` | `Auto` | Used when the colors do not fit: `MedianCut` (the `Auto` choice) or `Octree`, ported from Pillow's `Image.quantize`. |
| `Dither` | `None` | `FloydSteinberg` error diffusion against the quantized palette (smoother gradients, larger files). |
| `TransparencyThreshold` | 128 | Pixels with lower alpha become transparent, all others opaque: GIF has no partial transparency. |
| `Interlace` | false | Store rows in four interlaced passes. |
| `SaveAnimation` | true | Write all frames of animated images (otherwise only the first frame). |
| `OptimizeFrames` | true | Animation: write only changed rectangles, mark unchanged pixels transparent and merge repeated frames. |

Images whose colors fit keep them exactly: one global palette when all frames together fit, otherwise exact local palettes per animation frame. Otherwise a still image is quantized exactly as Pillow's `Image.quantize(colors)` (same palette and indices), and an animation gets one quantized palette shared by all frames. Durations are stored in hundredths of a second (rounded; at most 655.35 s). ICC and XMP are written as application extensions; GIF has no EXIF.

## Recipes

```csharp
// Pixel-exact WebP: decodes to exactly the same RGBA values, including RGB under transparent pixels.
var exact = new WebPEncoderOptions { Lossless = true, Exact = true };

// Web photo: small and good-looking.
var web = new WebPEncoderOptions { Quality = 78, Method = 6, Preset = WebPPreset.Photo, UseSharpYuv = true };

// Screenshots and UI: lossless, with near-lossless for a smaller file.
var screenshot = new WebPEncoderOptions { Lossless = true, NearLossless = 80 };

// Strip all metadata (privacy: removes EXIF GPS data).
var anonymous = new JpegEncoderOptions { Quality = 85, Metadata = MetadataKinds.None };

// Derive a variant.
WebPEncoderOptions thumbnails = web with { Quality = 70 };
```
