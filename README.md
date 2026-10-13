# RasterSharp

**A pure managed image processing library for .NET. No native dependencies.**

RasterSharp is a **pure C# image library for .NET 10 and later**. It reads WebP, PNG, BMP and JPEG, and writes lossy and lossless WebP, PNG, BMP and JPEG. The library has no NuGet dependencies, native libraries or P/Invoke calls. PNG compression uses its own managed deflate/inflate implementation.

The `Image` API follows the shape of ImageSharp: `Image.Load`, `image.Mutate(x => ...)` for in-place processing, `image.Clone(x => ...)` for a processed copy, and `SaveAs*` methods per format. It covers file and stream I/O (sync and async), animation frames, metadata, pixel access, resizing, geometry, color adjustments, filters and compositing. It uses `System.Drawing.Primitives` for `Size`, `Rectangle`, `Point` and `Color`, without requiring `System.Drawing.Common`.

**Status:** in development. Phase 9.5 is complete: an ImageSharp-style API (`Load`/`Mutate`/`Clone`/`SaveAs*`) with Pillow-exact filters and rotation joins static and animated WebP, PNG, BMP and JPEG reading and writing. Performance work and the release are next. See [plan.md](plan.md) for implementation phases and completion criteria.

## Supported formats

| Format | Reading | Writing |
| --- | --- | --- |
| **WebP** | Lossy VP8, lossless VP8L, alpha, animation, ICC/EXIF/XMP | Static and animated lossy VP8 and lossless VP8L, mixed animation, near-lossless, alpha, ICC/EXIF/XMP |
| **PNG** | All standard color types and bit depths, transparency, Adam7, APNG | Grayscale, RGB/RGBA and palette images, Adam7, APNG |
| **BMP** | Palette and truecolor images, RLE4/RLE8/OS/2 RLE24, bit fields, alpha, OS/2 and V2–V5 headers, embedded PNG/JPEG | Uncompressed 1/4/8-bit palette, 24-bit RGB and 32-bit RGBA, automatic bit depth selection |
| **JPEG** | Baseline, extended and progressive Huffman coding; grayscale, YCbCr, RGB, CMYK and YCCK; restart markers | Baseline and progressive, quality 1–100, grayscale, 4:4:4/4:2:2/4:2:0/4:4:0 sampling, optimized Huffman tables and restart markers |

Decoded pixels use 8-bit `Rgba32` channels with straight (unpremultiplied) alpha. Higher-bit-depth inputs are converted to this representation. Animated WebP and APNG are decoded into complete canvas frames.

## Quick start

```csharp
using RasterSharp;

using var image = await Image.LoadAsync("photo.jpg");
image.Mutate(x => x
    .AutoOrient()
    .Resize(1600, 0)                 // 0 keeps the aspect ratio
    .UnsharpMask());
await image.SaveAsWebPAsync("photo.webp", new() { Quality = 82 });

using Image thumbnail = image.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
thumbnail.SaveAsPng("thumbnail.png");
```

`Mutate` changes the image it is called on; `Clone` returns a new image and leaves the source untouched. Operations run in order on every animation frame. Images own their pixel buffers and should be disposed with `using`.

## Reading and writing images

```csharp
using RasterSharp;

using var image = Image.Load("input.png", new DecoderOptions
{
    ApplyExifOrientation = true,
    MaxPixels = 40_000_000,
});

image.Save("output.webp");                                           // format from the extension, default options
image.SaveAsWebP("exact.webp", new() { Lossless = true, Exact = true });
image.SaveAsPng("output.png", new() { Interlace = true });
image.SaveAsJpeg("output.jpg", new() { Quality = 90, Progressive = true });
image.SaveAsBmp(stream);                                             // any writable stream; it is left open
byte[] webp = image.ToWebP();

ImageInfo info = Image.Identify("output.webp");                      // no pixel decoding
Console.WriteLine($"{info.Format.Name}: {info.Width}x{info.Height}, frames: {info.FrameCount}");
ImageFormat? format = Image.DetectFormat("unknown.bin");
```

`Image.Load` accepts a path, a `Stream` (seeking is not needed), a byte array (used without copying) or a span. `LoadAsync`, `IdentifyAsync`, `SaveAsync` and `SaveAs*Async` take a `CancellationToken`. Each format has `SaveAsWebP`/`SaveAsPng`/`SaveAsJpeg`/`SaveAsBmp` for paths and streams, their async variants and `ToWebP`/`ToPng`/`ToJpeg`/`ToBmp`, all written as C# 14 extension members. `Save(path)` selects the format from the extension; WebP defaults to lossy quality 75 and method 4, JPEG to quality 75.

### Encoder options

Options are records: values are checked when they are set, so an invalid quality fails at the line that sets it, and `with` makes variants (`options with { Quality = 90 }`).

