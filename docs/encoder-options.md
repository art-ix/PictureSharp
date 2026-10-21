# Encoder options

Each format has an options record deriving from `EncoderOptions`: `WebPEncoderOptions`, `PngEncoderOptions`,
`JpegEncoderOptions`, `BmpEncoderOptions`, `GifEncoderOptions` and `PcxEncoderOptions` (ICO: [ICO icons](ico.md)). Pass
them to `Save`, `SaveAsync`, `SaveAs*`, `Encode` or `To*`:

```csharp
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.WebP;

image.SaveAsWebP("photo.webp", new() { Quality = 85, Method = 6 });
image.Save("photo.jpg", new JpegEncoderOptions { Quality = 90 });
```

- Out-of-range values throw `ArgumentOutOfRangeException` where they are set, not during encoding.
- Options are records: `var hq = options with { Quality = 95 };`.
- `Metadata` (default `MetadataKinds.All`) chooses which of ICC, EXIF and XMP are written; see [Metadata](metadata.md).
- `Configuration` selects the formats of this save; see
  [Loading and saving](loading-and-saving.md#choosing-the-formats-imageconfiguration).
- `MaxDegreeOfParallelism` (WebP, PNG, JPEG; default -1 = automatic) never changes the output bytes.

## WebP

```csharp
using PictureSharp.Formats.WebP;

image.SaveAsWebP("lossy.webp");                                              // quality 75, method 4
image.SaveAsWebP("small.webp", new() { Quality = 70, Method = 6 });
image.SaveAsWebP("exact.webp", new() { Lossless = true, Exact = true });
image.SaveAsWebP("near.webp", new() { Lossless = true, NearLossless = 60 });
image.SaveAsWebP("target.webp", new() { TargetSize = 50_000, Passes = 6 });
```

| Property | Default | Meaning |
| --- | --- | --- |
| `Lossless` | false | Lossless VP8L instead of lossy VP8. |
| `Quality` | 75 | 0–100. Lossy: visual quality. Lossless: compression effort. |
| `Method` | 4 | 0 (fast) – 6 (slowest, smallest). |
| `Preset` | `Default` | As `cwebp -preset`: `Picture`, `Photo`, `Drawing`, `Icon`, `Text`. |
| `Exact` | false | Keep RGB under fully transparent pixels. |
| `NearLossless` | 100 | Lossless: lower values quantize pixels for smaller files; 100 is off. |
| `AlphaQuality`, `AlphaCompression`, `AlphaFilter` | 100, true, `Fast` | Lossy alpha plane: quality (100 is exact), lossless compression, filter (`None`, `Fast`, `Best`). |
| `SnsStrength`, `FilterStrength`, `FilterSharpness`, `Segments` | preset (50, 60, 0, 4) | Noise shaping 0–100, deblocking 0–100 (0 off), sharpness 0–7, segments 1–4. |
| `AutoFilter` | false | Choose the filter strength automatically. |
| `Partitions` | 0 | log2 of token partitions (0–3); methods 3–6 always use one. |
| `TargetSize`, `TargetPsnr`, `Passes` | 0, 0, 1 | Target bytes (overrides `Quality`) or PSNR, reached in 1–10 passes. |
| `UseSharpYuv` | false | Slower, sharper RGB→YUV conversion. |
| `AllowMixed`, `MinimizeSize`, `KeyframeMin`, `KeyframeMax` | false, false, 9, 17 | Animation only; see [Animation](animation.md#saving-animated-webp). |

Presets set `SnsStrength`/`FilterStrength`/`FilterSharpness` to: `Picture` 80/35/4, `Photo` 80/30/3, `Drawing` 25/10/6,
`Icon` 0/0/0, `Text` 0/0/0 with 2 segments; explicit values win. On the corpus, opaque lossy and all lossless output are
byte-identical to `cwebp` 1.6.0 with the same settings.

## PNG

```csharp
using PictureSharp.Formats.Png;

image.SaveAsPng("out.png");                                                  // smallest lossless color type
image.SaveAsPng("fast.png", new() { CompressionLevel = 1 });
image.SaveAsPng("indexed.png", new() { ColorType = PngColorType.Palette });
image.SaveAsPng("interlaced.png", new() { Interlace = true });
```

| Property | Default | Meaning |
| --- | --- | --- |
| `CompressionLevel` | 6 | Deflate level 0 (store) – 9 (best). |
| `ColorType` | `Auto` | `Auto` picks the smallest lossless type. `Grayscale`/`GrayscaleAlpha` convert to luma, `Rgb` drops alpha, `Palette` throws `InvalidOperationException` above 256 colors. |
| `FilterStrategy` | `Adaptive` | Per-row filter choice, or a fixed `None`, `Sub`, `Up`, `Average`, `Paeth`. |
| `Interlace` | false | Adam7 interlacing. |
| `SaveAnimation` | true | Write animations as APNG (otherwise only the first frame). |

## JPEG

```csharp
using System.Drawing;
using PictureSharp.Formats.Jpeg;

image.SaveAsJpeg("photo.jpg", new() { Quality = 88 });
image.SaveAsJpeg("web.jpg", new() { Quality = 80, Progressive = true });
image.SaveAsJpeg("sharp-color.jpg", new() { Subsampling = JpegSubsampling.Ratio444 });
image.SaveAsJpeg("on-black.jpg", new() { BackgroundColor = Color.Black });   // transparent areas become black
```

| Property | Default | Meaning |
| --- | --- | --- |
| `Quality` | 75 | 1–100 (libjpeg scaling). |
| `Subsampling` | `Ratio420` | `Ratio444`, `Ratio422`, `Ratio420`, `Ratio440`. |
| `ColorMode` | `Auto` | `Auto` writes one component when every pixel is grey; `Color` always writes YCbCr; `Grayscale` always writes the luma alone. |
| `Progressive` | false | Progressive scans (implies optimized Huffman tables). |
| `OptimizeCoding` | false | Optimal Huffman tables in an extra pass. |
| `RestartInterval` | 0 | MCUs between restart markers (0–65535); 0 disables them. |
| `BackgroundColor` | white | Color under transparent pixels. |
| `GainMap` | `None` | `Preserve` or `Require` writes the HDR gain map; see [HDR photos](hdr.md#saving-with-the-gain-map). |
| `GainMapQuality` | 95 | 1–100 quality of a re-encoded gain map. |

JPEG writes the first frame; grayscale images get one component. Output is byte-identical to Pillow 10.2.0 /
libjpeg-turbo 2.1.5 on the reference vectors.

## BMP

```csharp
using PictureSharp.Formats.Bmp;

image.SaveAsBmp("out.bmp");                                                  // automatic bit depth
image.SaveAsBmp("rgb.bmp", new() { BitsPerPixel = 24 });
```

`BitsPerPixel` is 0 (automatic), 1, 4, 8, 24 or 32. Automatic picks a palette for opaque images with up to 256 colors,
24 bits for other opaque images and 32 bits otherwise; palette and 24-bit output drop alpha. BMP is uncompressed, stores
the first frame and embeds ICC in a `BITMAPV5HEADER`.

## GIF

```csharp
using PictureSharp.Formats.Gif;

image.SaveAsGif("out.gif");                                                  // exact colors when they fit, else median cut
image.SaveAsGif("small.gif", new() { ColorCount = 64, Dither = GifDither.FloydSteinberg });
image.SaveAsGif("fast.gif", new() { Quantizer = GifQuantizer.Octree });
image.SaveAsGif("soft-edges.gif", new() { TransparencyThreshold = 32 });     // keep faint pixels opaque
```

| Property | Default | Meaning |
| --- | --- | --- |
| `ColorCount` | 256 | 2–256 entries per frame, including a transparent entry when needed. |
| `Quantizer` | `Auto` | When colors do not fit: `MedianCut` (the `Auto` choice) or `Octree`, ported from Pillow. |
| `Dither` | `None` | `FloydSteinberg` error diffusion (smoother gradients, larger files). |
| `TransparencyThreshold` | 128 | Lower alpha becomes transparent, the rest opaque. |
| `Interlace` | false | Four interlaced passes. |
| `SaveAnimation` | true | Write all frames (otherwise only the first). |
| `OptimizeFrames` | true | Write only changed rectangles and merge repeated frames. |

Colors that fit are kept exactly (one global palette, or exact local palettes per frame). Otherwise a still image is
quantized exactly as Pillow's `Image.quantize(colors)`, and an animation shares one quantized palette. Durations are
stored in hundredths of a second (at most 655.35 s). ICC and XMP are written as extensions; GIF has no EXIF.

## PCX

```csharp
using PictureSharp.Formats.Pcx;

image.SaveAsPcx("out.pcx");                                                  // automatic bit depth
image.SaveAsPcx("rgb.pcx", new() { BitsPerPixel = 24 });
```

`BitsPerPixel` is `0` (automatic), `1` (two colors), `4` (16 colors on four planes), `8` (256 colors with a VGA
palette), `24` (RGB) or `32` (RGBA). Automatic writes 32 bits when any pixel is transparent, otherwise the smallest
depth that holds the colors exactly. Forced 1, 4 and 8 throw when the colors do not fit, and every forced depth except
32 drops alpha. 32-bit PCX is the four-plane form ImageMagick writes (Pillow cannot read it). Planes are unpadded so
Pillow reads them correctly. Resolution comes from `Image.Metadata` (outside 1–65535 dpi it is written as 96). PCX has
no ICC, EXIF or XMP and stores the first frame.

## Recipes

```csharp
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.WebP;

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
