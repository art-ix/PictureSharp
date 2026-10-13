# WebpSharp

A pure managed .NET 10 library for encoding and decoding **WebP**, with **PNG/BMP/JPEG import** and **PNG/BMP export**. It has no NuGet dependencies and no native code. The `Image` class works like `System.Drawing.Image`/`Bitmap`.

> Status: **work in progress**. See [plan.md](plan.md) for the roadmap. Done: phase 0 (core types, `Image` API, test corpus), phase 1 (WebP container parsing, lossless VP8L decoder), phase 2 (lossy VP8 decoder, alpha, animation, metadata) and phase 3 (own zlib inflate/deflate, PNG/APNG decoder and encoder). The WebP decoder is bit-exact with libwebp 1.3.2.

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
  - reading 1/4/8/16/24/32-bit files, RLE4/RLE8, bit fields, alpha, V5 headers with ICC
  - writing with automatic bit depth
- **JPEG:**
  - decoding of baseline and progressive files, CMYK/YCCK
  - EXIF orientation
- **`Image`:**
  - frames
  - pixel access (`GetPixel`, `LockBits`, `Span<Rgba32>`)
  - rotate/flip/crop/resize
  - simple color operations

## Building

```bash
dotnet build
dotnet test
```

The test corpus in `tests/assets/corpus` is generated with `tests/assets/generate_corpus.py`, and its reference decodes with `make_reference.py`. Both need libwebp tools, ImageMagick and Pillow.

## License

BSD-3-Clause. Parts of the code are ports of libwebp (Copyright Google Inc., BSD-3-Clause); see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
