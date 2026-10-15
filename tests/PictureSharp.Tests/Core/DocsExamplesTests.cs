using System.Drawing;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// The code samples from README.md and docs/*.md, kept compiling and running against corpus files.
/// Samples that need other libraries (WPF, SkiaSharp, ASP.NET Core) are covered by their PictureSharp part only.
/// </summary>
public sealed class DocsExamplesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"picturesharp-docs-{Guid.NewGuid():N}");

    public DocsExamplesTests()
    {
        Directory.CreateDirectory(_dir);
        File.Copy(Corpus.PathOf("jpeg/q90_444.jpg"), P("photo.jpg"));
        File.Copy(Corpus.PathOf("png/odd_rgba.png"), P("input.png"));
        File.Copy(Corpus.PathOf("png/odd_rgba.png"), P("logo.png"));
        File.Copy(Corpus.PathOf("png/odd_rgba.png"), P("frame-1.png"));
        File.Copy(Corpus.PathOf("webp/anim/alpha_lossless.webp"), P("animated.webp"));
        using (Image second = ImageReader.Load(P("frame-1.png")))
        {
            second.Mutate(x => x.Invert());
            ImageWriter.Save(second, P("frame-2.png"));
        }

        File.Copy(P("input.png"), P("unknown.bin"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Ico_IconsAndVariantOwnership()
    {
        using Image source = ImageReader.Load(P("logo.png"));
        using Image small = source.Clone(x => x.Resize(16, 16, ResizeMode.Pad));
        using Image large = source.Clone(x => x.Resize(256, 256, ResizeMode.Pad));
        small.SaveAsIco(P("favicon.ico"));
        IcoFile.Save([small, large], P("app.ico"));
        using Image preferred = ImageReader.Load(P("app.ico"), new() { Ico = new() { PreferredSize = new(32, 32) }});
        using Image indexed = ImageReader.Load(P("app.ico"), new() { Ico = new() { Index = 0 }});
        Assert.Equal(16, preferred.Width);
        Assert.Equal(16, indexed.Width);
        Assert.Equal(2, IcoFile.Identify(P("app.ico")).Count);
        using IcoImageCollection variants = IcoFile.LoadAll(P("app.ico"));
        await IcoFile.SaveAsync(variants, P("legacy.ico"), new() { Encoding = IcoEncoding.Dib });
        using IcoImageCollection legacy = IcoFile.LoadAll(P("legacy.ico"));
        Assert.Equal(large.ToPixelData(), legacy[1].ToPixelData());
    }

    // ------------------------------------------------------------ limits-and-cancellation.md

    [Fact]
    public void Limits_UntrustedUpload()
    {
        using Stream upload = File.OpenRead(P("photo.jpg"));
        using Stream output = File.Create(P("upload.webp"));
        var untrusted = new DecoderOptions
        {
            MaxInputBytes = 20 * 1024 * 1024,   // encoded bytes read from the file or stream
            MaxPixels = 40_000_000,             // width × height, summed over decoded frames
            MaxFrames = 500,                    // frames stored in an animation
            MaxMetadataBytes = 1024 * 1024,     // ICC + EXIF + XMP after decompression
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using Image image = ImageReader.Load(upload, untrusted, timeout.Token);
        image.Mutate(x => x.Thumbnail(512, 512), timeout.Token);
        ImageWriter.Save(image, output, new WebPEncoderOptions { Quality = 80 }, timeout.Token);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void Limits_AtomicClone()
    {
        using Image image = ImageReader.Load(P("input.png"));
        Rgba32[] before = image.GetPixelSpan().ToArray();
        string path = P("canceled.png");
        using var source = new CancellationTokenSource();
        source.Cancel();
        CancellationToken cancellationToken = source.Token;
        bool canceled = false;
        try
        {
            using Image result = image.Clone(x => x.Resize(1600, 0).GaussianBlur(2f), cancellationToken);
            ImageWriter.Save(result, path, new PngEncoderOptions(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // image is unchanged and no file was written
            canceled = true;
        }

        Assert.True(canceled);
        Assert.False(File.Exists(path));
        Assert.Equal(before, image.GetPixelSpan().ToArray());
    }

    // ------------------------------------------------------------ README.md

    [Fact]
    public async Task Readme_QuickStart()
    {
        using var image = await ImageReader.LoadAsync(P("photo.jpg"));
        image.Mutate(x => x
            .AutoOrient()
            .Resize(1600, 0)                 // 0 keeps the aspect ratio
            .UnsharpMask());
        await image.SaveAsWebPAsync(P("photo.webp"), new() { Quality = 82 });

        using Image thumbnail = image.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
        thumbnail.SaveAsPng(P("thumbnail.png"));
        thumbnail.SaveAsPcx(P("thumbnail.pcx"));

        Assert.Equal(1600, ImageReader.Identify(P("photo.webp")).Width);
        ImageInfo thumb = ImageReader.Identify(P("thumbnail.png"));
        Assert.Equal((256, 256), (thumb.Width, thumb.Height));
        using Image pcx = ImageReader.Load(P("thumbnail.pcx"));
        Assert.Equal(thumbnail.GetPixelSpan().ToArray(), pcx.GetPixelSpan().ToArray());
    }

    // ------------------------------------------------------------ getting-started.md

    [Fact]
    public void GettingStarted()
    {
        using (var image = ImageReader.Load(P("photo.jpg")))
        {
            Assert.Equal(ImageFormat.Jpeg, image.SourceFormat);
            image.Mutate(x => x.AutoOrient().Resize(800, 0).Grayscale());
            ImageWriter.Save(image, P("photo-small.webp"));
        }

        Assert.Equal(800, ImageReader.Identify(P("photo-small.webp")).Width);

        using var original = ImageReader.Load(P("photo.jpg"));
        original.Mutate(x => x.AutoOrient());
        using Image small = original.Clone(x => x.Resize(640, 0));
        using Image square = original.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
        using Image copy = original.Clone();
        Assert.Equal((640, 256, original.Width), (small.Width, square.Height, copy.Width));

        using var canvas = new Image(320, 200, Color.White);
        canvas[10, 10] = Color.Red;
        Rgba32 pixel = canvas[10, 10];
        Assert.Equal(new Rgba32(255, 0, 0, 255), pixel);

        byte[] upload = [1, 2, 3, 4, 5];
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(upload, new DecoderOptions { MaxPixels = 50_000_000 }));
        Assert.Throws<NotSupportedException>(() => ImageWriter.Save(canvas, P("x.tiff")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebPEncoderOptions { Quality = 120 });
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(P("photo.jpg"), new DecoderOptions { MaxPixels = 100 }));
    }

    // ------------------------------------------------------------ loading-and-saving.md

    [Fact]
    public async Task LoadingAndSaving()
    {
        byte[] bytes = [.. new byte[16], .. File.ReadAllBytes(P("input.png"))];
        CancellationToken cancellationToken = CancellationToken.None;
        using var stream = new MemoryStream(bytes[16..]);

        using var fromStream = ImageReader.Load(stream);
        using var fromBytes = ImageReader.Load(bytes[16..]);
        using var fromSpan = ImageReader.Load(bytes.AsSpan(16));
        using var fromAsync = await ImageReader.LoadAsync(P("input.png"), cancellationToken);
        Assert.Equal(fromAsync.GetPixelSpan().ToArray(), fromSpan.GetPixelSpan().ToArray());
        Assert.Equal(fromStream.Size, fromBytes.Size);

        var options = new DecoderOptions
        {
            ApplyExifOrientation = true,
            FirstFrameOnly = true,
            IgnoreMetadata = true,
            MaxPixels = 40_000_000,
        };
        using var upload = ImageReader.Load(P("photo.jpg"), options);
        Assert.Equal(1_000_000, (DecoderOptions.Default with { MaxPixels = 1_000_000 }).MaxPixels);

        ImageWriter.Save(upload, P("out.webp"));
        ImageWriter.Save(upload, P("out.bin"), new PngEncoderOptions());
        using var output = new MemoryStream();
        ImageWriter.Save(upload, output, ImageFormat.Jpeg);
        ImageWriter.Save(upload, output, new WebPEncoderOptions { Lossless = true });
        await ImageWriter.SaveAsync(upload, P("out.png"), cancellationToken);
        upload.SaveAsWebP(P("photo.webp"), new() { Quality = 82 });
        upload.SaveAsPng(output);
        await upload.SaveAsJpegAsync(P("photo.jpg2.jpg"), new() { Quality = 90, Progressive = true });
        await upload.SaveAsBmpAsync(output, cancellationToken: cancellationToken);
        byte[] webp = upload.ToWebP();
        byte[] png = upload.ToPng(new() { CompressionLevel = 9 });

        ImageInfo info = ImageReader.Identify(P("photo.webp"));
        Assert.Equal((ImageFormat.WebP, upload.Width), (info.Format, info.Width));
        Assert.Equal(ImageFormat.Png, ImageFormats.DetectFormat(P("unknown.bin")));
        Assert.Equal(ImageFormat.Png, ImageFormats.DetectFormat(P("out.bin")));
        Assert.Equal(ImageFormat.WebP, ImageFormats.DetectFormat(webp));
        Assert.Equal(ImageFormat.Png, ImageFormats.DetectFormat(png));
        Assert.Equal(ImageFormat.Jpeg, ImageFormats.FromExtension("photo.JPG"));

        // Convert a folder to WebP.
        string folder = P("photos");
        Directory.CreateDirectory(folder);
        File.Copy(P("photo.jpg"), Path.Combine(folder, "a.jpg"));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "skip me");
        foreach (string path in Directory.EnumerateFiles(folder))
        {
            if (ImageFormats.FromExtension(path) is not { } format || !ImageFormats.CanDecode(format))
            {
                continue;
            }

            using var image = ImageReader.Load(path, new DecoderOptions { ApplyExifOrientation = true });
            image.Mutate(x => x.Thumbnail(2048, 2048));
            image.SaveAsWebP(Path.ChangeExtension(path, ".webp"), new() { Quality = 80 });
        }

        Assert.True(File.Exists(Path.Combine(folder, "a.webp")));

        // Thumbnails while loading; the ASP.NET Core recipe uses the same options.
        var thumbnailOptions = new DecoderOptions { TargetSize = new(256, 256), ApplyExifOrientation = true };
        using (var thumbnail = ImageReader.Load(P("photo.jpg"), thumbnailOptions))
        {
            Assert.True(thumbnail.Width <= 256 && thumbnail.Height <= 256);
        }

        int? w = 40;
        var serveOptions = new DecoderOptions { TargetSize = new(w ?? 400, 0), ApplyExifOrientation = true };
        using (var served = await ImageReader.LoadAsync(P("photo.jpg"), serveOptions, cancellationToken))
        {
            Assert.Equal(40, served.Width);
            Assert.Equal(ImageFormat.WebP, ImageFormats.DetectFormat(served.ToWebP(new() { Quality = 80 })));
        }

        // Choose the output format from a MIME type.
        string accept = "image/webp";
        ImageFormat target = ImageFormats.FromMimeType(accept) ?? ImageFormat.Png;
        using var body = new MemoryStream();
        ImageWriter.Save(upload, body, target);
        Assert.Equal(ImageFormat.WebP, ImageFormats.DetectFormat(body.ToArray()));
    }

    // ------------------------------------------------------------ encoder-options.md

    [Fact]
    public void EncoderOptions()
    {
        using var image = ImageReader.Load(P("input.png"));
        image.SaveAsWebP(P("lossy.webp"));
        image.SaveAsWebP(P("small.webp"), new() { Quality = 70, Method = 6 });
        image.SaveAsWebP(P("exact.webp"), new() { Lossless = true, Exact = true });
        image.SaveAsWebP(P("near.webp"), new() { Lossless = true, NearLossless = 60 });
        image.SaveAsWebP(P("target.webp"), new() { TargetSize = 50_000, Passes = 6 });
        image.SaveAsPng(P("out.png"));
        image.SaveAsPng(P("fast.png"), new() { CompressionLevel = 1 });
        image.SaveAsPng(P("interlaced.png"), new() { Interlace = true });
        image.SaveAsJpeg(P("photo.jpg"), new() { Quality = 88 });
        image.SaveAsJpeg(P("web.jpg"), new() { Quality = 80, Progressive = true });
        image.SaveAsJpeg(P("sharp-color.jpg"), new() { Subsampling = JpegSubsampling.Ratio444 });
        image.SaveAsJpeg(P("on-black.jpg"), new() { BackgroundColor = Color.Black });
        image.SaveAsBmp(P("out.bmp"));
        image.SaveAsBmp(P("rgb.bmp"), new() { BitsPerPixel = 24 });
        image.SaveAsPcx(P("out.pcx"));
        image.SaveAsPcx(P("rgb.pcx"), new() { BitsPerPixel = 24 });
        image.SaveAsGif(P("out.gif"));
        image.SaveAsGif(P("small.gif"), new() { ColorCount = 64, Dither = GifDither.FloydSteinberg });
        image.SaveAsGif(P("fast.gif"), new() { Quantizer = GifQuantizer.Octree });
        image.SaveAsGif(P("soft-edges.gif"), new() { TransparencyThreshold = 32 });
        Assert.Equal(ImageFormat.Gif, ImageFormats.DetectFormat(P("small.gif")));
        Assert.Equal(ImageFormat.Pcx, ImageFormats.DetectFormat(P("rgb.pcx")));

        using Image exactBack = ImageReader.Load(P("exact.webp"));
        Assert.Equal(image.GetPixelSpan().ToArray(), exactBack.GetPixelSpan().ToArray());

        using Image few = new(4, 4, Color.Red);
        few.SaveAsPng(P("indexed.png"), new() { ColorType = PngColorType.Palette });
        Assert.Throws<InvalidOperationException>(() => image.SaveAsPng(P("indexed.png"), new() { ColorType = PngColorType.Palette }));

        var exact = new WebPEncoderOptions { Lossless = true, Exact = true };
        var web = new WebPEncoderOptions { Quality = 78, Method = 6, Preset = WebPPreset.Photo, UseSharpYuv = true };
        var screenshot = new WebPEncoderOptions { Lossless = true, NearLossless = 80 };
        var anonymous = new JpegEncoderOptions { Quality = 85, Metadata = MetadataKinds.None };
        WebPEncoderOptions thumbnails = web with { Quality = 70 };
        Assert.Equal(70, thumbnails.Quality);
        Assert.Equal(WebPPreset.Photo, thumbnails.Preset);
        Assert.NotEmpty(image.ToWebP(exact));
        Assert.NotEmpty(image.ToWebP(screenshot));
        Assert.NotEmpty(image.ToJpeg(anonymous));
    }

    // ------------------------------------------------------------ image-processing.md

    [Fact]
    public void ImageProcessing()
    {
        using var photo = ImageReader.Load(P("photo.jpg"));
        using var logo = ImageReader.Load(P("logo.png"));
        photo.Mutate(x => x
            .AutoOrient()
            .Resize(new ResizeOptions { Size = new(1200, 800), Mode = ResizeMode.Crop, Position = AnchorPosition.Top })
            .Contrast(1.1f)
            .DrawImage(logo, AnchorPosition.BottomRight, opacity: 0.8f));
        photo.SaveAsJpeg(P("card.jpg"), new() { Quality = 88 });
        Assert.Equal(new Size(1200, 800), photo.Size);

        using Image image = photo.Clone(x => x.Resize(400, 200));
        Assert.Equal(new Size(800, 600), image.Clone(x => x.Resize(800, 600)).Size);
        Assert.Equal(new Size(800, 400), image.Clone(x => x.Resize(800, 0)).Size);
        Assert.Equal(new Size(100, 50), image.Clone(x => x.Resize(100, 100, ResizeMode.Max)).Size);
        Assert.Equal(new Size(100, 100), image.Clone(x => x.Resize(100, 100, ResizeMode.Crop, ResamplingFilter.Bicubic)).Size);
        Assert.Equal(new Size(100, 100), image.Clone(x => x.Resize(100, 100, ResizeMode.Pad)).Size);
        Assert.Equal(new Size(200, 100), image.Clone(x => x.Resize(100, 100, ResizeMode.Min)).Size);
        Assert.Equal(new Size(400, 200), image.Clone(x => x.Thumbnail(1920, 1080)).Size);

        using Image padded = image.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new(1080, 1080),
            Mode = ResizeMode.Pad,
            Position = AnchorPosition.Bottom,
            PadColor = Color.White,
            Filter = ResamplingFilter.Lanczos3,
            AllowUpscale = false,
        }));
        Assert.Equal(new Size(1080, 1080), padded.Size);
        Assert.Equal(Rgba32.White, padded[0, 0]);

        using Image geometry = image.Clone(x => x
            .Crop(new Rectangle(10, 10, 200, 100))
            .Crop(150, 80)
            .Crop(100, 60, AnchorPosition.TopRight)
            .Pad(120, 120, Color.Black)
            .Rotate(RotateMode.Rotate90)
            .Flip(FlipMode.Horizontal)
            .RotateFlip(RotateMode.Rotate270, FlipMode.Vertical)
            .AutoOrient());
        Assert.Equal(new Size(120, 120), geometry.Size);

        using Image rotated = image.Clone(x => x
            .Rotate(15)
            .Rotate(-30, ResamplingFilter.Bilinear, Color.White)
            .Rotate(45, ResamplingFilter.Nearest, expand: false));
        Assert.True(rotated.Width > 400);

        using var watermark = ImageReader.Load(P("logo.png"));
        using Image adjusted = image.Clone(x => x
            .Brightness(1.2f).Contrast(0.8f).Saturate(1.5f).Grayscale().Sepia().Invert()
            .BackgroundColor(Color.White).Opacity(0.5f).MakeTransparent(Color.Magenta)
            .Fill(Color.Black).Fill(Color.FromArgb(128, 255, 0, 0), new Rectangle(0, 0, 50, 50))
            .GaussianBlur(2.5f).BoxBlur(3).UnsharpMask().UnsharpMask(radius: 1, percent: 80, threshold: 2).Sharpen()
            .DrawImage(watermark, AnchorPosition.BottomRight, opacity: 0.6f)
            .DrawImage(watermark, new Point(20, 20))
            .Apply(frame =>
            {
                foreach (ref Rgba32 p in frame.GetPixelSpan())
                {
                    p.B = 0;                                      // drop the blue channel
                }
            })
            .Resize(600, 0)
            .Polaroid());
        Assert.Equal(new Size(640, 420), adjusted.Size);
        Assert.Equal(Rgba32.White, adjusted[0, 0]);
    }

    // ------------------------------------------------------------ pixels-and-interop.md

    [Fact]
    public void PixelsAndInterop()
    {
        var orange = new Rgba32(255, 136, 0);
        var glass = new Rgba32(255, 255, 255, 64);
        Rgba32 fromColor = Color.CornflowerBlue;
        Color back = orange;
        Rgba32 parsed = Rgba32.Parse("#ff8800");
        Rgba32 named = Rgba32.Parse("cornflowerblue");
        string userInput = "#80ff0080";
        bool ok = Rgba32.TryParse(userInput, out Rgba32 color);
        string hex = orange.ToHex();
        (byte r, byte g, byte b, byte a) = orange;
        Assert.Equal(orange, parsed);
        Assert.Equal(fromColor, named);
        Assert.Equal(Color.FromArgb(255, 255, 136, 0).ToArgb(), back.ToArgb());
        Assert.True(ok);
        Assert.Equal(new Rgba32(0x80, 0xff, 0, 0x80), color);
        Assert.Equal("#FF8800FF", hex);
        Assert.Equal((255, 136, 0, 255, 64), (r, g, b, a, glass.A));

        using var image = new Image(64, 32, Color.CornflowerBlue);
        image[0, 0] = Rgba32.Parse("#ff8800");
        Span<Rgba32> row = image.GetRowSpan(1);
        row.Fill(new Rgba32(255, 255, 255, 128));
        Span<Rgba32> all = image.GetPixelSpan();
        foreach (ref Rgba32 p in all)
        {
            p.A = (byte)(p.A / 2);
        }

        Assert.Equal(64, image[5, 1].A);
        Assert.False(image.IsOpaque());

        byte[] bgra = image.ToPixelData(PixelFormat.Bgra32);
        using Image copy = Image.LoadPixelData(bgra, image.Width, image.Height, PixelFormat.Bgra32);
        Assert.Equal(image.GetPixelSpan().ToArray(), copy.GetPixelSpan().ToArray());

        int stride = (image.Width * PixelFormat.Rgb24.BytesPerPixel()) + 4;
        var buffer = new byte[stride * image.Height];
        image.CopyPixelDataTo(buffer, PixelFormat.Rgb24, stride);
        image.CopyPixelDataFrom(buffer, PixelFormat.Rgb24, stride);
        Assert.Equal(255, image[0, 0].A);

        Rgba32[] rgbaPixels = image.GetPixelSpan().ToArray();
        using Image fromStructs = Image.LoadPixelData(rgbaPixels, image.Width, image.Height);
        Assert.Equal(rgbaPixels, fromStructs.GetPixelSpan().ToArray());

        // The PictureSharp side of the WPF and SkiaSharp recipes.
        byte[] pixels = image.ToPixelData(PixelFormat.Bgra32);
        Assert.Equal(image.Width * 4 * image.Height, pixels.Length);
        int rowBytes = (image.Width * 4) + 16;
        var skiaBuffer = new byte[rowBytes * image.Height];
        image.CopyPixelDataTo(skiaBuffer, PixelFormat.Rgba32, rowBytes);
        using Image fromSkia = Image.LoadPixelData(skiaBuffer, image.Width, image.Height, PixelFormat.Rgba32, rowBytes);
        Assert.Equal(image.GetPixelSpan().ToArray(), fromSkia.GetPixelSpan().ToArray());

        // The format-independent image API and WebP converter.
        image.SaveAsWebP(P("image.webp"), new() { Lossless = true, Exact = true });
        byte[] data = File.ReadAllBytes(P("image.webp"));
        ImageInfo info = ImageReader.Identify(data);
        Assert.Equal(WebPCompression.Lossless, info.WebPCompression);
        using Image decoded = ImageReader.Load(data, new DecoderOptions { FirstFrameOnly = true });
        (int width, int height) = (decoded.Width, decoded.Height);
        byte[] rgba = decoded.ToPixelData();
        byte[] bgraDecoded = decoded.ToPixelData(PixelFormat.Bgra32);
        var target = new byte[width * height * 4];
        decoded.CopyPixelDataTo(target, PixelFormat.Bgra32);
        Size size = decoded.Size;
        using Image fromRgba = Image.LoadPixelData(rgba, width, height, PixelFormat.Rgba32);
        byte[] lossless = ImageWriter.Encode(fromRgba, new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image allFrames = ImageReader.Load(data);
        PictureSharp.Formats.WebP.WebPConverter.Convert(P("input.png"), P("output.webp"), new() { Quality = 80 });
        Assert.Equal(bgraDecoded, target);
        Assert.Equal(image.Size, size);
        using Image roundTrip = ImageReader.Load(lossless, new DecoderOptions { FirstFrameOnly = true });
        Assert.Equal(rgba, roundTrip.ToPixelData());
        Assert.Equal(image.ToPixelData(), rgba);
    }

    // ------------------------------------------------------------ animation.md

    [Fact]
    public void Animation()
    {
        using (var animation = ImageReader.Load(P("animated.webp")))
        {
            Assert.True(animation.IsAnimated);
            foreach (ImageFrame frame in animation.Frames)
            {
                Assert.True(frame.Duration > TimeSpan.Zero);
            }

            using Image third = animation.Frames.CloneFrame(2);
            third.SaveAsPng(P("frame-3.png"));
            Assert.Equal(1, third.FrameCount);
        }

        using var first = ImageReader.Load(P("frame-1.png"));
        using var second = ImageReader.Load(P("frame-2.png"));
        using var built = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), first, second);
        built.LoopCount = 0;
        built.Frames[1].Duration = TimeSpan.FromMilliseconds(150);
        built.SaveAsWebP(P("animation.webp"), new() { Lossless = true, Exact = true, MinimizeSize = true });

        using Image back = ImageReader.Load(P("animation.webp"));
        Assert.Equal(2, back.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(150), back.Frames[1].Duration);
        Assert.Equal(first.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());

        using Image thirdImage = first.Clone(x => x.Grayscale());
        built.Frames.Add(thirdImage, TimeSpan.FromMilliseconds(80));
        built.Frames.Insert(0, new ImageFrame(built.Width, built.Height) { Duration = TimeSpan.FromMilliseconds(40) });
        built.Frames.Move(0, 2);
        built.Frames.RemoveAt(1);
        using Image extracted = built.Frames.ExportFrame(0);
        Assert.Equal(2, built.FrameCount);

        built.Mutate(x => x.Resize(32, 0).Saturate(1.2f));
        Assert.All(built.Frames, f => Assert.Equal(32, f.Width));
        built.SaveAsPng(P("animation.png"));
        built.SaveAsPng(P("poster.png"), new() { SaveAnimation = false });
        Assert.Equal(2, ImageReader.Identify(P("animation.png")).FrameCount);
        Assert.Equal(1, ImageReader.Identify(P("poster.png")).FrameCount);

        built.SaveAsGif(P("animation.gif"));
        built.SaveAsGif(P("small.gif"), new() { ColorCount = 64, Dither = GifDither.FloydSteinberg });
        Assert.Equal(2, ImageReader.Identify(P("animation.gif")).FrameCount);

        PictureSharp.Formats.WebP.WebPConverter.Convert(P("animation.gif"), P("animation.webp"));
        using (var gif = ImageReader.Load(P("animation.gif")))
        {
            gif.SaveAsWebP(P("lossy.webp"), new() { Quality = 80, KeyframeMin = 3, KeyframeMax = 5 });
        }

        using (var webp = ImageReader.Load(P("animated.webp")))
        {
            webp.SaveAsGif(P("animated.gif"));
        }

        using Image converted = ImageReader.Load(P("animation.webp"));
        using Image gifBack = ImageReader.Load(P("animation.gif"));
        Assert.Equal(gifBack.FrameCount, converted.FrameCount);
        Assert.Equal(gifBack.GetPixelSpan().ToArray(), converted.GetPixelSpan().ToArray());
        Assert.Equal(ImageReader.Identify(P("animated.webp")).FrameCount, ImageReader.Identify(P("animated.gif")).FrameCount);
    }

    // ------------------------------------------------------------ metadata.md

    [Fact]
    public void Metadata()
    {
        using var photo = ImageReader.Load(P("photo.jpg"));
        photo.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        photo.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        photo.SaveAsJpeg(P("tagged.jpg"));

        using var reloaded = ImageReader.Load(P("tagged.jpg"));
        ImageMetadata meta = reloaded.Metadata;
        Assert.Equal(ExifOrientation.RightTop, meta.Orientation);
        Assert.NotNull(meta.Xmp);

        using var upright = ImageReader.Load(P("tagged.jpg"), new DecoderOptions { ApplyExifOrientation = true });
        reloaded.Mutate(x => x.AutoOrient());
        Assert.Equal(ExifOrientation.TopLeft, reloaded.Metadata.Orientation);
        Assert.Equal(upright.Size, reloaded.Size);
        Assert.Equal((photo.Height, photo.Width), (upright.Width, upright.Height));

        reloaded.SaveAsWebP(P("share.webp"), new() { Metadata = MetadataKinds.Icc });
        reloaded.SaveAsJpeg(P("clean.jpg"), new() { Metadata = MetadataKinds.None });
        using (Image clean = ImageReader.Load(P("clean.jpg")))
        using (Image shared = ImageReader.Load(P("share.webp")))
        {
            Assert.True(clean.Metadata.IsEmpty);
            Assert.Null(shared.Metadata.Exif);
        }

        meta.HorizontalResolution = 300;
        meta.VerticalResolution = 300;
        meta.Xmp = null;
        meta.Clear();
        Assert.True(meta.IsEmpty);
        Assert.Equal(300f, meta.HorizontalResolution);
        Assert.Throws<ArgumentOutOfRangeException>(() => meta.HorizontalResolution = 0);
    }
}

public static class MyOperations
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext Polaroid() =>
            x.Pad(x.Size.Width + 40, x.Size.Height + 120, Color.White, AnchorPosition.Top);
    }
}
