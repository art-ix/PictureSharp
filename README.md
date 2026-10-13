# WebpSharp

WebpSharp is a **pure C# image library for .NET 10 and later**. It reads WebP, PNG, BMP and JPEG, and writes lossy and lossless WebP, PNG, BMP and JPEG. The library has no NuGet dependencies, native libraries or P/Invoke calls. PNG compression uses its own managed deflate/inflate implementation.

The `Image` API provides file and stream I/O, animation frames, metadata, pixel access and basic geometry operations. It uses `System.Drawing.Primitives` for types such as `Color` and `Rectangle`, without requiring `System.Drawing.Common`.

**Status:** in development. Phase 7 is complete, including static lossy and lossless WebP encoding. Animated WebP encoding and additional image processing are planned. See [plan.md](plan.md) for implementation phases and completion criteria.

## Supported formats

| Format | Reading | Writing |
| --- | --- | --- |
| **WebP** | Lossy VP8, lossless VP8L, alpha, animation, ICC/EXIF/XMP | Static lossy VP8 and lossless VP8L, near-lossless, alpha, ICC/EXIF/XMP |
| **PNG** | All standard color types and bit depths, transparency, Adam7, APNG | Grayscale, RGB/RGBA and palette images, Adam7, APNG |
| **BMP** | Palette and truecolor images, RLE4/RLE8/OS/2 RLE24, bit fields, alpha, OS/2 and V2–V5 headers, embedded PNG/JPEG | Uncompressed 1/4/8-bit palette, 24-bit RGB and 32-bit RGBA, automatic bit depth selection |
| **JPEG** | Baseline, extended and progressive Huffman coding; grayscale, YCbCr, RGB, CMYK and YCCK; restart markers | Baseline and progressive, quality 1–100, grayscale, 4:4:4/4:2:2/4:2:0/4:4:0 sampling, optimized Huffman tables and restart markers |

Decoded pixels use 8-bit `Rgba32` channels with straight (unpremultiplied) alpha. Higher-bit-depth inputs are converted to this representation. Animated WebP and APNG are decoded into complete canvas frames.

## Reading and writing images

```csharp
using WebpSharp;

using var image = Image.FromFile("input.png", new DecoderOptions
{
    ApplyExifOrientation = true,
    MaxPixels = 40_000_000,
});

image.Save("output.webp", new WebPEncoderOptions
{
    Quality = 85,
    Method = 4,
});
image.Save("exact.webp", new WebPEncoderOptions { Lossless = true, Exact = true });
image.Save("output.png", new PngEncoderOptions { Interlace = true });
image.Save("output.bmp", new BmpEncoderOptions());
image.Save("output.jpg", new JpegEncoderOptions
{
    Quality = 90,
    Progressive = true,
});

ImageInfo info = Image.Identify("output.webp");
Console.WriteLine($"{info.Format.Name}: {info.Width}x{info.Height}, frames: {info.FrameCount}");
```

Use `Image.FromStream` to read a stream or `Image.FromBytes` to decode an image already in memory. `Save(Stream, options)` writes to a caller-owned stream; streams remain open and do not need to support seeking. `ToWebP`, `ToPng`, `ToBmp` and `ToJpeg` return encoded byte arrays. Images own their pixel buffers and should be disposed with `using`.

`Save(path)` selects the format from the extension and uses its defaults. Convenience methods include `SaveAsPng`, `SaveAsBmp`, `SaveAsJpeg` and `SaveAsWebP`. WebP defaults to lossy encoding at quality 75 and method 4. Use `SaveAsWebP(path, lossless: true)` or `WebPEncoderOptions { Lossless = true }` for lossless output.

### Encoder options

| Options type | Main controls |
| --- | --- |
| `WebPEncoderOptions` | `Lossless`, `Quality` (0–100), `Method` (0–6), `Preset`, `Exact`, `NearLossless`, alpha quality/filter/compression, SNS, filtering, segments, partitions, target size/PSNR, passes and `UseSharpYuv` |
| `PngEncoderOptions` | `CompressionLevel` (0–9), `ColorType`, `FilterStrategy`, `Interlace`, `SaveAnimation` |
| `BmpEncoderOptions` | `BitsPerPixel` (automatic, 1/4/8/24/32) |
| `JpegEncoderOptions` | `Quality` (1–100), `Subsampling`, `Progressive`, `OptimizeCoding`, `RestartInterval`, `BackgroundColor` |

For a pixel-exact WebP round-trip, set `Lossless = true`, `Exact = true` and leave `NearLossless = 100`. With `Exact = false`, RGB values under fully transparent pixels may change to improve compression. Lower `NearLossless` values permit pixel quantization. For lossy WebP, `Quality` controls visual quality and `Method` controls compression effort. `Preset` sets content-specific defaults; explicit SNS, filter and segment settings override them. `AlphaQuality = 100` preserves alpha exactly. `UseSharpYuv` enables the more accurate RGB→YUV conversion. `TargetSize` or `TargetPsnr` works with `Passes`; reaching a target depends on the image. As in libwebp, methods 3–6 use one token partition, while methods 0–2 honor `Partitions`. Animation options are reserved for the next phase.

JPEG uses quality 75 and 4:2:0 sampling by default. Progressive output uses optimized Huffman tables. JPEG composites transparency onto `BackgroundColor` (white by default); JPEG and BMP save the active frame. PNG preserves animation by default. The WebP encoder currently rejects animated images.

