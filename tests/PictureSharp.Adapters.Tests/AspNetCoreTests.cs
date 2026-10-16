using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using PictureSharp.AspNetCore;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using MediaType = Microsoft.Net.Http.Headers.MediaTypeHeaderValue;

namespace PictureSharp.Adapters.Tests;

public sealed class AspNetCoreTests
{
    private static readonly ImageFormat[] Offered = [ImageFormat.WebP, ImageFormat.Png, ImageFormat.Jpeg];

    public static IEnumerable<(string Accept, string Expected)> AcceptCases() =>
    [
        ("", "image/webp"),
        ("*/*", "image/webp"),
        ("image/*", "image/webp"),
        ("image/png", "image/png"),
        ("IMAGE/JPEG", "image/jpeg"),
        ("image/png;q=0.9, image/webp;q=0.5", "image/png"),
        ("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image/webp"),
        ("image/webp;q=0, image/*;q=0.5", "image/png"),
        ("image/*;q=0.2, image/jpeg", "image/jpeg"),
        ("*/*;q=0.1, image/png;q=0", "image/webp"),
        ("text/html, image/webp;q=0.01", "image/webp"),
        ("text/html", ""),
        ("image/*;q=0", ""),
    ];

    [Test]
    [MethodDataSource(nameof(AcceptCases))]
    public async Task Negotiation_UsesTheMostSpecificRange_ThenServerOrder(string accept, string expected)
    {
        IList<MediaType> ranges = MediaType.TryParseList(accept.Length == 0 ? [] : [accept], out IList<MediaType>? parsed) ? parsed : [];
        ImageFormat? format = AcceptNegotiation.Select(Offered, ranges);
        await Assert.That(format?.MimeType ?? "").IsEqualTo(expected);
    }

