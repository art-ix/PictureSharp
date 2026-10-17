# Adapters: System.Drawing, SkiaSharp and ASP.NET Core

The `PictureSharp` package stays dependency-free. Integration with other libraries ships as separate packages with the
same version, each depending only on `PictureSharp` and its target API:

| Package | Depends on | Provides |
| --- | --- | --- |
| `PictureSharp.Drawing` | `System.Drawing.Common` (Windows) | `image.ToBitmap()`, `Image.FromBitmap(bitmap)` |
| `PictureSharp.Skia` | `SkiaSharp` | `image.ToSKBitmap()`, `image.ToSKImage()`, `Image.FromSKBitmap/FromSKImage/FromSKPixmap` |
| `PictureSharp.AspNetCore` | ASP.NET Core shared framework | `Results.Extensions.Image(image)`, `ImageResult`, `request.ReadImageAsync()` |

```bash
dotnet add package PictureSharp.Skia
```

## Ownership

Every pixel conversion copies. The returned `Bitmap`, `SKBitmap`, `SKImage` or `Image` belongs to the caller, who
disposes it; the source stays unchanged and can be disposed independently. Conversions from `Image` read its first
frame; call the same methods on `image.Frames[i]` for another frame. Pixels are copied without color-space conversion:
run `Mutate(x => x.ConvertToSrgb())` first when the image has another ICC profile.

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

Row padding and bottom-up (negative) strides are handled. The resolution is copied in both directions. `FromBitmap`
reads the active frame of a multi-frame bitmap. The types are marked `[SupportedOSPlatform("windows")]`, as
`System.Drawing.Common` is supported on Windows only. `System.Drawing.Image` and `PictureSharp.Image` share a name;
alias the types you need instead of importing `System.Drawing`.

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

`ToSKBitmap` and `ToSKImage` accept `Rgba8888` or `Bgra8888` with `Unpremul` (bit-exact round trip) or `Premul`
(PictureSharp's premultiplication); other combinations throw `ArgumentOutOfRangeException`. `FromSKBitmap`,
`FromSKPixmap` and `FromSKImage` accept every color type Skia can convert (for example `Gray8` or `RgbaF16`) and always
produce straight alpha; an `Opaque` source gives alpha 255. A texture-backed `SKImage` must be read through its GPU
context first. Install the native assets your platform needs, for example `SkiaSharp.NativeAssets.Linux` on Linux
servers.

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

`ImageResult` chooses the format from the `Accept` header: each offered format takes the quality of its most specific
matching range (`image/webp` before `image/*` before `*/*`), the highest quality wins and ties keep the server order.
Without an `Accept` header, or with `*/*`, the first format is sent. When `Accept` excludes every offered format, the
first format is sent too, or `406 Not Acceptable` with `RespondNotAcceptable = true`.

The image is encoded into memory before anything is written, so the response has `Content-Type`, `Content-Length`,
`Vary: Accept` (when more than one format is offered) and the configured `Cache-Control`, and a failed or canceled
encoding sends no partial image. A `HEAD` request gets the headers only. Encoding and writing stop when
`HttpContext.RequestAborted` is canceled. `Results.Extensions.Image(image)` disposes the image after the response; pass
`disposeImage: false` to keep it, for example for a cached image. JPEG has no alpha: put a transparent image's formats
in an order that does not prefer JPEG.

`request.ReadImageAsync` decodes the request body with the given `DecoderOptions` and stops when the request is aborted.
A body over `MaxInputBytes`, declared by `Content-Length` or found while reading, throws `BadHttpRequestException` with
status 413 before it is buffered. Other limits behave as in `Image.LoadAsync`: corrupt or unrecognized data and an
exceeded `MaxPixels` throw `ImageFormatException`, unsupported variants `NotSupportedException`. Server limits such as
Kestrel's `MaxRequestBodySize` still apply first.
