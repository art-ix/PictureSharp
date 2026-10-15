using PictureSharp.Formats;
using PictureSharp.Formats.Png;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Extensibility;

public class FormatPluginTests
{
    private static readonly ImageConfiguration WithPpm = ImageConfiguration.Default.ToBuilder().AddFormat(new PpmFormat()).Build();

    private static Image Sample(int width = 5, int height = 3)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)(i * 17), (byte)(255 - i * 9), (byte)(i * 5));
        }

        return image;
    }

    private static byte[] EncodePpm(Image image, PpmEncoderOptions? options = null)
    {
        using var output = new MemoryStream();
        new PpmFormat().Encoder!.Encode(image, output, options ?? new PpmEncoderOptions(), default);
        return output.ToArray();
    }

    [Fact]
    public void AddedFormat_IsDetectedAfterBuiltIns_AndDecodes()
    {
        using Image source = Sample();
        byte[] ppm = EncodePpm(source, new PpmEncoderOptions { Comment = "written by a plugin" });
        var options = new DecoderOptions { Configuration = WithPpm };

        Assert.Same(PpmFormat.Format, WithPpm.Formats[^1]);
        Assert.True(WithPpm.CanDecode(PpmFormat.Format) && WithPpm.CanEncode(PpmFormat.Format));
        Assert.Same(PpmFormat.Format, WithPpm.FromExtension("photo.pnm"));
        Assert.Same(PpmFormat.Format, WithPpm.FromMimeType("IMAGE/X-PORTABLE-PIXMAP"));

        using Image image = Image.Load(ppm, options);
        Assert.Same(PpmFormat.Format, image.SourceFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), image.GetPixelSpan().ToArray());
        using Image fromStream = Image.Load(new MemoryStream(ppm), options);
        Assert.Equal(source.Size, fromStream.Size);
        ImageInfo info = Image.Identify(ppm, options);
        Assert.Equal((PpmFormat.Format, 5, 3), (info.Format, info.Width, info.Height));

        // Not part of the default configuration.
        Assert.Throws<ImageFormatException>(() => Image.Load(ppm));
    }

    [Fact]
    public void PluginOutput_GetsTargetSizeLikeBuiltInFormats()
    {
        using Image source = Sample(8, 4);
        var resize = new DecoderOptions { TargetSize = new(2, 2), Configuration = WithPpm };
        using Image fromPpm = Image.Load(EncodePpm(source), resize);
        using Image fromPng = Image.Load(source.ToPng(), resize);
        Assert.Equal(fromPng.Size, fromPpm.Size);
        Assert.Equal(new System.Drawing.Size(2, 1), fromPpm.Size);
    }

    [Fact]
    public void PluginDecodes_AreLimitedAndNormalized()
    {
        using Image source = Sample();
        byte[] ppm = EncodePpm(source);

        var reserved = new DecoderOptions { Configuration = WithPpm, MaxPixels = 10 };
        Assert.Throws<ImageFormatException>(() => Image.Load(ppm, reserved));

        // A plugin that forgets to reserve pixels is still held to the limits after decoding.
        ImageConfiguration careless = ImageConfiguration.Default.ToBuilder().AddFormat(new PpmFormat(reservePixels: false)).Build();
        Assert.Throws<ImageFormatException>(() => Image.Load(ppm, new DecoderOptions { Configuration = careless, MaxPixels = 10 }));

        var options = new DecoderOptions { Configuration = WithPpm };
        var truncated = Assert.Throws<ImageFormatException>(() => Image.Load(ppm[..^4], options));
        Assert.Same(PpmFormat.Format, truncated.Format);
        Assert.IsType<ArgumentOutOfRangeException>(truncated.InnerException);
        Assert.Throws<ImageFormatException>(() => Image.Load("P6 x 1 255 "u8.ToArray(), options));
        Assert.Throws<NotSupportedException>(() => Image.Load("P6 1 1 65535 \0\0\0\0\0\0"u8.ToArray(), options));
        Assert.Throws<OperationCanceledException>(() => Image.Load(ppm, options, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void FailingSignatureCheck_DoesNotEscapeDetection()
    {
        ImageConfiguration naive = ImageConfiguration.Default.ToBuilder().AddFormat(new NaiveSignatureFormat()).Build();
        var options = new DecoderOptions { Configuration = naive };
        Assert.Throws<ImageFormatException>(() => Image.Load(new byte[] { 1, 2 }, options));
        Assert.Throws<ImageFormatException>(() => Image.Identify(new byte[] { 1, 2 }, options));
    }

    [Fact]
    public void ContainerPlugin_DecodesEmbeddedImages_WithSharedLimits()
    {
        using Image source = Sample();
        byte[] wrapped = WrapFormat.Wrap(source.ToPng());
        ImageConfiguration withWrap = ImageConfiguration.Default.ToBuilder().AddFormat(new WrapFormat()).Build();

        using Image image = Image.Load(wrapped, new DecoderOptions { Configuration = withWrap });
        Assert.Same(WrapFormat.Format, image.SourceFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), image.GetPixelSpan().ToArray());
        Assert.False(withWrap.CanEncode(WrapFormat.Format));

        Assert.Throws<ImageFormatException>(() => Image.Load(wrapped, new DecoderOptions { Configuration = withWrap, MaxPixels = 10 }));
        ImageConfiguration withoutPng = withWrap.ToBuilder().RemoveFormat(ImageFormat.Png).Build();
        Assert.Throws<ImageFormatException>(() => Image.Load(wrapped, new DecoderOptions { Configuration = withoutPng }));
    }

    [Fact]
    public void ReplaceFormat_SwapsCodecs_AndKeepsDetectionOrder()
    {
        using Image source = Sample();
        ImageConfiguration replaced = ImageConfiguration.Default.ToBuilder().ReplaceFormat(new StubFormat(ImageFormat.Png)).Build();
        Assert.Equal(ImageConfiguration.Default.Formats, replaced.Formats);
        using Image image = Image.Load(source.ToPng(), new DecoderOptions { Configuration = replaced });
        Assert.Equal((1, 1, ImageFormat.Png), (image.Width, image.Height, image.SourceFormat));
        Assert.False(replaced.CanEncode(ImageFormat.Png));
        Assert.Throws<ArgumentException>(() => ImageConfiguration.Default.ToBuilder().ReplaceFormat(new PpmFormat()));
    }

    [Fact]
    public void AddFormat_RejectsInvalidOrConflictingModules()
    {
        ImageConfigurationBuilder builder = ImageConfiguration.Default.ToBuilder().AddFormat(new PpmFormat());
        Assert.Throws<ArgumentNullException>(() => builder.AddFormat(null!));
        Assert.Throws<ArgumentException>(() => builder.AddFormat(new PpmFormat()));
        Assert.Throws<ArgumentException>(() => builder.AddFormat(new StubFormat(ImageFormat.Create("png", "image/x-other", ".other"))));
        Assert.Throws<ArgumentException>(() => builder.AddFormat(new StubFormat(ImageFormat.Create("Other", "Image/PNG", ".other"))));
        Assert.Throws<ArgumentException>(() => builder.AddFormat(new StubFormat(ImageFormat.Create("Other", "image/x-other", ".other", ".jpg"))));
        ImageFormat fresh = ImageFormat.Create("Fresh", "image/x-fresh", ".fresh");
        Assert.Throws<ArgumentException>(() => builder.AddFormat(new StubFormat(fresh, headerSize: 0)));
        Assert.Throws<ArgumentException>(() => builder.AddFormat(new StubFormat(fresh, headerSize: 4097)));
        Assert.Throws<ArgumentException>(() => builder.AddFormat(new StubFormat(fresh, decoder: false)));
        Assert.Throws<ArgumentException>(() => builder.AddFormat(new StubFormat(fresh, encoder: new PpmFormat().Encoder)));
        Assert.Equal(8, builder.Formats.Count);
        builder.AddFormat(new StubFormat(fresh));
        Assert.Same(fresh, builder.Build().Formats[^1]);
    }

    [Fact]
    public void Registration_SnapshotsModuleMembers()
    {
        var module = new PpmFormat();
        ImageConfiguration configuration = ImageConfiguration.Default.ToBuilder().AddFormat(module).Build();
        module.Decoder = null;
        module.Encoder = null;
        using Image source = Sample();
        using Image image = Image.Load(EncodePpm(source), new DecoderOptions { Configuration = configuration });
        Assert.True(configuration.CanEncode(PpmFormat.Format));
    }

    [Fact]
    public void TypedEncoder_RejectsOptionsOfAnotherFormat()
    {
        using Image source = Sample();
        IImageFormatEncoder encoder = new PpmFormat().Encoder!;
        Assert.IsType<PpmEncoderOptions>(encoder.CreateDefaultOptions());
        Assert.Throws<ArgumentException>(() => encoder.Encode(source, Stream.Null, new PngEncoderOptions(), default));
    }

    [Fact]
    public void ImageFormatCreate_ValidatesAndNormalizes()
    {
        ImageFormat format = ImageFormat.Create(" TGA ", "Image/X-TGA", ".TGA", ".vda");
        Assert.Equal(("TGA", "image/x-tga"), (format.Name, format.MimeType));
        Assert.Equal([".tga", ".vda"], format.FileExtensions);
        Assert.NotSame(format, ImageFormat.Create("TGA", "image/x-tga", ".tga"));

        Assert.Throws<ArgumentException>(() => ImageFormat.Create(" ", "image/x", ".x"));
        Assert.Throws<ArgumentNullException>(() => ImageFormat.Create(null!, "image/x", ".x"));
        Assert.Throws<ArgumentException>(() => ImageFormat.Create("X", "image", ".x"));
        Assert.Throws<ArgumentException>(() => ImageFormat.Create("X", "image/", ".x"));
        Assert.Throws<ArgumentException>(() => ImageFormat.Create("X", "image/x"));
        Assert.Throws<ArgumentException>(() => ImageFormat.Create("X", "image/x", "x"));
        Assert.Throws<ArgumentException>(() => ImageFormat.Create("X", "image/x", "."));
        Assert.Throws<ArgumentException>(() => ImageFormat.Create("X", "image/x", ".a/b"));
        Assert.Throws<ArgumentException>(() => ImageFormat.Create("X", "image/x", ".x", ".X"));
        Assert.Throws<ArgumentNullException>(() => ImageFormat.Create("X", "image/x", ".x", null!));
    }
}

/// <summary>Registers a plugin in the process-wide default, so it must not run alongside other tests.</summary>
[Collection(nameof(SerialCollection))]
public class FormatPluginDefaultTests
{
    [Fact]
    public async Task PluginInDefault_WorksThroughImageShortcuts()
    {
        ImageConfiguration original = ImageConfiguration.Default;
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var source = new Image(4, 2, new Rgba32(10, 20, 30));
        try
        {
            ImageConfiguration.Default = original.ToBuilder().AddFormat(new PpmFormat()).Build();
            Assert.Contains(PpmFormat.Format, ImageFormat.All);
            Assert.Same(PpmFormat.Format, ImageFormat.FromExtension("x.ppm"));
            Assert.True(PpmFormat.Format.CanDecode && PpmFormat.Format.CanEncode);

            string path = Path.Combine(directory, "a.ppm");
            source.Save(path);
            Assert.Same(PpmFormat.Format, Image.DetectFormat(path));
            Assert.Equal(4, Image.Identify(path).Width);
            using (Image loaded = Image.Load(path))
            {
                Assert.Equal(source.GetPixelSpan().ToArray(), loaded.GetPixelSpan().ToArray());
            }

            source.SaveAsPpm(Path.Combine(directory, "b.ppm"), new PpmEncoderOptions { Comment = "shortcut" });
            using (Image loaded = await Image.LoadAsync(Path.Combine(directory, "b.ppm")))
            {
                Assert.Same(PpmFormat.Format, loaded.SourceFormat);
            }

            Assert.Equal(source.ToPpm(), source.Encode(PpmFormat.Format));
            using var stream = new MemoryStream();
            await source.SaveAsync(stream, PpmFormat.Format);
            Assert.Same(PpmFormat.Format, Image.DetectFormat(stream.ToArray()));
        }
        finally
        {
            ImageConfiguration.Default = original;
            Directory.Delete(directory, recursive: true);
        }

        Assert.Throws<NotSupportedException>(() => source.Encode(PpmFormat.Format));
    }
}