| Options type | Main controls |
| --- | --- |
| `WebPEncoderOptions` | `Lossless`, `Quality` (0–100), `Method` (0–6), `Preset`, `Exact`, `NearLossless`, alpha quality/filter/compression, SNS, filtering, segments, partitions, target size/PSNR, passes, `UseSharpYuv`, `AllowMixed`, `MinimizeSize`, `KeyframeMin` and `KeyframeMax` |
| `PngEncoderOptions` | `CompressionLevel` (0–9), `ColorType`, `FilterStrategy`, `Interlace`, `SaveAnimation` |
| `BmpEncoderOptions` | `BitsPerPixel` (automatic, 1/4/8/24/32) |
| `JpegEncoderOptions` | `Quality` (1–100), `Subsampling`, `Progressive`, `OptimizeCoding`, `RestartInterval`, `BackgroundColor` |

For a pixel-exact WebP round-trip, set `Lossless = true`, `Exact = true` and leave `NearLossless = 100`. With `Exact = false`, RGB values under fully transparent pixels may change to improve compression. Lower `NearLossless` values permit pixel quantization. For lossy WebP, `Quality` controls visual quality and `Method` controls compression effort. `Preset` sets content-specific defaults; explicit SNS, filter and segment settings override them. `AlphaQuality = 100` preserves alpha exactly. `UseSharpYuv` enables the more accurate RGB→YUV conversion. `TargetSize` or `TargetPsnr` works with `Passes`; reaching a target depends on the image. As in libwebp, methods 3–6 use one token partition, while methods 0–2 honor `Partitions`.

JPEG uses quality 75 and 4:2:0 sampling by default. Progressive output uses optimized Huffman tables. JPEG composites transparency onto `BackgroundColor` (white by default); JPEG and BMP save the first frame. PNG and WebP preserve all animation frames by default.

### Animated WebP

```csharp
using var first = Image.Load("frame-1.png");
using var second = Image.Load("frame-2.png");
using var animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), first, second);
animation.LoopCount = 0;                                   // forever
animation.Frames[1].Duration = TimeSpan.FromMilliseconds(150);
animation.SaveAsWebP("animation.webp", new() { Lossless = true, Exact = true, MinimizeSize = true });
```

Frames must have the same dimensions. `Frames` also offers `Add`, `Insert`, `RemoveAt`, `Move`, `CloneFrame(i)` and `ExportFrame(i)` (both return a still `Image`). WebP stores durations rounded to the nearest millisecond, from 0 to 16,777,215 ms per frame. `LoopCount` accepts 0–65,535; zero means infinite playback. Duplicate frames retain their individual durations. `AnimationBackgroundColor`, metadata and canvas dimensions are stored in the container.

The encoder tries even-offset change rectangles, blend/no-blend and disposal to transparent background, selecting the smallest encoded candidate. `AllowMixed = true` also compares VP8 and VP8L for each frame, so an animation may contain lossy frames even with `Lossless = true`. Leave it disabled for a pixel-exact lossless round-trip. `MinimizeSize` also tries full-canvas candidates and disables periodic keyframes. Otherwise `KeyframeMax = 0` disables insertion, `1` makes every frame a keyframe, and larger values bound the distance between keyframes. The search window after `KeyframeMin` is normalized as in libwebp (at most 30 candidates, with the lower bound raised above half of `KeyframeMax` when possible). Animated canvases currently use the same dimension limits as static frames: 16,383 pixels per side for lossy, or 16,384 when lossless or mixed encoding is available.

### Metadata and decoding behavior

`image.Metadata` holds the resolution (`HorizontalResolution`/`VerticalResolution` in DPI) and the raw ICC, EXIF and XMP blocks; `Orientation` reads and writes the EXIF orientation tag. All encoder options expose `Metadata` flags to select the blocks to write. WebP, PNG and JPEG support these blocks; BMP supports embedded ICC profiles. Profiles are preserved without applying color management. EXIF orientation can be applied during loading with `ApplyExifOrientation` or afterward with `Mutate(x => x.AutoOrient())`.

JPEG EXIF and XMP must each fit in one APP1 segment; ICC profiles can span multiple APP2 segments. Oversized metadata raises `ArgumentException`. JPEG arithmetic, lossless, hierarchical and 12-bit coding are unsupported.

`DecoderOptions` controls pixel limits, first-frame-only decoding, metadata loading, checksum validation and WebP upsampling/filtering. Invalid image data raises `ImageFormatException`; unsupported features raise `NotSupportedException`. JPEG decoding tolerates some truncated or damaged streams.

## Image processing

