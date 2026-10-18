using PictureSharp.Formats;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;

namespace PictureSharp.Tests.Extensibility;

/// <summary>
/// The 0.7.6 plugin decoder options: <see cref="DecoderOptions.FormatOptions"/> reach a plugin in Identify, Load,
/// metadata reads and embedded decodes; without them a plugin behaves as before and built-in formats ignore them.
/// </summary>
public class FormatOptionsTests
{
    private static readonly ImageConfiguration WithPaged =
        ImageConfiguration.Default.ToBuilder().AddFormat(new PagedContainerFormat()).AddFormat(new WrapFormat()).Build();

    private static readonly (int Width, int Height, Rgba32 Color)[] Pages =
        [(3, 2, new Rgba32(255, 0, 0)), (5, 4, new Rgba32(0, 255, 0, 128)), (1, 7, new Rgba32(0, 0, 255))];

    private static readonly byte[] Data = PagedContainerFormat.Create(Pages);

    private static DecoderOptions Page(int page, DecoderOptions? options = null) =>
        (options ?? new DecoderOptions()) with { Configuration = WithPaged, FormatOptions = [new PagedDecoderOptions { Page = page }] };

    // ------------------------------------------------------------ plugin

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Load_DecodesTheSelectedPage_AndIdentifyMatches(int page)
    {
        DecoderOptions options = Page(page);
        using Image image = Image.Load(Data, options);
        ImageInfo info = Image.Identify(Data, options);

        await Assert.That(image.Width).IsEqualTo(Pages[page].Width);
        await Assert.That(image.Height).IsEqualTo(Pages[page].Height);
        await Assert.That(image[0, 0]).IsEqualTo(Pages[page].Color);
        await Assert.That(GainMapContainerFormat.Text(image.Metadata.Xmp)).IsEqualTo($"page {page}");
        await Assert.That((info.Width, info.Height)).IsEqualTo((image.Width, image.Height));
        await Assert.That(info.ContainerItemCount).IsEqualTo(Pages.Length);
        await Assert.That(GainMapContainerFormat.Text(Image.ReadMetadata(Data, options).Xmp)).IsEqualTo($"page {page}");
    }

    [Test]
    public async Task NoOptions_DecodeTheFirstPage()
    {
        var options = new DecoderOptions { Configuration = WithPaged };
        using Image image = Image.Load(Data, options);
        await Assert.That((image.Width, image.Height)).IsEqualTo((3, 2));
        await Assert.That(Image.Identify(Data, options).Width).IsEqualTo(3);
        await Assert.That(GainMapContainerFormat.Text(Image.ReadMetadata(Data, options).Xmp)).IsEqualTo("page 0");
    }

    [Test]
    public async Task OptionsOfAnotherPlugin_AreIgnored()
    {
        var options = new DecoderOptions { Configuration = WithPaged, FormatOptions = [new OtherDecoderOptions { Page = 2 }] };
        using Image image = Image.Load(Data, options);
        await Assert.That((image.Width, image.Height)).IsEqualTo((3, 2));

        DecoderOptions both = options with { FormatOptions = [new OtherDecoderOptions { Page = 0 }, new PagedDecoderOptions { Page = 2 }] };
        using Image second = Image.Load(Data, both);
        await Assert.That((second.Width, second.Height)).IsEqualTo((1, 7));
    }

