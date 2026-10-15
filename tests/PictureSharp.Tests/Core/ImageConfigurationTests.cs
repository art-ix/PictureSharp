using PictureSharp.Formats.Png;
using PictureSharp.Tests.Bmp;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class ImageConfigurationTests
{
    private static readonly string BmpFile = "bmp/im_rgb24.bmp";
    private static readonly string PngFile = "png/odd_rgba.png";

    [Fact]
    public void Default_ContainsBuiltInFormats_InDetectionOrder()
    {
        ImageConfiguration configuration = ImageConfiguration.Default;
        Assert.Equal(["WebP", "PNG", "JPEG", "BMP", "GIF", "PCX", "ICO"], configuration.Formats.Select(f => f.Name));
        Assert.All(configuration.Formats, f => Assert.True(configuration.CanDecode(f) && configuration.CanEncode(f)));
        Assert.Same(ImageFormat.Png, configuration.FromExtension("photo.PNG"));
        Assert.Same(ImageFormat.WebP, configuration.FromMimeType("image/webp"));
        Assert.Equal("ImageConfiguration { Formats = WebP, PNG, JPEG, BMP, GIF, PCX, ICO }", configuration.ToString());
    }

    [Fact]
    public void RestrictedConfiguration_DecodesOnlyItsFormats()
    {
        ImageConfiguration uploads = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png, ImageFormat.Jpeg).Build();
        var options = new DecoderOptions { Configuration = uploads };
        byte[] bmp = Corpus.Read(BmpFile);

        using (Image png = Image.Load(Corpus.Read(PngFile), options))
        {
            Assert.Same(ImageFormat.Png, png.SourceFormat);
        }

        var error = Assert.Throws<ImageFormatException>(() => Image.Load(bmp, options));
        Assert.Contains("expected PNG, JPEG", error.Message);
        Assert.Throws<ImageFormatException>(() => Image.Load(Corpus.PathOf(BmpFile), options));
        Assert.Throws<ImageFormatException>(() => Image.Load(bmp.AsSpan(), options, CancellationToken.None));
        Assert.Throws<ImageFormatException>(() => Image.Identify(bmp, options));
        Assert.Throws<ImageFormatException>(() => Image.Identify(new MemoryStream(bmp), options));
        Assert.False(uploads.CanDecode(ImageFormat.Bmp));
        Assert.Null(uploads.FromExtension(".bmp"));

        // The default configuration is not affected.
        using Image fromDefault = Image.Load(bmp);
        Assert.Same(ImageFormat.Bmp, fromDefault.SourceFormat);
    }

    [Fact]
    public async Task RestrictedConfiguration_AppliesToAsyncLoadAndIdentify()
    {
        ImageConfiguration onlyPng = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png).Build();
        var options = new DecoderOptions { Configuration = onlyPng };
        await Assert.ThrowsAsync<ImageFormatException>(() => Image.LoadAsync(Corpus.PathOf(BmpFile), options));
        await Assert.ThrowsAsync<ImageFormatException>(() => Image.LoadAsync(new MemoryStream(Corpus.Read(BmpFile)), options));
        await Assert.ThrowsAsync<ImageFormatException>(() => Image.IdentifyAsync(Corpus.PathOf(BmpFile), options));
        ImageInfo info = await Image.IdentifyAsync(new MemoryStream(Corpus.Read(PngFile)), options);
        Assert.Same(ImageFormat.Png, info.Format);
    }

    [Fact]
    public void Builder_KeepsDetectionOrder_AndLeavesSourceUnchanged()
    {
        ImageConfiguration source = ImageConfiguration.Default;
        ImageConfigurationBuilder builder = source.ToBuilder().KeepOnly(ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Jpeg);
        Assert.Equal([ImageFormat.Png, ImageFormat.Jpeg], builder.Formats);
        ImageConfiguration built = builder.RemoveFormat(ImageFormat.Gif).Build();
        Assert.Equal([ImageFormat.Png, ImageFormat.Jpeg], built.Formats);
        Assert.Equal(7, source.Formats.Count);

        // A built configuration is a snapshot of the builder.
        builder.RemoveFormat(ImageFormat.Png);
        Assert.Equal([ImageFormat.Png, ImageFormat.Jpeg], built.Formats);
        Assert.Equal([ImageFormat.Jpeg], builder.Build().Formats);
    }

    [Fact]
    public void Builder_RejectsInvalidRequests()
    {
        ImageConfigurationBuilder builder = ImageConfiguration.Default.ToBuilder();
        Assert.Throws<ArgumentException>(() => builder.KeepOnly());
        Assert.Throws<ArgumentNullException>(() => builder.KeepOnly(ImageFormat.Png, null!));
        Assert.Throws<ArgumentNullException>(() => builder.RemoveFormat(null!));
        builder.RemoveFormat(ImageFormat.Pcx).RemoveFormat(ImageFormat.Pcx);
        Assert.Throws<ArgumentException>(() => builder.KeepOnly(ImageFormat.Pcx));
        foreach (ImageFormat format in builder.Formats)
        {
            builder.RemoveFormat(format);
        }

        Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Throws<ArgumentNullException>(() => ImageConfiguration.Default.CanDecode(null!));
    }

    [Fact]
    public void EmbeddedFormat_MissingFromConfiguration_IsNotSupported()
    {
        using var source = new Image(3, 2, new Rgba32(10, 20, 30, 128));
        byte[] png = source.ToPng();
        byte[] bmp = new BmpBuilder { Width = 3, Height = 2, Bits = 0, Compression = 5, Data = png, ImageSize = (uint)png.Length }.Build();
        ImageConfiguration withoutPng = ImageConfiguration.Default.ToBuilder().RemoveFormat(ImageFormat.Png).Build();

        Assert.Throws<NotSupportedException>(() => Image.Load(bmp, new DecoderOptions { Configuration = withoutPng }));
        using Image decoded = Image.Load(bmp);
        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    [Fact]
    public void DecoderOptions_CopiesKeepConfiguration()
    {
        ImageConfiguration onlyPng = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png).Build();
        var options = new DecoderOptions { Configuration = onlyPng };
        Assert.Same(onlyPng, (options with { MaxPixels = 10 }).Configuration);
        Assert.Null(DecoderOptions.Default.Configuration);
    }
}

