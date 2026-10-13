# RasterSharp

**A pure managed image processing library for .NET. No native dependencies.**

RasterSharp reads and writes WebP, PNG, JPEG, BMP and GIF and processes images in 100% C# for **.NET 10 and later**: no NuGet dependencies, native libraries or P/Invoke. The API follows ImageSharp (`Image.Load`, `Mutate`, `Clone`, `SaveAs*`), and its results are verified pixel for pixel against libwebp, libjpeg-turbo and Pillow.

- WebP lossy, lossless, alpha and animation (encoder byte-compatible with `cwebp` 1.6.0 where possible)
- PNG (including APNG and Adam7), JPEG (baseline and progressive), BMP
- GIF with animation, and GIF ↔ WebP conversion compatible with `gif2webp` 1.6.0
- Resize, crop, pad, rotate, flip, EXIF auto-orientation
- Brightness, contrast, saturation, blur, sharpen, compositing and watermarks
- Raw pixel access and conversion to BGRA/RGB/gray buffers for interop
- ICC, EXIF and XMP metadata preserved across formats

| Format | Read | Write |
| --- | --- | --- |
| WebP | ✔ lossy, lossless, alpha, animation | ✔ lossy, lossless, near-lossless, alpha, animation |
| PNG | ✔ all color types and bit depths, APNG | ✔ gray, RGB(A), palette, Adam7, APNG |
| JPEG | ✔ baseline, progressive, CMYK/YCCK | ✔ baseline, progressive, 4:4:4–4:2:0 |
| BMP | ✔ palette, RLE, bit fields, alpha, embedded PNG/JPEG | ✔ 1/4/8/24/32-bit |
| GIF | ✔ GIF87a/89a, interlace, transparency, animation | ✔ exact or quantized palettes (Pillow median cut/octree), dithering, animation |

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

`Mutate` changes the image it is called on; `Clone` returns a new image and leaves the source untouched.

## Documentation

- [Getting started](docs/getting-started.md): installation, the `Load` → `Mutate`/`Clone` → `Save` model, disposal and errors
- [Loading and saving](docs/loading-and-saving.md): files, streams, memory, async, format detection, web responses
- [Encoder options](docs/encoder-options.md): WebP, PNG, JPEG, BMP and GIF settings with recipes
- [Image processing](docs/image-processing.md): resize modes, geometry, color, filters, compositing, custom operations
- [Pixels and interop](docs/pixels-and-interop.md): pixel access, pixel formats, WPF/SkiaSharp/WinForms, the `WebP` one-call API
- [Animation](docs/animation.md): frames, durations, animated WebP, APNG and GIF, GIF ↔ WebP conversion
- [Metadata](docs/metadata.md): resolution, ICC/EXIF/XMP and orientation
- [Command-line sample](docs/cli.md): `info`, `convert`, `resize` and `decode`
- [Development](docs/development.md): building, tests, reference tools and oracles

## Building from source

```bash
dotnet build -c Release
dotnet test -c Release
```

Reference `src/RasterSharp/RasterSharp.csproj` from your application to use a source checkout. Implementation phases and their completion criteria are in [plan.md](plan.md); the library is in development (current release 0.2.0), with performance work next.

## License

RasterSharp is licensed under [MIT](LICENSE). It includes ports from libwebp, libjpeg-turbo, zlib and Pillow, and is based in part on the work of the Independent JPEG Group. Original license and copyright notices are retained in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
