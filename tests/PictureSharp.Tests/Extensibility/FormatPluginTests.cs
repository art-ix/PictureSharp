using PictureSharp.Formats;
using PictureSharp.Formats.Png;

namespace PictureSharp.Tests.Extensibility;

public class FormatPluginTests
{
    private static readonly ImageConfiguration WithPpm = ImageConfiguration.Default.ToBuilder().AddFormat(new PpmFormat()).Build();

    private static Image Sample(int width = 5, int height = 3)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan<Rgba32>();
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

    [Test]
    public async Task AddedFormat_IsDetectedAfterBuiltIns_AndDecodes()
    {
        using Image source = Sample();
        byte[] ppm = EncodePpm(source, new PpmEncoderOptions { Comment = "written by a plugin" });
        var options = new DecoderOptions { Configuration = WithPpm };

        await Assert.That(WithPpm.Formats[^1]).IsSameReferenceAs(PpmFormat.Format);
        await Assert.That(WithPpm.CanDecode(PpmFormat.Format) && WithPpm.CanEncode(PpmFormat.Format)).IsTrue();
        await Assert.That(WithPpm.FromExtension("photo.pnm")).IsSameReferenceAs(PpmFormat.Format);
        await Assert.That(WithPpm.FromMimeType("IMAGE/X-PORTABLE-PIXMAP")).IsSameReferenceAs(PpmFormat.Format);

        using Image image = Image.Load(ppm, options);
        await Assert.That(image.SourceFormat).IsSameReferenceAs(PpmFormat.Format);
        await Assert.That(image.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(source.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        using Image fromStream = Image.Load(new MemoryStream(ppm), options);
        await Assert.That(fromStream.Size).IsEqualTo(source.Size);
        ImageInfo info = Image.Identify(ppm, options);
        await Assert.That((info.Format, info.Width, info.Height)).IsEqualTo((PpmFormat.Format, 5, 3));
        await Assert.That(info.ContainerItemCount).IsEqualTo(1);   // passed through unchanged
        await Assert.That(Image.Identify(source.ToPng()).ContainerItemCount).IsNull();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = info with { ContainerItemCount = -1 });

        // Not part of the default configuration.
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(ppm));
    }

    [Test]
    public async Task PluginOutput_GetsTargetSizeLikeBuiltInFormats()
    {
        using Image source = Sample(8, 4);
        var resize = new DecoderOptions { TargetSize = new(2, 2), Configuration = WithPpm };
        using Image fromPpm = Image.Load(EncodePpm(source), resize);
        using Image fromPng = Image.Load(source.ToPng(), resize);
        await Assert.That(fromPpm.Size).IsEqualTo(fromPng.Size);
        await Assert.That(fromPpm.Size).IsEqualTo(new System.Drawing.Size(2, 1));
    }

    [Test]
    public async Task PluginDecodes_AreLimitedAndNormalized()
    {
        using Image source = Sample();
        byte[] ppm = EncodePpm(source);

        var reserved = new DecoderOptions { Configuration = WithPpm, MaxPixels = 10 };
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(ppm, reserved));

