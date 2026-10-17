# PictureSharp

**A pure managed image processing library for .NET. No native dependencies.**

PictureSharp reads, writes and processes JPEG, PNG, WebP, GIF, BMP, ICO and PCX in 100% C# for **.NET 10 and later**,
with no NuGet dependencies, native libraries or P/Invoke. Output is verified pixel for pixel against libwebp,
libjpeg-turbo and Pillow.

| Format | Best for | Transparency | Animation | HDR gain maps |
| --- | --- | :---: | :---: | :---: |
| JPEG | photos | | | read, write |
| PNG | graphics, screenshots | ✔ | ✔ APNG | |
| WebP | the web, lossy or lossless | ✔ | ✔ | |
| GIF | simple animations | ✔ | ✔ | |
| BMP | Windows bitmaps | ✔ | | |
| ICO | favicons, app icons | ✔ | | |
| PCX | legacy files | ✔ | | |
| HEIC ¹ | iPhone photos | ✔ | | read |

Every format is both read and written. Variants and options for each one are listed in
[Loading and saving](https://github.com/art-ix/PictureSharp/blob/main/docs/loading-and-saving.md#supported-formats).

¹ With the [PictureSharp.Heif](https://github.com/art-ix/PictureSharp.Heif) plugin package, also 100% C#:
`dotnet add package PictureSharp.Heif`.

- Resizing, geometry, color adjustments, filters, compositing and EXIF auto-orientation
- Levels, gamma, auto contrast, histograms, trimming, alpha masks and W3C blend modes
- Conversion of ICC and PNG color descriptions to sRGB, linear-light resize and compositing
- Fast JPEG thumbnails decoded at 1/2, 1/4 or 1/8 scale
- Frame-by-frame reading and writing of GIF, APNG and WebP animations
- Typed EXIF and ICC/XMP metadata, editable without recompressing the image
- HDR photos with gain maps (ISO 21496-1, Apple): reconstruction, tone mapping, editing and saving as JPEG
- Limits for untrusted input and cancellation of all long-running work
- SIMD on x64 and ARM64, bit-identical to the scalar code
- Extensible with format plugins (including HDR gain maps and metadata) and custom processors
- Optional adapter packages for System.Drawing, SkiaSharp and ASP.NET Core

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

`Mutate` edits the image; `Clone` returns an edited copy.

## Documentation

The public API may still change between 0.x releases.

- [Getting started](https://github.com/art-ix/PictureSharp/blob/main/docs/getting-started.md)
- [Loading and saving](https://github.com/art-ix/PictureSharp/blob/main/docs/loading-and-saving.md) ·
  [Limits and cancellation](https://github.com/art-ix/PictureSharp/blob/main/docs/limits-and-cancellation.md)
- [Image processing](https://github.com/art-ix/PictureSharp/blob/main/docs/image-processing.md) ·
  [Encoder options](https://github.com/art-ix/PictureSharp/blob/main/docs/encoder-options.md) ·
  [Pixels and interop](https://github.com/art-ix/PictureSharp/blob/main/docs/pixels-and-interop.md)
- [Animation](https://github.com/art-ix/PictureSharp/blob/main/docs/animation.md) ·
  [Metadata](https://github.com/art-ix/PictureSharp/blob/main/docs/metadata.md) ·
  [Color management](https://github.com/art-ix/PictureSharp/blob/main/docs/color-management.md) ·
  [HDR photos](https://github.com/art-ix/PictureSharp/blob/main/docs/hdr.md) ·
  [ICO icons](https://github.com/art-ix/PictureSharp/blob/main/docs/ico.md)
- [Adapters: System.Drawing, SkiaSharp, ASP.NET Core](https://github.com/art-ix/PictureSharp/blob/main/docs/adapters.md)
- [Extending PictureSharp](https://github.com/art-ix/PictureSharp/blob/main/docs/extending-picturesharp.md) ·
  [Architecture](https://github.com/art-ix/PictureSharp/blob/main/docs/architecture.md) ·
  [Performance](https://github.com/art-ix/PictureSharp/blob/main/docs/performance.md)
- [Command-line sample](https://github.com/art-ix/PictureSharp/blob/main/docs/cli.md) ·
  [Migrating from 0.5.0](https://github.com/art-ix/PictureSharp/blob/main/docs/migration-from-0.5.md) ·
  [Development](https://github.com/art-ix/PictureSharp/blob/main/docs/development.md) ·
  [Release notes](https://github.com/art-ix/PictureSharp/tree/main/docs/releases)

## Building from source

```bash
dotnet build -c Release
dotnet test -c Release
```

## License

PictureSharp is licensed under [MIT](https://github.com/art-ix/PictureSharp/blob/main/LICENSE). It includes ports from
libwebp, libjpeg-turbo, zlib and Pillow, and is based in part on the work of the Independent JPEG Group; see
[THIRD-PARTY-NOTICES.txt](https://github.com/art-ix/PictureSharp/blob/main/THIRD-PARTY-NOTICES.txt).