    [Test]
    public async Task Server_NegotiatesFormat_AndWritesHeaders()
    {
        await using var app = await StartAsync(new ImageResultOptions { CacheControl = "public, max-age=60" });
        HttpClient client = app.GetTestClient();
        using Image expected = TestImages.Create(13, 4);

        using HttpResponseMessage webp = await Get(client, "/image", "image/avif,image/webp,*/*;q=0.8");
        await Assert.That(webp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(webp.Content.Headers.ContentType?.MediaType).IsEqualTo("image/webp");
        byte[] webpBody = await webp.Content.ReadAsByteArrayAsync();
        await Assert.That(webp.Content.Headers.ContentLength).IsEqualTo(webpBody.LongLength);
        await Assert.That(webp.Headers.Vary).Contains("Accept");
        await Assert.That(webp.Headers.CacheControl?.ToString()).IsEqualTo("public, max-age=60");
        await Assert.That(Image.Identify(webpBody).Format).IsEqualTo(ImageFormat.WebP);

        using HttpResponseMessage png = await Get(client, "/image", "image/png");
        await Assert.That(png.Content.Headers.ContentType?.MediaType).IsEqualTo("image/png");
        using Image decoded = Image.Load(await png.Content.ReadAsByteArrayAsync());
        await Assert.That(TestImages.Pixels(decoded)).IsEquivalentTo(TestImages.Pixels(expected), CollectionOrdering.Matching);

        using HttpResponseMessage jpeg = await Get(client, "/image", "image/jpeg");
        await Assert.That(Image.Identify(await jpeg.Content.ReadAsByteArrayAsync()).Format).IsEqualTo(ImageFormat.Jpeg);

        using HttpResponseMessage fallback = await Get(client, "/image", "text/html");
        await Assert.That(fallback.Content.Headers.ContentType?.MediaType).IsEqualTo("image/webp");
    }

    [Test]
    public async Task Server_AppliesEncoderOptions_AndHead_SendsOnlyHeaders()
    {
        await using var app = await StartAsync(new ImageResultOptions { Encoders = [new WebPEncoderOptions { Lossless = true, Exact = true }] });
        HttpClient client = app.GetTestClient();
        using Image expected = TestImages.Create(13, 4);
        using HttpResponseMessage webp = await Get(client, "/image", "image/webp");
        byte[] body = await webp.Content.ReadAsByteArrayAsync();
        using Image decoded = Image.Load(body);
        await Assert.That(TestImages.Pixels(decoded)).IsEquivalentTo(TestImages.Pixels(expected), CollectionOrdering.Matching);

        using var head = new HttpRequestMessage(HttpMethod.Head, "/image");
        head.Headers.Accept.ParseAdd("image/webp");
        using HttpResponseMessage headResponse = await client.SendAsync(head);
        await Assert.That(headResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(headResponse.Content.Headers.ContentLength).IsEqualTo(body.LongLength);
        await Assert.That((await headResponse.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Server_RespondsNotAcceptable_WhenConfigured_AndSingleFormatHasNoVary()
    {
        await using var app = await StartAsync(new ImageResultOptions { Formats = [ImageFormat.Png], RespondNotAcceptable = true });
        HttpClient client = app.GetTestClient();
        using HttpResponseMessage rejected = await Get(client, "/image", "image/webp");
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.NotAcceptable);
        await Assert.That((await rejected.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        using HttpResponseMessage png = await Get(client, "/image", "image/*");
        await Assert.That(png.Content.Headers.ContentType?.MediaType).IsEqualTo("image/png");
        await Assert.That(png.Headers.Vary).IsEmpty();
    }

    [Test]
    public async Task Result_DisposesTheImageAfterWriting_UnlessAskedNotTo()
    {
        var owned = TestImages.Create(3, 3);
        var context = NewContext("image/png");
        await new ImageResult(owned).ExecuteAsync(context);
        await Assert.That(() => owned.Width).Throws<ObjectDisposedException>();
        await Assert.That(context.Response.Body.Length).IsGreaterThan(0);

        var withVary = NewContext("image/png");
        withVary.Response.Headers.Vary = "Accept-Encoding, accept";
        await new ImageResult(TestImages.Create(3, 3)).ExecuteAsync(withVary);
        await Assert.That(withVary.Response.Headers.Vary.ToString()).IsEqualTo("Accept-Encoding, accept");
        var otherVary = NewContext("image/png");
        otherVary.Response.Headers.Vary = "Accept-Encoding";
        await new ImageResult(TestImages.Create(3, 3)).ExecuteAsync(otherVary);
        await Assert.That(otherVary.Response.Headers.Vary.ToString()).IsEqualTo("Accept-Encoding,Accept");

        using var kept = TestImages.Create(3, 3);
        await ((IResult)Results.Extensions.Image(kept, disposeImage: false)).ExecuteAsync(NewContext("image/png"));
        await Assert.That(kept.Width).IsEqualTo(3);
    }

    [Test]
    public async Task Result_CanceledRequest_WritesNothing_AndDisposesTheImage()
    {
        var image = TestImages.Create(3, 3);
        var context = NewContext("image/png");
        context.RequestAborted = new CancellationToken(canceled: true);
        await Assert.That(() => new ImageResult(image).ExecuteAsync(context)).Throws<OperationCanceledException>();
        await Assert.That(context.Response.Body.Length).IsEqualTo(0);
        await Assert.That(context.Response.Headers.ContentType.Count).IsEqualTo(0);
        await Assert.That(() => image.Width).Throws<ObjectDisposedException>();
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task Result_RequestAbortedDuringEncoding_StopsTheEncoder()
    {
        var image = new Image(3000, 3000);
        var random = new Random(3);
        random.NextBytes(image.GetPixelByteSpan());
        var context = NewContext("image/png");
        using var aborted = new CancellationTokenSource();
        context.RequestAborted = aborted.Token;
        var options = new ImageResultOptions { Formats = [ImageFormat.Png], Encoders = [new PngEncoderOptions { CompressionLevel = 9 }] };
        // Encoding runs synchronously inside ExecuteAsync, as on a server request thread; the abort comes from elsewhere.
        aborted.CancelAfter(TimeSpan.FromMilliseconds(20));
        Task execution = Task.Run(() => new ImageResult(image, options).ExecuteAsync(context));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.That(() => execution).Throws<OperationCanceledException>();
        await Assert.That(context.Response.Body.Length).IsEqualTo(0);
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Upload_IsDecoded_WithinTheLimits()
    {
        await using var app = await StartAsync(ImageResultOptions.Default);
        HttpClient client = app.GetTestClient();
        using Image image = TestImages.Create(7, 5);
        byte[] png = image.ToPng();

        using HttpResponseMessage ok = await client.PostAsync("/upload?limit=100000", new ByteArrayContent(png));
        await Assert.That(await ok.Content.ReadAsStringAsync()).IsEqualTo("7x5");

        int limit = png.Length - 1;
        using HttpResponseMessage declared = await client.PostAsync($"/upload?limit={limit}", new ByteArrayContent(png));
        await Assert.That(declared.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);

        using HttpResponseMessage streamed = await client.PostAsync($"/upload?limit={limit}", new UnknownLengthContent(png));
        await Assert.That(streamed.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);

        using HttpResponseMessage exact = await client.PostAsync($"/upload?limit={png.Length}", new UnknownLengthContent(png));
        await Assert.That(await exact.Content.ReadAsStringAsync()).IsEqualTo("7x5");

        using HttpResponseMessage corrupt = await client.PostAsync("/upload?limit=100000", new ByteArrayContent([1, 2, 3, 4, 5, 6, 7, 8]));
        await Assert.That(corrupt.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using HttpResponseMessage pixels = await client.PostAsync("/upload?limit=100000&maxPixels=34", new ByteArrayContent(png));
        await Assert.That(pixels.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Upload_StopsWhenTheRequestIsAborted()
    {
        using Image image = TestImages.Create(7, 5);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(image.ToPng());
        context.RequestAborted = new CancellationToken(canceled: true);
        await Assert.That(() => (Task)context.Request.ReadImageAsync()).Throws<OperationCanceledException>();
        context.RequestAborted = CancellationToken.None;
        await Assert.That(() => (Task)context.Request.ReadImageAsync(null, new CancellationToken(canceled: true))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Options_RejectInvalidFormatLists()
    {
        await Assert.That(() => new ImageResultOptions { Formats = [] }).Throws<ArgumentException>();
        await Assert.That(() => new ImageResultOptions { Formats = [ImageFormat.Png, null!] }).Throws<ArgumentNullException>();
        await Assert.That(() => new ImageResultOptions { Encoders = [null!] }).Throws<ArgumentNullException>();
        ImageFormat decodeOnly = ImageFormat.Create("Test", "image/x-test", ".tst");
        await Assert.That(() => new ImageResultOptions { Formats = [decodeOnly] }).Throws<ArgumentException>();
        await Assert.That(() => new ImageResult(null!)).Throws<ArgumentNullException>();
    }

    private static async Task<WebApplication> StartAsync(ImageResultOptions options)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.MapMethods("/image", ["GET", "HEAD"], () => Results.Extensions.Image(TestImages.Create(13, 4), options));
        app.MapPost("/upload", async (HttpRequest request, long limit, long? maxPixels) =>
        {
            try
            {
                var decoder = new DecoderOptions { MaxInputBytes = limit, MaxPixels = maxPixels ?? DecoderOptions.Default.MaxPixels };
                using Image image = await request.ReadImageAsync(decoder);
                return Results.Text($"{image.Width}x{image.Height}");
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
        return app;
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string path, string accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, accept);
        return await client.SendAsync(request);
    }

    private static DefaultHttpContext NewContext(string accept)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Accept = accept;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>A body without Content-Length, so the limit is enforced while reading.</summary>
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
