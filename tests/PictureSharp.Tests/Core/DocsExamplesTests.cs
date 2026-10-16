using System.Drawing;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
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
        using (Image second = Image.Load(P("frame-1.png")))
        {
            second.Mutate(x => x.Invert());
            second.Save(P("frame-2.png"));
        }

        File.Copy(P("input.png"), P("unknown.bin"));
        File.Copy(Corpus.PathOf("color/png_gama_chrm.png"), P("wide-gamut.png"));
        File.Copy(Corpus.PathOf("src/photo.png"), P("photo.png"));
        File.Copy(Corpus.PathOf("hdr/iso_orientation6.jpg"), P("IMG_8113.jpeg"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    [Test]
    public async Task Hdr_LoadReconstructToneMapAndEdit()
    {
        var options = new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true };
        using Image image = Image.Load(P("IMG_8113.jpeg"), options);
        await Assert.That(image.HasHdr).IsTrue();
        if (image.HasHdr)
        {
            using HdrImage hdr = image.ToHdr();
            ReadOnlySpan<Rgba128> row = hdr.GetRowSpan(0);
            await Assert.That(row.Length).IsEqualTo(hdr.Width);
            await Assert.That(hdr.ColorSpace).IsEqualTo(HdrColorSpace.LinearDisplayP3);
            using Image sdr = hdr.ToneMap(new ToneMappingOptions { Exposure = -0.5f });
            sdr.Save(P("tone-mapped.jpg"));
        }

        image.Save(P("base.jpg"));
        await Assert.That(Image.Load(P("base.jpg"), options).Hdr).IsNull();

        HdrInfo? info = Image.Identify(P("IMG_8113.jpeg"), new DecoderOptions { LoadGainMap = true }).Hdr;
        await Assert.That(info is { CanDecode: true }).IsTrue();
        await Assert.That(info!.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);

        Image.EditMetadata(P("IMG_8113.jpeg"), P("share.jpeg"), m => m.RemoveBlocks(MetadataKinds.Exif | MetadataKinds.Xmp));
        await Assert.That(Image.Identify(P("share.jpeg"), new DecoderOptions { LoadGainMap = true }).Hdr).IsEqualTo(info);
    }

    [Test]
    public async Task Metadata_TypedTagsAndEditingWithoutRecompression()
    {
        ImageMetadata metadata = Image.ReadMetadata(P("photo.jpg"));
        metadata.ExifProfile.SetValue(ExifTag.Model, "Camera");
        metadata.ExifProfile.SetValue(ExifTag.ExposureTime, new ExifRational(1, 125));
        metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, new DateTime(2026, 10, 15, 12, 0, 0));
        Image.EditMetadata(P("photo.jpg"), P("edited.jpg"), m => m.Exif = metadata.Exif);
        var read = Image.ReadMetadata(P("edited.jpg"));
        await Assert.That(read.ExifProfile.TryGetValue(ExifTag.Model, out var model)).IsTrue();
        await Assert.That(model).IsEqualTo("Camera");
        Image.EditMetadata(P("edited.jpg"), P("share.jpg"), m => m.ExifProfile.RemoveGps());
        await Image.EditMetadataAsync(P("share.jpg"), P("share.jpg"), m => m.Clear());
        await Assert.That((await Image.ReadMetadataAsync(P("share.jpg"))).IsEmpty).IsTrue();
    }

    [Test]
    public async Task Ico_IconsAndVariantOwnership()
    {
        using Image source = Image.Load(P("logo.png"));
        using Image small = source.Clone(x => x.Resize(16, 16, ResizeMode.Pad));
        using Image large = source.Clone(x => x.Resize(256, 256, ResizeMode.Pad));
        small.SaveAsIco(P("favicon.ico"));
        IcoFile.Save([small, large], P("app.ico"));
        using Image preferred = Image.Load(P("app.ico"), new() { Ico = new() { PreferredSize = new(32, 32) }});
        using Image indexed = Image.Load(P("app.ico"), new() { Ico = new() { Index = 0 }});
        await Assert.That(preferred.Width).IsEqualTo(16);
        await Assert.That(indexed.Width).IsEqualTo(16);
        await Assert.That(IcoFile.Identify(P("app.ico")).Count).IsEqualTo(2);
        using IcoImageCollection variants = IcoFile.LoadAll(P("app.ico"));
        await IcoFile.SaveAsync(variants, P("legacy.ico"), new() { Encoding = IcoEncoding.Dib });
        using IcoImageCollection legacy = IcoFile.LoadAll(P("legacy.ico"));
        await Assert.That(legacy[1].ToPixelData()).IsEquivalentTo(large.ToPixelData(), CollectionOrdering.Matching);
    }

    // ------------------------------------------------------------ color-management.md

    [Test]
    public async Task ColorManagement_ConversionEditingAndLinearLight()
    {
        using Image photo = Image.Load(P("wide-gamut.png"), new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });

        using Image other = Image.Load(P("wide-gamut.png"));
        other.Mutate(x => x.ConvertToSrgb().Resize(800, 0));
        await Assert.That(photo.Metadata.HasColorDescription).IsFalse();
        await Assert.That(other.Metadata.Gamma).IsNull();

        byte[] tagged = Image.EditMetadata(File.ReadAllBytes(P("input.png")), m =>
        {
            m.ClearColorDescription();
            m.SrgbRenderingIntent = RenderingIntent.Perceptual;
        });
        await Assert.That(Image.ReadMetadata(tagged).SrgbRenderingIntent).IsEqualTo(RenderingIntent.Perceptual);

        using Image image = Image.Load(P("photo.png"));
        using Image logo = Image.Load(P("logo.png"));
        image.Mutate(x => x
            .Resize(new ResizeOptions { Size = new(800, 0), LinearLight = true })
            .DrawImage(logo, AnchorPosition.BottomRight, new DrawImageOptions { Opacity = 0.8f, LinearLight = true }));
        await Assert.That(image.Width).IsEqualTo(800);
    }

    // ------------------------------------------------------------ limits-and-cancellation.md

    [Test]
    public async Task Limits_UntrustedUpload()
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
        using Image image = Image.Load(upload, untrusted, timeout.Token);
        image.Mutate(x => x.Thumbnail(512, 512), timeout.Token);
        image.Save(output, new WebPEncoderOptions { Quality = 80 }, timeout.Token);
        await Assert.That(output.Length > 0).IsTrue();
    }

    [Test]
    public async Task Limits_AtomicClone()
    {
        using Image image = Image.Load(P("input.png"));
        Rgba32[] before = image.GetPixelSpan().ToArray();
        string path = P("canceled.png");
        using var source = new CancellationTokenSource();
        source.Cancel();
        CancellationToken cancellationToken = source.Token;
        bool canceled = false;
        try
        {
            using Image result = image.Clone(x => x.Resize(1600, 0).GaussianBlur(2f), cancellationToken);
            result.Save(path, new PngEncoderOptions(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // image is unchanged and no file was written
            canceled = true;
        }

        await Assert.That(canceled).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(image.GetPixelSpan().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    // ------------------------------------------------------------ README.md

    [Test]
    public async Task Readme_QuickStart()
    {
        using var image = await Image.LoadAsync(P("photo.jpg"));
        image.Mutate(x => x
            .AutoOrient()
            .Resize(1600, 0)                 // 0 keeps the aspect ratio
            .UnsharpMask());
        await image.SaveAsync(P("photo.webp"));  // the format comes from the extension

        using Image thumbnail = image.Clone(x => x.Thumbnail(256, 256));
        thumbnail.Save(P("thumbnail.png"));

        await Assert.That(Image.Identify(P("photo.webp")).Width).IsEqualTo(1600);
        ImageInfo thumb = Image.Identify(P("thumbnail.png"));
        await Assert.That(Math.Max(thumb.Width, thumb.Height)).IsEqualTo(256);
        using Image png = Image.Load(P("thumbnail.png"));
        await Assert.That(png.GetPixelSpan().ToArray()).IsEquivalentTo(thumbnail.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    // ------------------------------------------------------------ getting-started.md

    [Test]
    public async Task GettingStarted()
    {
        using (var image = Image.Load(P("photo.jpg")))
        {
            await Assert.That(image.SourceFormat).IsEqualTo(ImageFormat.Jpeg);
            image.Mutate(x => x.AutoOrient().Resize(800, 0).Grayscale());
            image.Save(P("photo-small.webp"));
        }

        await Assert.That(Image.Identify(P("photo-small.webp")).Width).IsEqualTo(800);

        using var original = Image.Load(P("photo.jpg"));
        original.Mutate(x => x.AutoOrient());
        using Image small = original.Clone(x => x.Resize(640, 0));
        using Image square = original.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
        using Image copy = original.Clone();
        await Assert.That((small.Width, square.Height, copy.Width)).IsEqualTo((640, 256, original.Width));

        using var canvas = new Image(320, 200, Color.White);
        canvas[10, 10] = Color.Red;
        Rgba32 pixel = canvas[10, 10];
        await Assert.That(pixel).IsEqualTo(new Rgba32(255, 0, 0, 255));

        byte[] upload = [1, 2, 3, 4, 5];
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(upload, new DecoderOptions { MaxPixels = 50_000_000 }));
        Assert.ThrowsExactly<NotSupportedException>(() => canvas.Save(P("x.tiff")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WebPEncoderOptions { Quality = 120 });
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(P("photo.jpg"), new DecoderOptions { MaxPixels = 100 }));
    }

    // ------------------------------------------------------------ loading-and-saving.md

    [Test]
    public async Task LoadingAndSaving()
    {
        byte[] bytes = [.. new byte[16], .. File.ReadAllBytes(P("input.png"))];
        CancellationToken cancellationToken = CancellationToken.None;
        using var stream = new MemoryStream(bytes[16..]);

        using var fromStream = Image.Load(stream);
        using var fromBytes = Image.Load(bytes[16..]);
        using var fromSpan = Image.Load(bytes.AsSpan(16));
        using var fromAsync = await Image.LoadAsync(P("input.png"), cancellationToken);
        await Assert.That(fromSpan.GetPixelSpan().ToArray()).IsEquivalentTo(fromAsync.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        await Assert.That(fromBytes.Size).IsEqualTo(fromStream.Size);

        var options = new DecoderOptions
        {
            ApplyExifOrientation = true,
            FirstFrameOnly = true,
            IgnoreMetadata = true,
            MaxPixels = 40_000_000,
        };
        using var upload = Image.Load(P("photo.jpg"), options);
        await Assert.That((DecoderOptions.Default with { MaxPixels = 1_000_000 }).MaxPixels).IsEqualTo(1_000_000);

        upload.Save(P("out.webp"));
        upload.Save(P("out.bin"), new PngEncoderOptions());
        using var output = new MemoryStream();
        upload.Save(output, ImageFormat.Jpeg);
        upload.Save(output, new WebPEncoderOptions { Lossless = true });
        await upload.SaveAsync(P("out.png"), cancellationToken);
        upload.SaveAsWebP(P("photo.webp"), new() { Quality = 82 });
        upload.SaveAsPng(output);
        await upload.SaveAsJpegAsync(P("photo.jpg2.jpg"), new() { Quality = 90, Progressive = true });
        await upload.SaveAsBmpAsync(output, cancellationToken: cancellationToken);
        byte[] webp = upload.ToWebP();
        byte[] png = upload.ToPng(new() { CompressionLevel = 9 });

        ImageInfo info = Image.Identify(P("photo.webp"));
        await Assert.That((info.Format, info.Width)).IsEqualTo((ImageFormat.WebP, upload.Width));
        await Assert.That(Image.DetectFormat(P("unknown.bin"))).IsEqualTo(ImageFormat.Png);
        await Assert.That(Image.DetectFormat(P("out.bin"))).IsEqualTo(ImageFormat.Png);
        await Assert.That(Image.DetectFormat(webp)).IsEqualTo(ImageFormat.WebP);
        await Assert.That(Image.DetectFormat(png)).IsEqualTo(ImageFormat.Png);
        await Assert.That(ImageFormat.FromExtension("photo.JPG")).IsEqualTo(ImageFormat.Jpeg);

        // Convert a folder to WebP.
        string folder = P("photos");
        Directory.CreateDirectory(folder);
        File.Copy(P("photo.jpg"), Path.Combine(folder, "a.jpg"));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "skip me");
        foreach (string path in Directory.EnumerateFiles(folder))
        {
            if (ImageFormat.FromExtension(path) is not { } format || !format.CanDecode)
            {
                continue;
            }

            using var image = Image.Load(path, new DecoderOptions { ApplyExifOrientation = true });
            image.Mutate(x => x.Thumbnail(2048, 2048));
            image.SaveAsWebP(Path.ChangeExtension(path, ".webp"), new() { Quality = 80 });
        }

        await Assert.That(File.Exists(Path.Combine(folder, "a.webp"))).IsTrue();

        // Thumbnails while loading; the ASP.NET Core recipe uses the same options.
        var thumbnailOptions = new DecoderOptions { TargetSize = new(256, 256), ApplyExifOrientation = true };
        using (var thumbnail = Image.Load(P("photo.jpg"), thumbnailOptions))
        {
            await Assert.That(thumbnail.Width <= 256 && thumbnail.Height <= 256).IsTrue();
        }

        int? w = 40;
        var serveOptions = new DecoderOptions { TargetSize = new(w ?? 400, 0), ApplyExifOrientation = true };
        using (var served = await Image.LoadAsync(P("photo.jpg"), serveOptions, cancellationToken))
        {
            await Assert.That(served.Width).IsEqualTo(40);
            await Assert.That(Image.DetectFormat(served.ToWebP(new() { Quality = 80 }))).IsEqualTo(ImageFormat.WebP);
        }

        // Choose the output format from a MIME type.
        string accept = "image/webp";
        ImageFormat target = ImageFormat.FromMimeType(accept) ?? ImageFormat.Png;
        using var body = new MemoryStream();
        upload.Save(body, target);
        await Assert.That(Image.DetectFormat(body.ToArray())).IsEqualTo(ImageFormat.WebP);
    }

    // ------------------------------------------------------------ encoder-options.md

    [Test]
    public async Task EncoderOptions()
    {
        using var image = Image.Load(P("input.png"));
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
        await Assert.That(Image.DetectFormat(P("small.gif"))).IsEqualTo(ImageFormat.Gif);
        await Assert.That(Image.DetectFormat(P("rgb.pcx"))).IsEqualTo(ImageFormat.Pcx);

        using Image exactBack = Image.Load(P("exact.webp"));
        await Assert.That(exactBack.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);

        using Image few = new(4, 4, Color.Red);
        few.SaveAsPng(P("indexed.png"), new() { ColorType = PngColorType.Palette });
        Assert.ThrowsExactly<InvalidOperationException>(() => image.SaveAsPng(P("indexed.png"), new() { ColorType = PngColorType.Palette }));

        var exact = new WebPEncoderOptions { Lossless = true, Exact = true };
        var web = new WebPEncoderOptions { Quality = 78, Method = 6, Preset = WebPPreset.Photo, UseSharpYuv = true };
        var screenshot = new WebPEncoderOptions { Lossless = true, NearLossless = 80 };
        var anonymous = new JpegEncoderOptions { Quality = 85, Metadata = MetadataKinds.None };
        WebPEncoderOptions thumbnails = web with { Quality = 70 };
        await Assert.That(thumbnails.Quality).IsEqualTo(70);
        await Assert.That(thumbnails.Preset).IsEqualTo(WebPPreset.Photo);
        await Assert.That(image.ToWebP(exact)).IsNotEmpty();
        await Assert.That(image.ToWebP(screenshot)).IsNotEmpty();
        await Assert.That(image.ToJpeg(anonymous)).IsNotEmpty();
    }

    // ------------------------------------------------------------ image-processing.md

    [Test]
    public async Task ImageProcessing()
    {
        using var photo = Image.Load(P("photo.jpg"));
        using var logo = Image.Load(P("logo.png"));
        photo.Mutate(x => x
            .AutoOrient()
            .Resize(new ResizeOptions { Size = new(1200, 800), Mode = ResizeMode.Crop, Position = AnchorPosition.Top })
            .Contrast(1.1f)
            .DrawImage(logo, AnchorPosition.BottomRight, opacity: 0.8f));
        photo.SaveAsJpeg(P("card.jpg"), new() { Quality = 88 });
        await Assert.That(photo.Size).IsEqualTo(new Size(1200, 800));

        using Image image = photo.Clone(x => x.Resize(400, 200));
        await Assert.That(image.Clone(x => x.Resize(800, 600)).Size).IsEqualTo(new Size(800, 600));
        await Assert.That(image.Clone(x => x.Resize(800, 0)).Size).IsEqualTo(new Size(800, 400));
        await Assert.That(image.Clone(x => x.Resize(100, 100, ResizeMode.Max)).Size).IsEqualTo(new Size(100, 50));
        await Assert.That(image.Clone(x => x.Resize(100, 100, ResizeMode.Crop, ResamplingFilter.Bicubic)).Size).IsEqualTo(new Size(100, 100));
        await Assert.That(image.Clone(x => x.Resize(100, 100, ResizeMode.Pad)).Size).IsEqualTo(new Size(100, 100));
        await Assert.That(image.Clone(x => x.Resize(100, 100, ResizeMode.Min)).Size).IsEqualTo(new Size(200, 100));
        await Assert.That(image.Clone(x => x.Thumbnail(1920, 1080)).Size).IsEqualTo(new Size(400, 200));

        using Image padded = image.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new(1080, 1080),
            Mode = ResizeMode.Pad,
            Position = AnchorPosition.Bottom,
            PadColor = Color.White,
            Filter = ResamplingFilter.Lanczos3,
            AllowUpscale = false,
        }));
        await Assert.That(padded.Size).IsEqualTo(new Size(1080, 1080));
        await Assert.That(padded[0, 0]).IsEqualTo(Rgba32.White);

        using Image geometry = image.Clone(x => x
            .Crop(new Rectangle(10, 10, 200, 100))
            .Crop(150, 80)
            .Crop(100, 60, AnchorPosition.TopRight)
            .Pad(120, 120, Color.Black)
            .Rotate(RotateMode.Rotate90)
            .Flip(FlipMode.Horizontal)
            .RotateFlip(RotateMode.Rotate270, FlipMode.Vertical)
            .AutoOrient());
        await Assert.That(geometry.Size).IsEqualTo(new Size(120, 120));

        using Image rotated = image.Clone(x => x
            .Rotate(15)
            .Rotate(-30, ResamplingFilter.Bilinear, Color.White)
            .Rotate(45, ResamplingFilter.Nearest, expand: false));
        await Assert.That(rotated.Width > 400).IsTrue();

        using var watermark = Image.Load(P("logo.png"));
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
        await Assert.That(adjusted.Size).IsEqualTo(new Size(640, 420));
        await Assert.That(adjusted[0, 0]).IsEqualTo(Rgba32.White);

        using Image toned = image.Clone(x => x
            .Levels(new LevelsOptions { InputBlack = 20, InputWhite = 230 })
            .Levels(new LevelsOptions { Gamma = 1.4f, OutputBlack = 16, OutputWhite = 235 })
            .Gamma(2.2f)
            .Gamma(0.8f, ColorChannels.Blue));
        await Assert.That(toned.Size).IsEqualTo(image.Size);

        using Image texture = Image.Load(P("logo.png"));
        using Image blended = image.Clone(x => x
            .DrawImage(texture, Point.Empty, new DrawImageOptions { BlendMode = BlendMode.Multiply })
            .DrawImage(texture, AnchorPosition.Center, new DrawImageOptions
            {
                BlendMode = BlendMode.SoftLight,
                Opacity = 0.7f,
                LinearLight = true,
            }));
        await Assert.That(blended.Size).IsEqualTo(image.Size);

        using Image shape = Image.Load(P("logo.png"));
        using Image vignette = Image.Load(P("logo.png"));
        using Image masked = image.Clone(x => x
            .ApplyMask(shape)
            .ApplyMask(vignette, AnchorPosition.Center, new MaskOptions
            {
                Channel = MaskChannel.Luminance,
                Outside = MaskOutside.Transparent,
            })
            .ApplyMask(shape, new Point(40, 10), new MaskOptions { Invert = true }));
        await Assert.That(masked.Size).IsEqualTo(image.Size);

        using Image trimmed = image.Clone(x => x
            .AutoCrop()
            .AutoCrop(new AutoCropOptions { AlphaThreshold = 16, Padding = 4 })
            .AutoCrop(new AutoCropOptions
            {
                Mode = AutoCropMode.Background,
                Tolerance = 12,
            })
            .AutoCrop(new AutoCropOptions { Mode = AutoCropMode.Background, Background = Color.White }));
        await Assert.That(trimmed.Width <= image.Width && trimmed.Height <= image.Height).IsTrue();
        if (image.FindTrimBounds() is { } box)
        {
            await Assert.That(box.Width <= image.Width).IsTrue();
        }

        using Image automatic = image.Clone(x => x
            .AutoContrast()
            .AutoContrast(2f)
            .AutoContrast(1f, 5f, preserveTone: true)
            .Equalize());
        await Assert.That(automatic.Size).IsEqualTo(image.Size);

        ImageHistogram histogram = image.GetHistogram();
        long darkPixels = histogram.Luminance[..32].ToArray().Sum();
        byte median = histogram.Percentile(HistogramChannel.Luminance, 50);
        double meanRed = histogram.Mean(HistogramChannel.Red);
        ImageHistogram visible = image.GetHistogram(new HistogramOptions
        {
            Transparency = HistogramTransparency.Exclude,
            Region = new Rectangle(0, 0, 100, 100),
            AllFrames = true,
        });
        await Assert.That(histogram.PixelCount).IsEqualTo((long)image.Width * image.Height);
        await Assert.That(darkPixels <= histogram.PixelCount && median <= 255 && meanRed >= 0).IsTrue();
        await Assert.That(visible.PixelCount <= 100 * 100).IsTrue();

        // Custom frame processors: the Transpose sample, same code as Extensibility.TransposeProcessor.
        using Image transposed = image.Clone(x => x.ApplyProcessor(new Extensibility.TransposeProcessor()).Resize(800, 0));
        await Assert.That(transposed.Size).IsEqualTo(new Size(800, 1600));
    }

    // ------------------------------------------------------------ loading-and-saving.md: ImageConfiguration

    [Test]
    public async Task LoadingAndSaving_Configuration()
    {
        // Accept only PNG and JPEG from untrusted uploads; other formats fail as unrecognized.
        ImageConfiguration uploads = ImageConfiguration.Default.ToBuilder()
            .KeepOnly(ImageFormat.Png, ImageFormat.Jpeg)
            .Build();
        using (var stream = File.OpenRead(P("photo.jpg")))
        {
            using var upload = Image.Load(stream, new DecoderOptions { Configuration = uploads, MaxPixels = 50_000_000 });
            await Assert.That(upload.SourceFormat).IsSameReferenceAs(ImageFormat.Jpeg);
        }

        using Image gif = Image.Load(P("photo.jpg"));
        byte[] notAllowed = gif.Encode(ImageFormat.Gif);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(notAllowed, new DecoderOptions { Configuration = uploads }));
        await Assert.That(uploads.Formats).IsEquivalentTo([ImageFormat.Png, ImageFormat.Jpeg], CollectionOrdering.Matching);
    }

    // ------------------------------------------------------------ pixels-and-interop.md

    [Test]
    public async Task PixelsAndInterop()
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
        await Assert.That(parsed).IsEqualTo(orange);
        await Assert.That(named).IsEqualTo(fromColor);
        await Assert.That(back.ToArgb()).IsEqualTo(Color.FromArgb(255, 255, 136, 0).ToArgb());
        await Assert.That(ok).IsTrue();
        await Assert.That(color).IsEqualTo(new Rgba32(0x80, 0xff, 0, 0x80));
        await Assert.That(hex).IsEqualTo("#FF8800FF");
        await Assert.That(((int, int, int, int, int))((r, g, b, a, glass.A))).IsEqualTo((255, 136, 0, 255, 64));

        using var image = new Image(64, 32, Color.CornflowerBlue);
        image[0, 0] = Rgba32.Parse("#ff8800");
        Span<Rgba32> row = image.GetRowSpan(1);
        row.Fill(new Rgba32(255, 255, 255, 128));
        Span<Rgba32> all = image.GetPixelSpan();
        foreach (ref Rgba32 p in all)
        {
            p.A = (byte)(p.A / 2);
        }

        await Assert.That((int)(image[5, 1].A)).IsEqualTo(64);
        await Assert.That(image.IsOpaque()).IsFalse();

        byte[] bgra = image.ToPixelData(PixelFormat.Bgra32);
        using Image copy = Image.LoadPixelData(bgra, image.Width, image.Height, PixelFormat.Bgra32);
        await Assert.That(copy.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);

        int stride = (image.Width * PixelFormat.Rgb24.BytesPerPixel()) + 4;
        var buffer = new byte[stride * image.Height];
        image.CopyPixelDataTo(buffer, PixelFormat.Rgb24, stride);
        image.CopyPixelDataFrom(buffer, PixelFormat.Rgb24, stride);
        await Assert.That((int)(image[0, 0].A)).IsEqualTo(255);

        Rgba32[] rgbaPixels = image.GetPixelSpan().ToArray();
        using Image fromStructs = Image.LoadPixelData(rgbaPixels, image.Width, image.Height);
        await Assert.That(fromStructs.GetPixelSpan().ToArray()).IsEquivalentTo(rgbaPixels, CollectionOrdering.Matching);

        // The PictureSharp side of the WPF and SkiaSharp recipes.
        byte[] pixels = image.ToPixelData(PixelFormat.Bgra32);
        await Assert.That(pixels.Length).IsEqualTo(image.Width * 4 * image.Height);
        int rowBytes = (image.Width * 4) + 16;
        var skiaBuffer = new byte[rowBytes * image.Height];
        image.CopyPixelDataTo(skiaBuffer, PixelFormat.Rgba32, rowBytes);
        using Image fromSkia = Image.LoadPixelData(skiaBuffer, image.Width, image.Height, PixelFormat.Rgba32, rowBytes);
        await Assert.That(fromSkia.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);

        // The format-independent image API and WebP converter.
        image.SaveAsWebP(P("image.webp"), new() { Lossless = true, Exact = true });
        byte[] data = File.ReadAllBytes(P("image.webp"));
        ImageInfo info = Image.Identify(data);
        await Assert.That(info.WebPCompression).IsEqualTo(WebPCompression.Lossless);
        using Image decoded = Image.Load(data, new DecoderOptions { FirstFrameOnly = true });
        (int width, int height) = (decoded.Width, decoded.Height);
        byte[] rgba = decoded.ToPixelData();
        byte[] bgraDecoded = decoded.ToPixelData(PixelFormat.Bgra32);
        var target = new byte[width * height * 4];
        decoded.CopyPixelDataTo(target, PixelFormat.Bgra32);
        Size size = decoded.Size;
        using Image fromRgba = Image.LoadPixelData(rgba, width, height, PixelFormat.Rgba32);
        byte[] lossless = fromRgba.Encode(new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image allFrames = Image.Load(data);
        PictureSharp.Formats.WebP.WebPConverter.Convert(P("input.png"), P("output.webp"), new() { Quality = 80 });
        await Assert.That(target).IsEquivalentTo(bgraDecoded, CollectionOrdering.Matching);
        await Assert.That(size).IsEqualTo(image.Size);
        using Image roundTrip = Image.Load(lossless, new DecoderOptions { FirstFrameOnly = true });
        await Assert.That(roundTrip.ToPixelData()).IsEquivalentTo(rgba, CollectionOrdering.Matching);
        await Assert.That(rgba).IsEquivalentTo(image.ToPixelData(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task SequentialAnimation_DocumentationExample()
    {
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(P("animated.webp"));
        await using ImageFrameWriter writer = Image.CreateFrameWriter(
            P("sequential.png"), reader.Size, ImageFormat.Png, ImageFrameWriterOptions.From(reader));
        int count = 0;
        await foreach (ImageFrame frame in reader.ReadFramesAsync())
        {
            await writer.WriteAsync(frame);
            count++;
        }
        await writer.CompleteAsync();
        using Image actual = Image.Load(P("sequential.png"));
        using Image expected = Image.Load(P("animated.webp"));
        await Assert.That(count).IsEqualTo(expected.FrameCount);
        await Assert.That(actual.LoopCount).IsEqualTo(expected.LoopCount);
        for (int i = 0; i < count; i++)
        {
            await Assert.That(actual.Frames[i].GetPixelSpan().SequenceEqual(expected.Frames[i].GetPixelSpan())).IsTrue();
            await Assert.That(actual.Frames[i].Duration).IsEqualTo(expected.Frames[i].Duration);
        }
    }

    [Test]
    public async Task SequentialAnimation_StreamingResizeExample()
    {
        await using (ImageFrameReader reader = await Image.OpenFrameReaderAsync(P("animated.webp")))
        await using (ImageFrameWriter writer = Image.CreateFrameWriter(
            P("small.webp"), new Size(16, 12), ImageFrameWriterOptions.From(reader)))
        {
            await foreach (ImageFrame frame in reader.ReadFramesAsync())
            {
                using Image image = reader.CloneCurrent();
                image.Mutate(x => x.Resize(16, 12));
                await writer.WriteAsync(image, frame.Duration);
            }
            await writer.CompleteAsync();
        }

        using Image expected = Image.Load(P("animated.webp"));
        using Image actual = Image.Load(P("small.webp"));
        await Assert.That(actual.Size).IsEqualTo(new Size(16, 12));
        await Assert.That(actual.FrameCount).IsEqualTo(expected.FrameCount);
        await Assert.That(actual.LoopCount).IsEqualTo(expected.LoopCount);
        for (int i = 0; i < expected.FrameCount; i++)
            await Assert.That(actual.Frames[i].Duration).IsEqualTo(expected.Frames[i].Duration);
    }

    // ------------------------------------------------------------ animation.md

    [Test]
    public async Task Animation()
    {
        using (var animation = Image.Load(P("animated.webp")))
        {
            await Assert.That(animation.IsAnimated).IsTrue();
            foreach (ImageFrame frame in animation.Frames)
            {
                await Assert.That(frame.Duration > TimeSpan.Zero).IsTrue();
            }

            using Image third = animation.Frames.CloneFrame(2);
            third.SaveAsPng(P("frame-3.png"));
            await Assert.That(third.FrameCount).IsEqualTo(1);
        }

        using var first = Image.Load(P("frame-1.png"));
        using var second = Image.Load(P("frame-2.png"));
        using var built = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), first, second);
        built.LoopCount = 0;
        built.Frames[1].Duration = TimeSpan.FromMilliseconds(150);
        built.SaveAsWebP(P("animation.webp"), new() { Lossless = true, Exact = true, MinimizeSize = true });

        using Image back = Image.Load(P("animation.webp"));
        await Assert.That(back.FrameCount).IsEqualTo(2);
        await Assert.That(back.Frames[1].Duration).IsEqualTo(TimeSpan.FromMilliseconds(150));
        await Assert.That(back.GetPixelSpan().ToArray()).IsEquivalentTo(first.GetPixelSpan().ToArray(), CollectionOrdering.Matching);

        using Image thirdImage = first.Clone(x => x.Grayscale());
        built.Frames.Add(thirdImage, TimeSpan.FromMilliseconds(80));
        built.Frames.Insert(0, new ImageFrame(built.Width, built.Height) { Duration = TimeSpan.FromMilliseconds(40) });
        built.Frames.Move(0, 2);
        built.Frames.RemoveAt(1);
        using Image extracted = built.Frames.ExportFrame(0);
        await Assert.That(built.FrameCount).IsEqualTo(2);

        built.Mutate(x => x.Resize(32, 0).Saturate(1.2f));
        foreach (var f in built.Frames)
        {
            await Assert.That(f.Width).IsEqualTo(32);
        }

        built.SaveAsPng(P("animation.png"));
        built.SaveAsPng(P("poster.png"), new() { SaveAnimation = false });
        await Assert.That(Image.Identify(P("animation.png")).FrameCount).IsEqualTo(2);
        await Assert.That(Image.Identify(P("poster.png")).FrameCount).IsEqualTo(1);

        built.SaveAsGif(P("animation.gif"));
        built.SaveAsGif(P("small.gif"), new() { ColorCount = 64, Dither = GifDither.FloydSteinberg });
        await Assert.That(Image.Identify(P("animation.gif")).FrameCount).IsEqualTo(2);

        PictureSharp.Formats.WebP.WebPConverter.Convert(P("animation.gif"), P("animation.webp"));
        using (var gif = Image.Load(P("animation.gif")))
        {
            gif.SaveAsWebP(P("lossy.webp"), new() { Quality = 80, KeyframeMin = 3, KeyframeMax = 5 });
        }

        using (var webp = Image.Load(P("animated.webp")))
        {
            webp.SaveAsGif(P("animated.gif"));
        }

        using Image converted = Image.Load(P("animation.webp"));
        using Image gifBack = Image.Load(P("animation.gif"));
        await Assert.That(converted.FrameCount).IsEqualTo(gifBack.FrameCount);
        await Assert.That(converted.GetPixelSpan().ToArray()).IsEquivalentTo(gifBack.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        await Assert.That(Image.Identify(P("animated.gif")).FrameCount).IsEqualTo(Image.Identify(P("animated.webp")).FrameCount);
    }

    // ------------------------------------------------------------ metadata.md

    [Test]
    public async Task Metadata()
    {
        using var photo = Image.Load(P("photo.jpg"));
        photo.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        photo.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        photo.SaveAsJpeg(P("tagged.jpg"));

        using var reloaded = Image.Load(P("tagged.jpg"));
        ImageMetadata meta = reloaded.Metadata;
        await Assert.That(meta.Orientation).IsEqualTo(ExifOrientation.RightTop);
        Assert.NotNull(meta.Xmp);

        using var upright = Image.Load(P("tagged.jpg"), new DecoderOptions { ApplyExifOrientation = true });
        reloaded.Mutate(x => x.AutoOrient());
        await Assert.That(reloaded.Metadata.Orientation).IsEqualTo(ExifOrientation.TopLeft);
        await Assert.That(reloaded.Size).IsEqualTo(upright.Size);
        await Assert.That((upright.Width, upright.Height)).IsEqualTo((photo.Height, photo.Width));

        reloaded.SaveAsWebP(P("share.webp"), new() { Metadata = MetadataKinds.Icc });
        reloaded.SaveAsJpeg(P("clean.jpg"), new() { Metadata = MetadataKinds.None });
        using (Image clean = Image.Load(P("clean.jpg")))
        using (Image shared = Image.Load(P("share.webp")))
        {
            await Assert.That(clean.Metadata.IsEmpty).IsTrue();
            Assert.Null(shared.Metadata.Exif);
        }

        meta.HorizontalResolution = 300;
        meta.VerticalResolution = 300;
        meta.Xmp = null;
        meta.Clear();
        await Assert.That(meta.IsEmpty).IsTrue();
        await Assert.That(meta.HorizontalResolution).IsEqualTo(300f);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => meta.HorizontalResolution = 0);
    }

    [Test]
    public async Task Metadata_GpsPosition()
    {
        var metadata = new ImageMetadata();
        metadata.ExifProfile.SetGpsLocation(new GpsLocation(-33.8688, 151.2093, altitude: 58));
        await Assert.That(metadata.ExifProfile.TryGetGpsLocation(out GpsLocation location)).IsTrue();
        await Assert.That($"{location.Latitude:F4}, {location.Longitude:F4}, {location.Altitude} m").IsEqualTo(
            string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{-33.8688:F4}, {151.2093:F4}, {58.0} m"));
        bool removed = metadata.ExifProfile.RemoveGps();
        await Assert.That(removed).IsTrue();
        await Assert.That(metadata.ExifProfile.TryGetGpsLocation(out _)).IsFalse();
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