        // A plugin that forgets to reserve pixels is still held to the limits after decoding.
        ImageConfiguration careless = ImageConfiguration.Default.ToBuilder().AddFormat(new PpmFormat(reservePixels: false)).Build();
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(ppm, new DecoderOptions { Configuration = careless, MaxPixels = 10 }));

        var options = new DecoderOptions { Configuration = WithPpm };
        var truncated = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(ppm[..^4], options));
        await Assert.That(truncated.Format).IsSameReferenceAs(PpmFormat.Format);
        await Assert.That(truncated.InnerException).IsTypeOf<ArgumentOutOfRangeException>();
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load("P6 x 1 255 "u8.ToArray(), options));
        Assert.ThrowsExactly<NotSupportedException>(() => Image.Load("P6 1 1 65535 \0\0\0\0\0\0"u8.ToArray(), options));
        Assert.ThrowsExactly<OperationCanceledException>(() => Image.Load(ppm, options, new CancellationToken(canceled: true)));
    }

    [Test]
    public void FailingSignatureCheck_DoesNotEscapeDetection()
    {
        ImageConfiguration naive = ImageConfiguration.Default.ToBuilder().AddFormat(new NaiveSignatureFormat()).Build();
        var options = new DecoderOptions { Configuration = naive };
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(new byte[] { 1, 2 }, options));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(new byte[] { 1, 2 }, options));
    }

    [Test]
    public async Task ContainerPlugin_DecodesEmbeddedImages_WithSharedLimits()
    {
        using Image source = Sample();
        byte[] wrapped = WrapFormat.Wrap(source.ToPng());
        ImageConfiguration withWrap = ImageConfiguration.Default.ToBuilder().AddFormat(new WrapFormat()).Build();

        using Image image = Image.Load(wrapped, new DecoderOptions { Configuration = withWrap });
        await Assert.That(image.SourceFormat).IsSameReferenceAs(WrapFormat.Format);
        await Assert.That(image.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(source.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        await Assert.That(withWrap.CanEncode(WrapFormat.Format)).IsFalse();

        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(wrapped, new DecoderOptions { Configuration = withWrap, MaxPixels = 10 }));
        ImageConfiguration withoutPng = withWrap.ToBuilder().RemoveFormat(ImageFormat.Png).Build();
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(wrapped, new DecoderOptions { Configuration = withoutPng }));
    }

    [Test]
    public async Task ReplaceFormat_SwapsCodecs_AndKeepsDetectionOrder()
    {
        using Image source = Sample();
        ImageConfiguration replaced = ImageConfiguration.Default.ToBuilder().ReplaceFormat(new StubFormat(ImageFormat.Png)).Build();
        await Assert.That(replaced.Formats).IsEquivalentTo(ImageConfiguration.Default.Formats, CollectionOrdering.Matching);
        using Image image = Image.Load(source.ToPng(), new DecoderOptions { Configuration = replaced });
        await Assert.That((image.Width, image.Height, image.SourceFormat)).IsEqualTo((1, 1, ImageFormat.Png));
        await Assert.That(replaced.CanEncode(ImageFormat.Png)).IsFalse();
        Assert.ThrowsExactly<ArgumentException>(() => ImageConfiguration.Default.ToBuilder().ReplaceFormat(new PpmFormat()));
    }

    [Test]
    public async Task AddFormat_RejectsInvalidOrConflictingModules()
    {
        ImageConfigurationBuilder builder = ImageConfiguration.Default.ToBuilder().AddFormat(new PpmFormat());
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddFormat(null!));
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddFormat(new PpmFormat()));
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddFormat(new StubFormat(ImageFormat.Create("png", "image/x-other", ".other"))));
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddFormat(new StubFormat(ImageFormat.Create("Other", "Image/PNG", ".other"))));
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddFormat(new StubFormat(ImageFormat.Create("Other", "image/x-other", ".other", ".jpg"))));
        ImageFormat fresh = ImageFormat.Create("Fresh", "image/x-fresh", ".fresh");
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddFormat(new StubFormat(fresh, headerSize: 0)));
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddFormat(new StubFormat(fresh, headerSize: 4097)));
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddFormat(new StubFormat(fresh, decoder: false)));
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddFormat(new StubFormat(fresh, encoder: new PpmFormat().Encoder)));
        await Assert.That(builder.Formats.Count).IsEqualTo(8);
        builder.AddFormat(new StubFormat(fresh));
        await Assert.That(builder.Build().Formats[^1]).IsSameReferenceAs(fresh);
    }

    [Test]
    public async Task Registration_SnapshotsModuleMembers()
    {
        var module = new PpmFormat();
        ImageConfiguration configuration = ImageConfiguration.Default.ToBuilder().AddFormat(module).Build();
        module.Decoder = null;
        module.Encoder = null;
        using Image source = Sample();
        using Image image = Image.Load(EncodePpm(source), new DecoderOptions { Configuration = configuration });
        await Assert.That(configuration.CanEncode(PpmFormat.Format)).IsTrue();
    }

    [Test]
    public async Task TypedEncoder_RejectsOptionsOfAnotherFormat()
    {
        using Image source = Sample();
        IImageFormatEncoder encoder = new PpmFormat().Encoder!;
        await Assert.That(encoder.CreateDefaultOptions()).IsTypeOf<PpmEncoderOptions>();
        Assert.ThrowsExactly<ArgumentException>(() => encoder.Encode(source, Stream.Null, new PngEncoderOptions(), default));
    }

    [Test]
    public async Task ImageFormatCreate_ValidatesAndNormalizes()
    {
        ImageFormat format = ImageFormat.Create(" TGA ", "Image/X-TGA", ".TGA", ".vda");
        await Assert.That((format.Name, format.MimeType)).IsEqualTo(("TGA", "image/x-tga"));
        await Assert.That(format.FileExtensions).IsEquivalentTo([".tga", ".vda"], CollectionOrdering.Matching);
        await Assert.That(ImageFormat.Create("TGA", "image/x-tga", ".tga")).IsNotSameReferenceAs(format);

        Assert.ThrowsExactly<ArgumentException>(() => ImageFormat.Create(" ", "image/x", ".x"));
        Assert.ThrowsExactly<ArgumentNullException>(() => ImageFormat.Create(null!, "image/x", ".x"));
        Assert.ThrowsExactly<ArgumentException>(() => ImageFormat.Create("X", "image", ".x"));
        Assert.ThrowsExactly<ArgumentException>(() => ImageFormat.Create("X", "image/", ".x"));
        Assert.ThrowsExactly<ArgumentException>(() => ImageFormat.Create("X", "image/x"));
        Assert.ThrowsExactly<ArgumentException>(() => ImageFormat.Create("X", "image/x", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => ImageFormat.Create("X", "image/x", "."));
        Assert.ThrowsExactly<ArgumentException>(() => ImageFormat.Create("X", "image/x", ".a/b"));
        Assert.ThrowsExactly<ArgumentException>(() => ImageFormat.Create("X", "image/x", ".x", ".X"));
        Assert.ThrowsExactly<ArgumentNullException>(() => ImageFormat.Create("X", "image/x", ".x", null!));
    }
}