```csharp
using System.Drawing;
using RasterSharp;

using var photo = Image.Load("photo.jpg");
using var logo = Image.Load("logo.png");
photo.Mutate(x => x
    .AutoOrient()
    .Resize(new ResizeOptions { Size = new(1200, 800), Mode = ResizeMode.Crop, Position = AnchorPosition.Top })
    .Contrast(1.1f)
    .DrawImage(logo, AnchorPosition.BottomRight, opacity: 0.8f));
photo.SaveAsJpeg("card.jpg", new() { Quality = 88 });
```

The lambda receives an `ImageProcessingContext` (a `ref struct`, so it cannot outlive the call); `x.Size` reports the size reached so far. `Clone(x => ...)` copies source pixels only when an operation needs to change them in place, so `Clone(x => x.Resize(...))` does not copy the full-size image first. If an operation throws inside `Mutate`, earlier operations stay applied.

| Operation | Behavior |
| --- | --- |
| `Resize(w, h)`, `Resize(size)`, `Resize(w, h, mode, filter)`, `Resize(ResizeOptions)` | 0 for one side keeps the aspect ratio. Modes: `Stretch`, `Max` (fit inside), `Crop` (cover, crop at `Position`), `Pad` (fit, then pad with `PadColor` at `Position`), `Min` (cover without cropping). `AllowUpscale = false` never enlarges. Filters: `Nearest`, `Bilinear`, `Bicubic`, `Lanczos3` (default), `Box`, `Hamming`. |
| `Thumbnail(maxW, maxH)` | Shrinks to fit (0 = unbounded side); never enlarges. |
| `Crop(rect)`, `Crop(x, y, w, h)`, `Crop(w, h, anchor)` | Exact region; the anchored form keeps the part at the given position. |
| `Pad(w, h, color, anchor)` | Places the image on a larger (or smaller) canvas without scaling. |
| `Rotate(RotateMode)`, `Flip(FlipMode)`, `RotateFlip`, `AutoOrient` | Lossless quarter turns and mirrors; `AutoOrient` applies EXIF orientation 1–8 and resets the tag. |
| `Rotate(degrees, filter, background, expand)` | Any clockwise angle with `Nearest`, `Bilinear` or `Bicubic`; `expand` (default) grows the canvas. |
| `Brightness`, `Contrast`, `Saturate` | 1 keeps the image; 0 gives black, flat gray or grayscale; larger values strengthen. |
| `Grayscale`, `Invert`, `Sepia` | BT.601 luma, RGB inversion, sepia matrix. Alpha is kept. |
| `BoxBlur(radius)`, `GaussianBlur(sigma)`, `UnsharpMask(radius, percent, threshold)`, `Sharpen()` | Blur and sharpen on premultiplied alpha, so transparent pixels do not leak color. |
| `Fill(color)`, `Fill(color, rect)`, `MakeTransparent(color)` | Overwrite pixels (no blending); `MakeTransparent` sets alpha to 0 on an exact color match. |
| `BackgroundColor(color)`, `Opacity(amount)` | Flatten transparency onto a color; scale alpha. |
| `DrawImage(source, point or anchor, opacity)` | Source-over compositing of the source's first frame, clipped to the image. |
| `Apply(frame => ...)` | Custom per-frame pixel work through `ImageFrame.GetPixelSpan()`. |

Custom operations read like built-in ones when written as C# 14 extension members:

```csharp
using System.Drawing;
using RasterSharp;

public static class MyOperations
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext Polaroid() => x.Pad(x.Size.Width + 40, x.Size.Height + 120, Color.White, AnchorPosition.Top);
    }
}
```

Every operation reproduces Pillow 10.2.0 pixel for pixel: resizing (`Image.resize`, `ImageOps.contain`/`fit`/`pad` with `centering`), compositing (`Image.alpha_composite`), `ImageEnhance`, `ImageFilter.BoxBlur`/`GaussianBlur`/`UnsharpMask`/`SHARPEN` and `Image.rotate(-degrees)`. Blur, sharpening and interpolated rotation run on premultiplied alpha, as Pillow does on an `RGBa` image. As in Pillow, semi-transparent colors pass through 8-bit premultiplied values, so their RGB may shift slightly; opaque colors are preserved exactly.

## Pixels and frames

```csharp
using System.Drawing;
using RasterSharp;

using var image = new Image(64, 32, Color.CornflowerBlue);
image[0, 0] = Rgba32.Parse("#ff8800");                 // Color and Rgba32 convert implicitly
Span<Rgba32> row = image.GetRowSpan(1);
row.Fill(new Rgba32(255, 255, 255, 128));

byte[] bgra = image.ToPixelData(PixelFormat.Bgra32);   // e.g. WPF BitmapSource.Create or SkiaSharp Bgra8888
using Image copy = Image.LoadPixelData(bgra, image.Width, image.Height, PixelFormat.Bgra32);
```

