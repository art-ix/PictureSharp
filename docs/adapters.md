# Adapters: System.Drawing, SkiaSharp and ASP.NET Core

`PictureSharp` stays dependency-free; integrations ship as separate packages of the same version, each depending only
on `PictureSharp` and its target API:

| Package | Depends on | Provides |
| --- | --- | --- |
| `PictureSharp.Drawing` | `System.Drawing.Common` (Windows) | `image.ToBitmap()`, `Image.FromBitmap(bitmap)` |
| `PictureSharp.Skia` | `SkiaSharp` | `image.ToSKBitmap()`, `image.ToSKImage()`, `Image.FromSKBitmap/FromSKImage/FromSKPixmap` |
| `PictureSharp.AspNetCore` | ASP.NET Core shared framework | `Results.Extensions.Image(image)`, `ImageResult`, `request.ReadImageAsync()` |

```bash
dotnet add package PictureSharp.Skia
```

Every conversion copies; the result belongs to the caller and the source stays unchanged. Conversions from `Image` read
the first frame (use `image.Frames[i]` for others). Colors are not converted: run `Mutate(x => x.ConvertToSrgb())`
first for images with another ICC profile.

## System.Drawing (Windows)

```csharp
using PictureSharp;
using PictureSharp.Drawing;
using Bitmap = System.Drawing.Bitmap;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

using Image image = Image.Load("logo.png");
using Bitmap bitmap = image.ToBitmap();                                   // Format32bppArgb, straight alpha
using Bitmap fast = image.ToBitmap(GdiPixelFormat.Format32bppPArgb);      // premultiplied, faster GDI+ drawing
using Image back = Image.FromBitmap(bitmap);
```

| `Bitmap` format | `ToBitmap` | `FromBitmap` |
| --- | --- | --- |
| `Format32bppArgb` (default) | exact | exact |
| `Format32bppPArgb` | premultiplied | un-premultiplied by PictureSharp |
| `Format24bppRgb`, `Format32bppRgb` | alpha dropped without compositing | opaque, `HasAlpha` false |
| other (indexed, 16-, 48-, 64-bit) | `ArgumentOutOfRangeException` | converted to 32-bit ARGB by GDI+ |

Padding, bottom-up strides and resolution are handled; `FromBitmap` reads the active frame. The types are Windows-only
(`[SupportedOSPlatform("windows")]`). Because `System.Drawing.Image` clashes with `PictureSharp.Image`, alias the types
you need instead of importing `System.Drawing`.

## SkiaSharp

```csharp
using PictureSharp;
using PictureSharp.Skia;
using SkiaSharp;

using Image image = Image.Load("photo.png");
using SKBitmap bitmap = image.ToSKBitmap();                                  // Rgba8888, Unpremul: exact
using SKImage premultiplied = image.ToSKImage(SKColorType.Bgra8888, SKAlphaType.Premul);
using Image fromSkia = Image.FromSKBitmap(bitmap);                           // any color and alpha type
```

`ToSKBitmap` and `ToSKImage` accept `Rgba8888` or `Bgra8888` with `Unpremul` (bit-exact) or `Premul`; other combinations
throw `ArgumentOutOfRangeException`. The `From*` methods accept every color type Skia can convert and produce straight
alpha. A texture-backed `SKImage` must be read through its GPU context first. Install the native assets for your
platform, e.g. `SkiaSharp.NativeAssets.Linux`.

## ASP.NET Core

```csharp
using PictureSharp;
using PictureSharp.AspNetCore;
using PictureSharp.Formats.WebP;

var thumbnails = new ImageResultOptions
{
    Formats = [ImageFormat.WebP, ImageFormat.Png, ImageFormat.Jpeg],   // server preference
    Encoders = [new WebPEncoderOptions { Quality = 80 }],
    CacheControl = "public, max-age=86400",
};

app.MapGet("/thumb/{name}", async (string name, CancellationToken ct) =>
{
    var options = new DecoderOptions { TargetSize = new(400, 0), ApplyExifOrientation = true };
    Image image = await Image.LoadAsync(Path.Combine("images", Path.GetFileName(name)), options, ct);
    return Results.Extensions.Image(image, thumbnails);                 // disposed after the response
});

app.MapPost("/upload", async (HttpRequest request) =>
{
    try
    {
        using Image image = await request.ReadImageAsync(new DecoderOptions { MaxInputBytes = 10_000_000, MaxPixels = 50_000_000 });
        return Results.Ok(new { image.Width, image.Height });
    }
    catch (BadHttpRequestException e)
    {
        return Results.StatusCode(e.StatusCode);                        // 413 above MaxInputBytes
    }
    catch (ImageFormatException)
    {
        return Results.BadRequest();                                    // corrupt, unrecognized or over MaxPixels
    }
});
```

`ImageResult` picks the format from `Accept`: each offered format takes the quality of its most specific matching range,
the highest quality wins and ties keep the server order. Without a usable `Accept`, the first format is sent (or `406`
with `RespondNotAcceptable = true`). JPEG has no alpha, so do not prefer it for transparent images.

The image is encoded into memory first, so the response has `Content-Type`, `Content-Length`, `Vary: Accept` and the
configured `Cache-Control`, and a failed or canceled encoding sends nothing partial. `HEAD` gets headers only, and work
stops on `RequestAborted`. The image is disposed after the response unless `disposeImage: false` is passed.

`request.ReadImageAsync` decodes the body with the given `DecoderOptions`. A body over `MaxInputBytes` throws
`BadHttpRequestException` (413) before buffering; other failures behave as in `Image.LoadAsync`. Server limits such as
Kestrel's `MaxRequestBodySize` apply first.
