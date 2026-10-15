using PictureSharp.Formats.Png;
using PictureSharp.Tests.Bmp;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class ImageConfigurationTests
{
    private static readonly string BmpFile = "bmp/im_rgb24.bmp";
    private static readonly string PngFile = "png/odd_rgba.png";

    [Test]
    public async Task Default_ContainsBuiltInFormats_InDetectionOrder()
    {
        ImageConfiguration configuration = ImageConfiguration.Default;
        await Assert.That(configuration.Formats.Select(f => f.Name)).IsEquivalentTo(["WebP", "PNG", "JPEG", "BMP", "GIF", "PCX", "ICO"], CollectionOrdering.Matching);
        foreach (var f in configuration.Formats)
        {
            await Assert.That(configuration.CanDecode(f) && configuration.CanEncode(f)).IsTrue();
        }

        await Assert.That(configuration.FromExtension("photo.PNG")).IsSameReferenceAs(ImageFormat.Png);
        await Assert.That(configuration.FromMimeType("image/webp")).IsSameReferenceAs(ImageFormat.WebP);
        await Assert.That(configuration.ToString()).IsEqualTo("ImageConfiguration { Formats = WebP, PNG, JPEG, BMP, GIF, PCX, ICO }");
    }

    [Test]
    public async Task RestrictedConfiguration_DecodesOnlyItsFormats()
    {
        ImageConfiguration uploads = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png, ImageFormat.Jpeg).Build();
        var options = new DecoderOptions { Configuration = uploads };
        byte[] bmp = Corpus.Read(BmpFile);

        using (Image png = Image.Load(Corpus.Read(PngFile), options))
        {
            await Assert.That(png.SourceFormat).IsSameReferenceAs(ImageFormat.Png);
        }

        var error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(bmp, options));
        await Assert.That(error.Message).Contains("expected PNG, JPEG");
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(Corpus.PathOf(BmpFile), options));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(bmp.AsSpan(), options, CancellationToken.None));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(bmp, options));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(new MemoryStream(bmp), options));
        await Assert.That(uploads.CanDecode(ImageFormat.Bmp)).IsFalse();
        Assert.Null(uploads.FromExtension(".bmp"));

        // The default configuration is not affected.
        using Image fromDefault = Image.Load(bmp);
        await Assert.That(fromDefault.SourceFormat).IsSameReferenceAs(ImageFormat.Bmp);
    }

    [Test]
    public async Task RestrictedConfiguration_AppliesToAsyncLoadAndIdentify()
    {
        ImageConfiguration onlyPng = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png).Build();
        var options = new DecoderOptions { Configuration = onlyPng };
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.LoadAsync(Corpus.PathOf(BmpFile), options));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.LoadAsync(new MemoryStream(Corpus.Read(BmpFile)), options));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.IdentifyAsync(Corpus.PathOf(BmpFile), options));
        ImageInfo info = await Image.IdentifyAsync(new MemoryStream(Corpus.Read(PngFile)), options);
        await Assert.That(info.Format).IsSameReferenceAs(ImageFormat.Png);
    }

    [Test]
    public async Task Builder_KeepsDetectionOrder_AndLeavesSourceUnchanged()
    {
        ImageConfiguration source = ImageConfiguration.Default;
        ImageConfigurationBuilder builder = source.ToBuilder().KeepOnly(ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Jpeg);
        await Assert.That(builder.Formats).IsEquivalentTo([ImageFormat.Png, ImageFormat.Jpeg], CollectionOrdering.Matching);
        ImageConfiguration built = builder.RemoveFormat(ImageFormat.Gif).Build();
        await Assert.That(built.Formats).IsEquivalentTo([ImageFormat.Png, ImageFormat.Jpeg], CollectionOrdering.Matching);
        await Assert.That(source.Formats.Count).IsEqualTo(7);

        // A built configuration is a snapshot of the builder.
        builder.RemoveFormat(ImageFormat.Png);
        await Assert.That(built.Formats).IsEquivalentTo([ImageFormat.Png, ImageFormat.Jpeg], CollectionOrdering.Matching);
        await Assert.That(builder.Build().Formats).IsEquivalentTo([ImageFormat.Jpeg], CollectionOrdering.Matching);
    }

    [Test]
    public void Builder_RejectsInvalidRequests()
    {
        ImageConfigurationBuilder builder = ImageConfiguration.Default.ToBuilder();
        Assert.ThrowsExactly<ArgumentException>(() => builder.KeepOnly());
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.KeepOnly(ImageFormat.Png, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.RemoveFormat(null!));
        builder.RemoveFormat(ImageFormat.Pcx).RemoveFormat(ImageFormat.Pcx);
        Assert.ThrowsExactly<ArgumentException>(() => builder.KeepOnly(ImageFormat.Pcx));
        foreach (ImageFormat format in builder.Formats)
        {
            builder.RemoveFormat(format);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.Build());
        Assert.ThrowsExactly<ArgumentNullException>(() => ImageConfiguration.Default.CanDecode(null!));
    }

    [Test]
    public async Task EmbeddedFormat_MissingFromConfiguration_IsNotSupported()
    {
        using var source = new Image(3, 2, new Rgba32(10, 20, 30, 128));
        byte[] png = source.ToPng();
        byte[] bmp = new BmpBuilder { Width = 3, Height = 2, Bits = 0, Compression = 5, Data = png, ImageSize = (uint)png.Length }.Build();
        ImageConfiguration withoutPng = ImageConfiguration.Default.ToBuilder().RemoveFormat(ImageFormat.Png).Build();

        Assert.ThrowsExactly<NotSupportedException>(() => Image.Load(bmp, new DecoderOptions { Configuration = withoutPng }));
        using Image decoded = Image.Load(bmp);
        await Assert.That(decoded.GetPixelSpan().ToArray()).IsEquivalentTo(source.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecoderOptions_CopiesKeepConfiguration()
    {
        ImageConfiguration onlyPng = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png).Build();
        var options = new DecoderOptions { Configuration = onlyPng };
        await Assert.That((options with { MaxPixels = 10 }).Configuration).IsSameReferenceAs(onlyPng);
        Assert.Null(DecoderOptions.Default.Configuration);
    }
}

