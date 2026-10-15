# RasterSharp

**A pure managed image processing library for .NET. No native dependencies.**

RasterSharp reads and writes WebP, PNG, JPEG, BMP, GIF, PCX and ICO and processes images in 100% C# for **.NET 10 and later**: no NuGet dependencies, native libraries or P/Invoke. The API follows ImageSharp (`Image.Load`, `Mutate`, `Clone`, `SaveAs*`), and its results are verified pixel for pixel against libwebp, libjpeg-turbo and Pillow.

- WebP lossy, lossless, alpha and animation (encoder byte-compatible with `cwebp` 1.6.0 where possible)
- PNG (including APNG and Adam7), JPEG (baseline and progressive), BMP
- GIF with animation, and GIF ↔ WebP conversion compatible with `gif2webp` 1.6.0
- ICO with PNG/DIB variants, alpha masks and multiple independent sizes
- PCX with palette and grayscale images, RGB and RGBA, and automatic output bit depth
- Resize, crop, pad, rotate, flip, EXIF auto-orientation
- Brightness, contrast, saturation, blur, sharpen, compositing and watermarks
- Raw pixel access and conversion to BGRA/RGB/gray buffers for interop
- SIMD on x64 (SSE2–AVX2) and ARM64 (AdvSimd/NEON), bit-identical to the scalar code
- ICC, EXIF and XMP metadata preserved where the output format supports them

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
using RasterSharp;

using var image = await Image.LoadAsync("photo.jpg");
image.Mutate(x => x
    .AutoOrient()
    .Resize(1600, 0)                 // 0 keeps the aspect ratio
    .UnsharpMask());
await image.SaveAsWebPAsync("photo.webp", new() { Quality = 82 });

using Image thumbnail = image.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
thumbnail.SaveAsPng("thumbnail.png");
thumbnail.SaveAsPcx("thumbnail.pcx");
```

`Mutate` changes the image it is called on; `Clone` returns a new image and leaves the source untouched.

## Documentation

- [Getting started](docs/getting-started.md): installation, the `Load` → `Mutate`/`Clone` → `Save` model, disposal and errors
- [Loading and saving](docs/loading-and-saving.md): files, streams, memory, async, format detection, web responses
- [ICO icons](docs/ico.md): selecting variants and creating favicons or multi-size icons
- [Encoder options](docs/encoder-options.md): WebP, PNG, JPEG, BMP, GIF and PCX settings with recipes
- [Image processing](docs/image-processing.md): resize modes, geometry, color, filters, compositing, custom operations
- [Pixels and interop](docs/pixels-and-interop.md): pixel access, pixel formats, WPF/SkiaSharp/WinForms, frame pixel access and conversion
- [Animation](docs/animation.md): frames, durations, animated WebP, APNG and GIF, GIF ↔ WebP conversion
- [Metadata](docs/metadata.md): resolution, ICC/EXIF/XMP and orientation
- [Command-line sample](docs/cli.md): `info`, `convert`, `resize`, `decode` and `icon`
- [Development](docs/development.md): building, tests, reference tools and oracles
- [Performance](docs/performance.md): benchmark matrix, comparison with libwebp, memory use, benchmark commands and limitations

## Building from source

```bash
dotnet build -c Release
dotnet test -c Release
```

Reference `src/RasterSharp/RasterSharp.csproj` from your application to use a source checkout. Implementation phases and their completion criteria are in [plan.md](plan.md); the library is in development (current release 0.5.1, which adds ICO reading and writing, variant selection, multi-size icon collections and the `icon` CLI command).

## License

RasterSharp is licensed under [MIT](LICENSE). It includes ports from libwebp, libjpeg-turbo, zlib and Pillow, and is based in part on the work of the Independent JPEG Group. Original license and copyright notices are retained in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