Pixel accessors (`this[x, y]`, `GetRowSpan`, `GetPixelSpan`, `GetPixelMemory`, `CopyPixelDataTo`/`CopyPixelDataFrom`, `ToPixelData`) work on the first frame; other frames are available through `image.Frames[i]`. Pixel formats: `Rgba32`, `Bgra32`, `Argb32`, `Rgb24`, `Bgr24`, `Gray8`, `Rgba32Premultiplied`, `Bgra32Premultiplied`, with optional row stride. For native interop, `GetPixelMemory().Pin()` returns a handle to the RGBA buffer without copying.

The `WebP` facade provides libwebp-style one-call functions: `TryGetInfo`, `DecodeImage`, `DecodeRgba`, `DecodeBgra`, `DecodeInto`, `Encode`, `EncodeLosslessRgba` and `Convert`.

The public surface is recorded in [tests/assets/PublicApi.approved.txt](tests/assets/PublicApi.approved.txt); `PublicApiTests` fails on any unreviewed change.

## Building and running

Use the .NET 10 SDK or a compatible later SDK:

```bash
dotnet build -c Release
dotnet test -c Release
```

To use the library from a source checkout, add a project reference from your application to `src/RasterSharp/RasterSharp.csproj`. The library version is defined in [VERSION](VERSION).

The sample CLI supports image inspection, conversion, resizing and decode timing:

```bash
dotnet run -c Release --project samples/RasterSharp.Cli -- info input.webp
dotnet run -c Release --project samples/RasterSharp.Cli -- convert input.png output.webp -q 85 -m 4
dotnet run -c Release --project samples/RasterSharp.Cli -- convert input.png exact.webp -lossless -q 75 -m 4
dotnet run -c Release --project samples/RasterSharp.Cli -- convert input.webp output.jpg -q 90
dotnet run -c Release --project samples/RasterSharp.Cli -- resize input.jpg thumb.webp 800 0 -mode max -filter lanczos3 -q 80
dotnet run -c Release --project samples/RasterSharp.Cli -- decode input.webp 10
```

## Testing and reference tools

The committed synthetic corpus and reference hashes support tests without native image tools. Additional `Category=Oracle` checks compare results with external implementations when the required tools are available.

New WebP ports and encoder comparisons use **libwebp 1.6.0**. Build its pinned sources and command-line tools with Git, CMake and a C compiler:

```bash
./scripts/build-webp-reference.sh
dotnet test -c Release --filter Category=Oracle
```

Tests and corpus scripts automatically use `reference/libwebp-1.6.0-build/`. Set `RASTERSHARP_WEBP_TOOLS` to select another directory containing the 1.6.0 tools. Native sources and binaries stay in the Git-ignored `reference/` directory and are used only for verification.

The WebP decoder ports originated from libwebp 1.3.2. Recomputing the existing corpus references with 1.6.0 left all 248 entries unchanged. At completion of phase 9.5, all 2203 tests passed. The nine WebP encoder source images round-trip exactly through both decoders and meet the size limit of `cwebp -lossless -exact -q 75 -m 4` plus 1%; byte-identical lossless WebP output is not guaranteed. Lossy WebP was checked across 252 corpus configurations (nine images, four quality values, all seven methods). Opaque outputs are byte-identical to `cwebp`; where alpha compression differs, decoded pixels match exactly and output size stays within +1%. Presets, filters, alpha settings, SharpYuv, target size/PSNR and token partitions also have native oracle checks. JPEG encoder reference vectors are byte-identical to Pillow 10.2.0 / libjpeg-turbo 2.1.5.

Corpus regeneration requires libwebp 1.6.0 tools, ImageMagick, Python, NumPy and Pillow:

```bash
python3 -I tests/assets/generate_corpus.py
python3 -I tests/assets/make_reference.py
python3 -I tests/assets/make_jpeg_encoder_reference.py
```

Animated WebP has 46 encoder tests, including 38 external checks through Pillow and webpmux 1.6.0. These compare complete decoded canvases, durations, loop counts, background color and EXIF extraction across lossy, lossless, mixed, blend/dispose and keyframe cases.

Image processing has 140 tests. The Pillow 10.2.0 oracle (`tests/assets/processing_reference.py`) checks 569 results for exact pixel equality: resizing with all filters, modes and anchors, compositing, background flattening, grayscale, enhancement, blur and sharpening, rotation, sepia and EXIF orientation.

Use Pillow 10.2.0 / libjpeg-turbo 2.1.5 to reproduce the committed JPEG encoder references. Additional JPEG oracle tests use a C compiler and libjpeg development headers, with Pillow and ImageMagick for independent decoding. External corpora are optional; their download procedure is described in [plan.md](plan.md).

## License

RasterSharp is licensed under [MIT](LICENSE). It includes ports from libwebp, libjpeg-turbo, zlib and Pillow, and is based in part on the work of the Independent JPEG Group. Original license and copyright notices are retained in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
