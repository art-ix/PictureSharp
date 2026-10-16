using Microsoft.AspNetCore.Http;
using PictureSharp;
using PictureSharp.AspNetCore;
using PictureSharp.Drawing;
using PictureSharp.Skia;
using SkiaSharp;

using var source = new Image(7, 5, new Rgba32(20, 80, 160, 128));
source[0, 0] = Rgba32.Transparent;
byte[] expected = source.ToPixelData();

// SkiaSharp: straight and premultiplied copies in both channel orders.
foreach (SKColorType colorType in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
{
    using SKBitmap bitmap = source.ToSKBitmap(colorType);
    using Image fromBitmap = Image.FromSKBitmap(bitmap);
    using SKImage skImage = source.ToSKImage(colorType, SKAlphaType.Premul);
    using Image fromImage = Image.FromSKImage(skImage);
    if (!fromBitmap.ToPixelData().AsSpan().SequenceEqual(expected) || fromImage.Size != source.Size)
        throw new InvalidOperationException($"SkiaSharp round trip failed: {colorType}.");
}

// System.Drawing: GDI+ exists only on Windows; elsewhere the adapter must still compile, trim and link.
if (OperatingSystem.IsWindows())
{
    using System.Drawing.Bitmap gdi = source.ToBitmap();
    using Image fromGdi = Image.FromBitmap(gdi);
    if (!fromGdi.ToPixelData().AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("System.Drawing round trip failed.");
}

// ASP.NET Core: negotiation, headers and upload limits without a server.
var context = new DefaultHttpContext();
context.Request.Headers.Accept = "image/png;q=0.9, image/webp;q=0.5";
context.Response.Body = new MemoryStream();
await Results.Extensions.Image(source.Clone(), new ImageResultOptions { CacheControl = "no-store" }).ExecuteAsync(context);
if (context.Response.ContentType != "image/png" || context.Response.Headers.Vary != "Accept" || context.Response.ContentLength != context.Response.Body.Length)
    throw new InvalidOperationException("ImageResult headers failed.");
var upload = new DefaultHttpContext();
upload.Request.Body = new MemoryStream(((MemoryStream)context.Response.Body).ToArray());
using (Image uploaded = await upload.Request.ReadImageAsync())
{
    if (!uploaded.ToPixelData().AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("ReadImageAsync failed.");
}

upload.Request.Body.Position = 0;
try
{
    using Image tooLarge = await upload.Request.ReadImageAsync(new DecoderOptions { MaxInputBytes = 16 });
    throw new InvalidOperationException("The upload limit was not enforced.");
}
catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
{
}

Console.WriteLine("PictureSharp adapters smoke test passed: SkiaSharp, System.Drawing (Windows only), ASP.NET Core.");
