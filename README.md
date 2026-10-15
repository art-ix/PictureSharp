# PictureSharp

**A pure managed image processing library for .NET. No native dependencies.**

PictureSharp reads and writes WebP, PNG, JPEG, BMP, GIF, PCX and ICO and processes images in 100% C# for **.NET 10 and later**: no NuGet dependencies, native libraries or P/Invoke. Everything starts from `Image` (`Image.Load`, `Mutate`, `Clone`, `Save`, `SaveAs*`) with a single `using PictureSharp;`, and results are verified pixel for pixel against libwebp, libjpeg-turbo and Pillow.

- WebP lossy, lossless, alpha and animation (encoder byte-compatible with `cwebp` 1.6.0 where possible)
- PNG (including APNG and Adam7), JPEG (baseline and progressive), BMP
- GIF with animation, and GIF ↔ WebP conversion compatible with `gif2webp` 1.6.0
- ICO with PNG/DIB variants, alpha masks and multiple independent sizes
- PCX with palette and grayscale images, RGB and RGBA, and automatic output bit depth
- Resize, crop, pad, rotate, flip, EXIF auto-orientation
- Fast JPEG thumbnails: decoding at 1/2, 1/4 or 1/8 scale (bit-exact with libjpeg-turbo) without full-size buffers
- Brightness, contrast, saturation, blur, sharpen, compositing and watermarks
- Raw pixel access and conversion to BGRA/RGB/gray buffers for interop
- SIMD on x64 (SSE2–AVX2) and ARM64 (AdvSimd/NEON), bit-identical to the scalar code
- ICC, EXIF and XMP metadata preserved where the output format supports them
- Typed EXIF and metadata editing without image recompression in source version 0.6.1
- Limits for untrusted input (encoded size, pixels, frames, metadata) and cancellation of decoding, encoding and processing
- Extensible: format plugins, per-call or application-wide format configurations, custom frame processors

| Format | Read | Write |
| --- | --- | --- |
| WebP | ✔ lossy, lossless, alpha, animation | ✔ lossy, lossless, near-lossless, alpha, animation |
| PNG | ✔ all color types and bit depths, APNG | ✔ gray, RGB(A), palette, Adam7, APNG |
| JPEG | ✔ baseline, progressive, CMYK/YCCK | ✔ baseline, progressive, 4:4:4–4:2:0 |
| BMP | ✔ palette, RLE, bit fields, alpha, embedded PNG/JPEG | ✔ 1/4/8/24/32-bit |
| GIF | ✔ GIF87a/89a, interlace, transparency, animation | ✔ exact or quantized palettes (Pillow median cut/octree), dithering, animation |
| ICO | ✔ PNG, DIB 1/4/8/24/32-bit | ✔ PNG or 32-bit DIB, multiple sizes |
| PCX | ✔ 1–8 bit planar and packed, 256-color, 24-bit RGB, 32-bit RGBA | ✔ 1/4/8/24/32-bit, RLE |

## Quick start

```csharp
using PictureSharp;

using var image = await Image.LoadAsync("photo.jpg");
image.Mutate(x => x
    .AutoOrient()
    .Resize(1600, 0)                 // 0 keeps the aspect ratio
    .UnsharpMask());
await image.SaveAsync("photo.webp");  // the format comes from the extension

using Image thumbnail = image.Clone(x => x.Thumbnail(256, 256));
thumbnail.Save("thumbnail.png");
```

