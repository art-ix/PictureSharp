# WebpSharp

A pure managed .NET 10 library for encoding and decoding **WebP**, with **PNG/BMP/JPEG import** and **PNG/BMP/JPEG export**. It has no NuGet dependencies and no native code. The `Image` class works like `System.Drawing.Image`/`Bitmap`.

> Status: **work in progress**. See [plan.md](plan.md) for the roadmap. 

```csharp
using WebpSharp;

using var img = Image.FromFile("photo.jpg");
img.AutoOrient().SaveAsWebP("photo.webp", quality: 82);

ImageInfo info = Image.Identify("anim.webp");
Console.WriteLine($"{info.Width}x{info.Height}, frames: {info.FrameCount}");
```

## Features (target)

- **WebP:**
  - lossy (VP8), lossless (VP8L) and alpha
  - animation
  - ICC/EXIF/XMP metadata
  - ported from libwebp 1.3.2 and verified against `cwebp`/`dwebp`
- **PNG:**
  - all color types and bit depths, Adam7 interlacing, APNG
  - own inflate/deflate implementation
- **BMP:**
  - reading 1/2/4/8/16/24/32-bit files, RLE4/RLE8 (and OS/2 RLE24), arbitrary bit fields, alpha, OS/2 and V2–V5 headers, embedded ICC and PNG
  - writing 1/4/8-bit palette, 24-bit and 32-bit (alpha) files, with automatic bit depth
- **JPEG:**
  - decoding of baseline, extended and progressive Huffman files, any integral sampling factors, restart markers
  - grayscale, YCbCr, RGB, CMYK and YCCK
  - EXIF/XMP/ICC metadata, JFIF/EXIF resolution, EXIF orientation
  - tolerant decoding of truncated or damaged files
  - baseline and progressive encoding, quality 1–100, optional optimal Huffman tables and restart markers
  - 4:4:4, 4:2:2, 4:2:0 (default) and 4:4:0 chroma sampling; automatic grayscale detection
  - transparent pixels composited on a configurable opaque background (white by default); saves the active frame
  - not supported: arithmetic coding, lossless, hierarchical and 12-bit files
- **`Image`:**
  - frames
  - pixel access (`GetPixel`, `LockBits`, `Span<Rgba32>`)
  - rotate/flip/crop/resize
  - simple color operations

## JPEG export

```csharp
using var image = Image.FromFile("input.png");
image.SaveAsJpeg("output.jpg", quality: 90);
byte[] jpeg = image.ToJpeg(new JpegEncoderOptions
{
    Quality = 90,
    Subsampling = JpegSubsampling.Ratio444,
    Progressive = true,
    BackgroundColor = System.Drawing.Color.White,
});
```

`Save("output.jpg")` and `Save("output.jpeg")` use quality 75 and 4:2:0 sampling.
Progressive output always uses optimal Huffman tables. `RestartInterval` counts MCUs
within each scan. ICC profiles are preserved without color management; EXIF and XMP
must each fit in one APP1 segment (65527 and 65504 bytes respectively). ICC profiles
may span up to 255 APP2 segments. Oversized metadata raises `ArgumentException`;
use `Metadata` flags to omit unwanted blocks. JFIF DPI is rounded to an integer
and clamped to 1–65535.

Encoding is verified byte for byte against Pillow 10.2.0 / libjpeg-turbo 2.1.5.
Regenerate the committed encoder hashes with
`python3 -I tests/assets/make_jpeg_encoder_reference.py` after generating the source
corpus. These tests run without external tools. Additional `Category=Oracle` tests
compare 4:4:0 sampling and restarts with system libjpeg (a C compiler and libjpeg
development headers are required), and decode output with Pillow and ImageMagick
when installed. Native code is used only by the test oracle.

## Building

```bash
dotnet build
dotnet test
```

The test corpus in `tests/assets/corpus` is generated with `tests/assets/generate_corpus.py`, and its reference decodes with `make_reference.py`. Both need libwebp tools, ImageMagick and Pillow.

## License

MIT, see [LICENSE](LICENSE). Parts of the code are ported from libwebp (Copyright Google Inc.) and libjpeg-turbo; this software is based in part on the work of the Independent JPEG Group. The original copyright notices are kept in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