/// <summary>Registers a plugin in the process-wide default, so it must not run alongside other tests.</summary>
[NotInParallel]
public class FormatPluginDefaultTests
{
    [Test]
    public async Task PluginInDefault_WorksThroughImageShortcuts()
    {
        ImageConfiguration original = ImageConfiguration.Default;
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var source = new Image(4, 2, new Rgba32(10, 20, 30));
        try
        {
            ImageConfiguration.Default = original.ToBuilder().AddFormat(new PpmFormat()).Build();
            await Assert.That(ImageFormat.All).Contains(PpmFormat.Format);
            await Assert.That(ImageFormat.FromExtension("x.ppm")).IsSameReferenceAs(PpmFormat.Format);
            await Assert.That(PpmFormat.Format.CanDecode && PpmFormat.Format.CanEncode).IsTrue();

            string path = Path.Combine(directory, "a.ppm");
            source.Save(path);
            await Assert.That(Image.DetectFormat(path)).IsSameReferenceAs(PpmFormat.Format);
            await Assert.That(Image.Identify(path).Width).IsEqualTo(4);
            using (Image loaded = Image.Load(path))
            {
                await Assert.That(loaded.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(source.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
            }

            source.SaveAsPpm(Path.Combine(directory, "b.ppm"), new PpmEncoderOptions { Comment = "shortcut" });
            using (Image loaded = await Image.LoadAsync(Path.Combine(directory, "b.ppm")))
            {
                await Assert.That(loaded.SourceFormat).IsSameReferenceAs(PpmFormat.Format);
            }

            await Assert.That(source.Encode(PpmFormat.Format)).IsEquivalentTo(source.ToPpm(), CollectionOrdering.Matching);
            using var stream = new MemoryStream();
            await source.SaveAsync(stream, PpmFormat.Format);
            await Assert.That(Image.DetectFormat(stream.ToArray())).IsSameReferenceAs(PpmFormat.Format);
        }
        finally
        {
            ImageConfiguration.Default = original;
            Directory.Delete(directory, recursive: true);
        }

        Assert.ThrowsExactly<NotSupportedException>(() => source.Encode(PpmFormat.Format));
    }
}