`Mutate` changes the image it is called on; `Clone` returns a new image and leaves the source untouched. Format-specific settings such as WebP quality live in `PictureSharp.Formats.<Format>` (`image.SaveAsWebP(path, new() { Quality = 82 })`), and resize modes in `PictureSharp.Processing`; see the [encoder options](https://github.com/art-ix/PictureSharp/blob/main/docs/encoder-options.md) and [image processing](https://github.com/art-ix/PictureSharp/blob/main/docs/image-processing.md) guides.

PictureSharp 0.6.0 adds `Image.OpenFrameReaderAsync` and `Image.CreateFrameWriter`
to process GIF, APNG and WebP one frame at a time. See
[streaming ownership, output capabilities and examples](https://github.com/art-ix/PictureSharp/blob/main/docs/animation.md#sequential-reading-and-writing-060).

Source version **0.6.1** adds `Image.ReadMetadata`, `Image.EditMetadata` and typed `ExifProfile` / `ExifTag<T>`.
JPEG, PNG/APNG and WebP edits preserve compressed image data byte for byte and replace output files atomically.
This version is prepared locally; the latest published package remains 0.6.0. See the
[0.6.1 notes](https://github.com/art-ix/PictureSharp/blob/main/docs/release-0.6.1.md) and
[metadata guide](https://github.com/art-ix/PictureSharp/blob/main/docs/metadata.md).

## Documentation

The library is not yet stable: public API may still change between 0.x releases. See also the [architecture](https://github.com/art-ix/PictureSharp/blob/main/docs/architecture.md).

- [Getting started](https://github.com/art-ix/PictureSharp/blob/main/docs/getting-started.md): installation, the `Load` → `Mutate`/`Clone` → `Save` model, disposal and errors
- [Loading and saving](https://github.com/art-ix/PictureSharp/blob/main/docs/loading-and-saving.md): files, streams, memory, async, format detection, thumbnails while loading, web responses
- [Resource limits and cancellation](https://github.com/art-ix/PictureSharp/blob/main/docs/limits-and-cancellation.md): `MaxInputBytes`, `MaxPixels`, `MaxFrames`, `MaxMetadataBytes` and cancellation tokens
- [ICO icons](https://github.com/art-ix/PictureSharp/blob/main/docs/ico.md): selecting variants and creating favicons or multi-size icons
- [Encoder options](https://github.com/art-ix/PictureSharp/blob/main/docs/encoder-options.md): WebP, PNG, JPEG, BMP, GIF and PCX settings with recipes
- [Image processing](https://github.com/art-ix/PictureSharp/blob/main/docs/image-processing.md): resize modes, geometry, color, filters, compositing, custom operations
- [Pixels and interop](https://github.com/art-ix/PictureSharp/blob/main/docs/pixels-and-interop.md): pixel access, pixel formats, WPF/SkiaSharp/WinForms, frame pixel access and conversion
- [Animation](https://github.com/art-ix/PictureSharp/blob/main/docs/animation.md): frames, durations, animated WebP, APNG and GIF, GIF ↔ WebP conversion
- [Metadata](https://github.com/art-ix/PictureSharp/blob/main/docs/metadata.md): resolution, typed EXIF, ICC/XMP and metadata editing without recompression
- [Extending PictureSharp](https://github.com/art-ix/PictureSharp/blob/main/docs/extending-picturesharp.md): format plugins, `ImageConfiguration`, custom frame processors and shortcuts
- [Command-line sample](https://github.com/art-ix/PictureSharp/blob/main/docs/cli.md): `info`, `convert`, `resize`, `thumbnail`, `decode` and `icon`
- [Development](https://github.com/art-ix/PictureSharp/blob/main/docs/development.md): building, tests, reference tools and oracles
- [Performance](https://github.com/art-ix/PictureSharp/blob/main/docs/performance.md): benchmark matrix, comparison with libwebp, memory use, benchmark commands and limitations

## Building from source

```bash
dotnet build -c Release
dotnet test -c Release
```

Reference `src/PictureSharp/PictureSharp.csproj` from your application to use a source checkout. Implementation phases and their completion criteria are in [plan.md](https://github.com/art-ix/PictureSharp/blob/main/plan.md); the library is in development (source version **0.6.1**, latest published release **0.6.0**). See the [0.6.1 notes](https://github.com/art-ix/PictureSharp/blob/main/docs/release-0.6.1.md).

## License

PictureSharp is licensed under [MIT](https://github.com/art-ix/PictureSharp/blob/main/LICENSE). It includes ports from libwebp, libjpeg-turbo, zlib and Pillow, and is based in part on the work of the Independent JPEG Group. Original license and copyright notices are retained in [THIRD-PARTY-NOTICES.txt](https://github.com/art-ix/PictureSharp/blob/main/THIRD-PARTY-NOTICES.txt).
