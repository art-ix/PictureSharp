using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using PictureSharp.AspNetCore;
using PictureSharp.Drawing;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Skia;
using SkiaSharp;
using Bitmap = System.Drawing.Bitmap;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PictureSharp.Adapters.Tests;

/// <summary>The samples of docs/adapters.md and the adapter READMEs, kept compiling and running.</summary>
public sealed class AdapterDocsExamplesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"picturesharp-adapter-docs-{Guid.NewGuid():N}");

    public AdapterDocsExamplesTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "images"));
        using Image sample = TestImages.Create(64, 48);
        sample.SaveAsPng(P("logo.png"));
        sample.SaveAsPng(P("photo.png"));
        sample.SaveAsPng(Path.Combine(_dir, "images", "cat.png"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    [Test]
    public async Task Skia()
    {
        using Image image = Image.Load(P("photo.png"));
        using SKBitmap bitmap = image.ToSKBitmap();
        using SKImage premultiplied = image.ToSKImage(SKColorType.Bgra8888, SKAlphaType.Premul);
        using Image fromSkia = Image.FromSKBitmap(bitmap);
        await Assert.That(TestImages.Pixels(fromSkia)).IsEquivalentTo(TestImages.Pixels(image), CollectionOrdering.Matching);
        await Assert.That(premultiplied.Width).IsEqualTo(64);
    }

    [Test]
    public async Task Drawing()
    {
        Skip.Unless(OperatingSystem.IsWindows(), "GDI+ (System.Drawing.Common) is supported on Windows only.");
        if (OperatingSystem.IsWindows()) await DrawingOnWindows();
    }

    [Test]
    public async Task AspNetCore()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = _dir });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using WebApplication app = builder.Build();
        string root = _dir;

        var thumbnails = new ImageResultOptions
        {
            Formats = [ImageFormat.WebP, ImageFormat.Png, ImageFormat.Jpeg],
            Encoders = [new WebPEncoderOptions { Quality = 80 }],
            CacheControl = "public, max-age=86400",
        };

        app.MapGet("/thumb/{name}", async (string name, CancellationToken ct) =>
        {
            var options = new DecoderOptions { TargetSize = new(40, 0), ApplyExifOrientation = true };
            Image image = await Image.LoadAsync(Path.Combine(root, "images", Path.GetFileName(name)), options, ct);
            return Results.Extensions.Image(image, thumbnails);
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
                return Results.StatusCode(e.StatusCode);
            }
            catch (ImageFormatException)
            {
                return Results.BadRequest();
            }
        });

        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        using HttpResponseMessage thumbnail = await client.GetAsync("/thumb/cat.png");
        await Assert.That(thumbnail.Content.Headers.ContentType?.MediaType).IsEqualTo("image/webp");
        await Assert.That(Image.Identify(await thumbnail.Content.ReadAsByteArrayAsync()).Width).IsEqualTo(40);
        using HttpResponseMessage upload = await client.PostAsync("/upload", new ByteArrayContent(await File.ReadAllBytesAsync(P("logo.png"))));
        await Assert.That(upload.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await upload.Content.ReadAsStringAsync()).IsEqualTo("""{"width":64,"height":48}""");
    }

    [SupportedOSPlatform("windows")]
    private async Task DrawingOnWindows()
    {
        using Image image = Image.Load(P("logo.png"));
        using Bitmap bitmap = image.ToBitmap();
        using Bitmap fast = image.ToBitmap(GdiPixelFormat.Format32bppPArgb);
        using Image back = Image.FromBitmap(bitmap);
        await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(TestImages.Pixels(image), CollectionOrdering.Matching);
        await Assert.That(fast.PixelFormat).IsEqualTo(GdiPixelFormat.Format32bppPArgb);
    }
}