/// <summary>Replaces the process-wide default, so it must not run alongside other tests.</summary>
[Collection(nameof(SerialCollection))]
public class ImageConfigurationDefaultTests
{
    [Fact]
    public void ReplacingDefault_ChangesLoadSaveAndLookup()
    {
        ImageConfiguration original = ImageConfiguration.Default;
        byte[] bmp = Corpus.Read("bmp/im_rgb24.bmp");
        using var image = new Image(2, 2, new Rgba32(1, 2, 3));
        try
        {
            ImageConfiguration.Default = original.ToBuilder().RemoveFormat(ImageFormat.Bmp).Build();
            Assert.Throws<ImageFormatException>(() => Image.Load(bmp));
            Assert.Null(Image.DetectFormat(bmp));
            Assert.DoesNotContain(ImageFormat.Bmp, ImageFormat.All);
            Assert.Null(ImageFormat.FromExtension("x.bmp"));
            Assert.False(ImageFormat.Bmp.CanDecode);
            Assert.False(ImageFormat.Bmp.CanEncode);
            Assert.True(ImageFormat.Png.CanEncode);
            Assert.Throws<NotSupportedException>(() => image.Save(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bmp")));
            Assert.Throws<NotSupportedException>(() => image.Encode(ImageFormat.Bmp));

            // An explicit configuration still wins over the default.
            using Image explicitBmp = Image.Load(bmp, new DecoderOptions { Configuration = original });
            Assert.Same(ImageFormat.Bmp, explicitBmp.SourceFormat);
        }
        finally
        {
            ImageConfiguration.Default = original;
        }

        using Image restored = Image.Load(bmp);
        Assert.Same(ImageFormat.Bmp, restored.SourceFormat);
    }

    [Fact]
    public void Default_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => ImageConfiguration.Default = null!);

    [Fact]
    public async Task ReplacingDefault_WhileLoading_UsesCompleteSnapshots()
    {
        ImageConfiguration original = ImageConfiguration.Default;
        ImageConfiguration narrowed = original.ToBuilder().KeepOnly(ImageFormat.Png, ImageFormat.Bmp).Build();
        byte[] png = Corpus.Read("png/odd_rgba.png");
        using Image expected = Image.Load(png);
        Rgba32[] expectedPixels = expected.GetPixelSpan().ToArray();
        using var stop = new CancellationTokenSource();
        Task swapper = Task.Run(() =>
        {
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                ImageConfiguration.Default = i % 2 == 0 ? narrowed : original;
            }
        });
        try
        {
            Parallel.For(0, 200, _ =>
            {
                using Image image = Image.Load(png);
                Assert.Equal(expectedPixels, image.GetPixelSpan().ToArray());
            });
        }
        finally
        {
            stop.Cancel();
            await swapper;
            ImageConfiguration.Default = original;
        }
    }
}