### Metadata and decoding behavior

All encoder options expose `Metadata` flags to select the supported ICC, EXIF and XMP blocks. WebP, PNG and JPEG support these blocks; BMP supports embedded ICC profiles. Profiles are preserved without applying color management. EXIF orientation can be applied during loading with `ApplyExifOrientation` or afterward with `AutoOrient()`.

JPEG EXIF and XMP must each fit in one APP1 segment; ICC profiles can span multiple APP2 segments. Oversized metadata raises `ArgumentException`. JPEG arithmetic, lossless, hierarchical and 12-bit coding are unsupported.

`DecoderOptions` controls pixel limits, first-frame-only decoding, metadata loading, checksum validation and WebP upsampling/filtering. Invalid image data raises `ImageFormatException`; unsupported features raise `NotSupportedException`. JPEG decoding tolerates some truncated or damaged streams.

## Pixels, frames and geometry

```csharp
using WebpSharp;

using var image = new Image(64, 32);
Span<Rgba32> pixels = image.GetPixelSpan();
pixels.Fill(new Rgba32(30, 120, 200, 255));
image.GetRowSpan(0).Fill(new Rgba32(255, 255, 255, 255));

byte[] rgba = image.ToPixelArray(PixelFormat.Rgba32);
byte[] webp = WebP.EncodeLosslessRgba(rgba, image.Width, image.Height);
```

`Image.FromPixels`, `CopyPixels` and `LoadPixels` support explicit pixel formats and row strides. `GetPixel`/`SetPixel`, the pixel indexer and `LockBits` are also available. Pixel access operates on the active frame; use `Frames`, `FrameCount` and `SelectActiveFrame` to work with animations and frame durations.

Implemented geometry operations include `Crop`, `RotateFlip`, `AutoOrient`, `Clone` and `Clear`. Resizing, resampling and additional color/compositing operations are planned.

The `WebP` facade provides `TryGetInfo`, `DecodeImage`, `DecodeRgba`, `DecodeBgra`, `DecodeInto`, `Encode`, `EncodeLosslessRgba` and `Convert`. `Encode` and `Convert` use lossy WebP defaults unless lossless options are supplied.

## Building and running

Use the .NET 10 SDK or a compatible later SDK:

```bash
dotnet build -c Release
dotnet test -c Release
```

To use the library from a source checkout, add a project reference from your application to `src/WebpSharp/WebpSharp.csproj`. The library version is defined in [VERSION](VERSION).

The sample CLI supports image inspection, conversion and decode timing:

```bash
dotnet run -c Release --project samples/WebpSharp.Cli -- info input.webp
dotnet run -c Release --project samples/WebpSharp.Cli -- convert input.png output.webp -q 85 -m 4
dotnet run -c Release --project samples/WebpSharp.Cli -- convert input.png exact.webp -lossless -q 75 -m 4
dotnet run -c Release --project samples/WebpSharp.Cli -- convert input.webp output.jpg -q 90
dotnet run -c Release --project samples/WebpSharp.Cli -- decode input.webp 10
```

## Testing and reference tools

The committed synthetic corpus and reference hashes support tests without native image tools. Additional `Category=Oracle` checks compare results with external implementations when the required tools are available.

New WebP ports and encoder comparisons use **libwebp 1.6.0**. Build its pinned sources and command-line tools with Git, CMake and a C compiler:

```bash
./scripts/build-webp-reference.sh
dotnet test -c Release --filter Category=Oracle
```

Tests and corpus scripts automatically use `reference/libwebp-1.6.0-build/`. Set `WEBPSHARP_WEBP_TOOLS` to select another directory containing the 1.6.0 tools. Native sources and binaries stay in the Git-ignored `reference/` directory and are used only for verification.

The WebP decoder ports originated from libwebp 1.3.2. Recomputing the existing corpus references with 1.6.0 left all 248 entries unchanged. At completion of phase 7, all 2023 tests passed. The nine WebP encoder source images round-trip exactly through both decoders and meet the size limit of `cwebp -lossless -exact -q 75 -m 4` plus 1%; byte-identical lossless WebP output is not guaranteed. Lossy WebP was checked across 252 corpus configurations (nine images, four quality values, all seven methods). Opaque outputs are byte-identical to `cwebp`; where alpha compression differs, decoded pixels match exactly and output size stays within +1%. Presets, filters, alpha settings, SharpYuv, target size/PSNR and token partitions also have native oracle checks. JPEG encoder reference vectors are byte-identical to Pillow 10.2.0 / libjpeg-turbo 2.1.5.

Corpus regeneration requires libwebp 1.6.0 tools, ImageMagick, Python, NumPy and Pillow:

```bash
python3 -I tests/assets/generate_corpus.py
python3 -I tests/assets/make_reference.py
python3 -I tests/assets/make_jpeg_encoder_reference.py
```

Use Pillow 10.2.0 / libjpeg-turbo 2.1.5 to reproduce the committed JPEG encoder references. Additional JPEG oracle tests use a C compiler and libjpeg development headers, with Pillow and ImageMagick for independent decoding. External corpora are optional; their download procedure is described in [plan.md](plan.md).

## License

WebpSharp is licensed under [MIT](LICENSE). It includes ports from libwebp, libjpeg-turbo and zlib, and is based in part on the work of the Independent JPEG Group. Original license and copyright notices are retained in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