    [Test]
    public async Task PageOutsideTheFile_IsAFormatError()
    {
        DecoderOptions options = Page(Pages.Length);
        await Assert.That(() => Image.Load(Data, options)).Throws<ImageFormatException>();
        await Assert.That(() => Image.Identify(Data, options)).Throws<ImageFormatException>();
        await Assert.That(() => Image.ReadMetadata(Data, options)).Throws<ImageFormatException>();
        await Assert.That(() => new PagedDecoderOptions { Page = -1 }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Limits_ApplyToTheSelectedPage()
    {
        byte[] data = PagedContainerFormat.Create((2, 2, new Rgba32(1, 2, 3)), (100, 100, new Rgba32(4, 5, 6)));
        DecoderOptions small = new() { MaxPixels = 100 };
        using (Image first = Image.Load(data, Page(0, small)))
        {
            await Assert.That(first.Width).IsEqualTo(2);
        }

        await Assert.That(() => Image.Load(data, Page(1, small))).Throws<ImageFormatException>();
        await Assert.That(() => Image.ReadMetadata(Data, Page(1, new DecoderOptions { MaxMetadataBytes = 3 }))).Throws<ImageFormatException>();
        await Assert.That(() => Image.Load(Data, Page(1), new CancellationToken(canceled: true))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task EmbeddedDecodes_ReceiveTheSameOptions()
    {
        byte[] wrapped = WrapFormat.Wrap(Data);
        using Image image = Image.Load(wrapped, Page(1));
        await Assert.That((image.Width, image.Height)).IsEqualTo((5, 4));
        await Assert.That(Image.Identify(wrapped, Page(2)).Height).IsEqualTo(7);
    }

    // ------------------------------------------------------------ built-in formats

    public static IEnumerable<Func<EncoderOptions>> BuiltInEncoders() =>
    [
        () => new PngEncoderOptions(), () => new WebPEncoderOptions(), () => new WebPEncoderOptions { Lossless = true },
        () => new JpegEncoderOptions(), () => new GifEncoderOptions(), () => new BmpEncoderOptions(),
        () => new PcxEncoderOptions(), () => new IcoEncoderOptions(),
    ];

    [Test]
    [MethodDataSource(nameof(BuiltInEncoders))]
    public async Task BuiltInFormats_IgnoreFormatOptions(EncoderOptions encoder)
    {
        using var source = new Image(9, 6);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source[x, y] = new Rgba32((byte)(x * 28), (byte)(y * 40), (byte)(x * y * 5), (byte)(255 - x * 10));
            }
        }

        byte[] encoded = source.Encode(encoder);
        DecoderOptions plain = new() { Configuration = WithPaged };
        DecoderOptions withOptions = plain with { FormatOptions = [new PagedDecoderOptions { Page = 3 }, new OtherDecoderOptions()] };
        using Image expected = Image.Load(encoded, plain);
        using Image actual = Image.Load(encoded, withOptions);

        await Assert.That(actual.GetPixelSpan().ToArray()).IsEquivalentTo(expected.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        await Assert.That(Image.Identify(encoded, withOptions)).IsEqualTo(Image.Identify(encoded, plain));
    }

    // ------------------------------------------------------------ DecoderOptions

    [Test]
    public async Task FormatOptions_AreAValidatedSnapshot()
    {
        await Assert.That(DecoderOptions.Default.FormatOptions.Count).IsEqualTo(0);
        await Assert.That(DecoderOptions.Default.GetFormatOptions<PagedDecoderOptions>()).IsNull();

        List<FormatDecoderOptions> source = [new PagedDecoderOptions { Page = 1 }];
        var options = new DecoderOptions { FormatOptions = source };
        source.Add(new OtherDecoderOptions());
        source[0] = new PagedDecoderOptions { Page = 2 };
        await Assert.That(options.FormatOptions.Count).IsEqualTo(1);
        await Assert.That(options.GetFormatOptions<PagedDecoderOptions>()?.Page).IsEqualTo(1);
        await Assert.That(options.GetFormatOptions<OtherDecoderOptions>()).IsNull();
        await Assert.That(options.FormatOptions is FormatDecoderOptions[]).IsFalse();
        await Assert.That((options with { MaxPixels = 5 }).FormatOptions).IsSameReferenceAs(options.FormatOptions);
        await Assert.That(options.GetFormatOptions<FormatDecoderOptions>()).IsSameReferenceAs(options.FormatOptions[0]);

        await Assert.That(() => new DecoderOptions { FormatOptions = null! }).Throws<ArgumentNullException>();
        await Assert.That(() => new DecoderOptions { FormatOptions = [null!] }).Throws<ArgumentException>();
        await Assert.That(() => new DecoderOptions { FormatOptions = [new PagedDecoderOptions(), new PagedDecoderOptions { Page = 1 }] })
            .Throws<ArgumentException>();
    }
}