/// <summary>Replaces the process-wide default, so it must not run alongside other tests.</summary>
[NotInParallel]
public class ImageConfigurationDefaultTests
{
    [Test]
    public async Task ReplacingDefault_ChangesLoadSaveAndLookup()
    {
        ImageConfiguration original = ImageConfiguration.Default;
        byte[] bmp = Corpus.Read("bmp/im_rgb24.bmp");
        using var image = new Image(2, 2, new Rgba32(1, 2, 3));
        try
        {
            ImageConfiguration.Default = original.ToBuilder().RemoveFormat(ImageFormat.Bmp).Build();
            Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(bmp));
            Assert.Null(Image.DetectFormat(bmp));
            await Assert.That(ImageFormat.All).DoesNotContain(ImageFormat.Bmp);
            Assert.Null(ImageFormat.FromExtension("x.bmp"));
            await Assert.That(ImageFormat.Bmp.CanDecode).IsFalse();
            await Assert.That(ImageFormat.Bmp.CanEncode).IsFalse();
            await Assert.That(ImageFormat.Png.CanEncode).IsTrue();
            Assert.ThrowsExactly<NotSupportedException>(() => image.Save(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bmp")));
            Assert.ThrowsExactly<NotSupportedException>(() => image.Encode(ImageFormat.Bmp));

            // An explicit configuration still wins over the default.
            using Image explicitBmp = Image.Load(bmp, new DecoderOptions { Configuration = original });
            await Assert.That(explicitBmp.SourceFormat).IsSameReferenceAs(ImageFormat.Bmp);
        }
        finally
        {
            ImageConfiguration.Default = original;
        }

        using Image restored = Image.Load(bmp);
        await Assert.That(restored.SourceFormat).IsSameReferenceAs(ImageFormat.Bmp);
    }

    [Test]
    public void Default_RejectsNull() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => ImageConfiguration.Default = null!);

    [Test]
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
        int mismatches = 0;
        try
        {
            Parallel.For(0, 200, _ =>
            {
                using Image image = Image.Load(png);
                if (!image.GetPixelSpan().SequenceEqual(expectedPixels))
                {
                    Interlocked.Increment(ref mismatches);
                }
            });
        }
        finally
        {
            stop.Cancel();
            await swapper;
            ImageConfiguration.Default = original;
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }
}
