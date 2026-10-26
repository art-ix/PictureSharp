using PictureSharp.Formats;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Tests.Infrastructure;
using PictureSharp.Tests.Jpeg;

namespace PictureSharp.Tests.Extensibility;

public class EmbeddedPartTests
{
    private static readonly ImageConfiguration WithTiles = Configure(new TiledContainerFormat());

    // A 10 x 6 image of 4 x 4 PNG tiles: 3 x 2 tiles, the right and bottom ones overhanging the edge.
    private static readonly byte[] Tiled = TiledContainerFormat.Create(10, 6, 4, 4, [.. Enumerable.Range(0, 6).Select(i => Tile(4, 4, i))]);

    [Test]
    public async Task Tiles_DecodeWithinTheImageAllowance()
    {
        using Image image = Image.Load(Tiled, new DecoderOptions { Configuration = WithTiles, MaxPixels = 10 * 6 });

        await Assert.That(image.Size).IsEqualTo(new System.Drawing.Size(10, 6));
        await Assert.That(image[0, 0]).IsEqualTo(Color(0));
        await Assert.That(image[9, 0]).IsEqualTo(Color(2));
        await Assert.That(image[4, 4]).IsEqualTo(Color(4));
        await Assert.That(image[9, 5]).IsEqualTo(Color(5));
    }

    [Test]
    public async Task Tiles_AreNotFrames()
    {
        using Image image = Image.Load(Tiled, new DecoderOptions { Configuration = WithTiles, MaxFrames = 1 });

        await Assert.That(image[5, 5]).IsEqualTo(Color(4));
        var charged = new DecoderOptions { Configuration = Configure(new TiledContainerFormat(charged: true)), MaxFrames = 1 };
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(Tiled, charged));
    }

    [Test]
    public async Task DecodeEmbedded_StillChargesTheSharedBudget()
    {
        var charged = new DecoderOptions { Configuration = Configure(new TiledContainerFormat(charged: true)), MaxPixels = 10 * 6 + 6 * 4 * 4 };

        using Image image = Image.Load(Tiled, charged);
        await Assert.That(image[9, 5]).IsEqualTo(Color(5));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(Tiled, charged with { MaxPixels = charged.MaxPixels - 1 }));
    }

    [Test]
    public async Task TileLargerThanDeclared_IsRejectedByTheEmbeddedDecoder()
    {
        byte[] data = TiledContainerFormat.Create(4, 4, 4, 4, Tile(5, 4, 0));

        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, new DecoderOptions { Configuration = WithTiles }));
        await Assert.That(error.Format).IsSameReferenceAs(ImageFormat.Png);
    }

    [Test]
    public async Task DeclaredTileOverMaxPixels_IsRejected()
    {
        byte[] data = TiledContainerFormat.Create(4, 4, 8, 8, Tile(8, 8, 0));

        var options = new DecoderOptions { Configuration = WithTiles, MaxPixels = 16 };
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, options));
        await Assert.That(error.Format).IsSameReferenceAs(TiledContainerFormat.Format);
        using Image image = Image.Load(data, options with { MaxPixels = 64 });
        await Assert.That(image[3, 3]).IsEqualTo(Color(0));
    }

    [Test]
    public async Task Tile_IsDecodedAsStored()
    {
        byte[] icc = File.ReadAllBytes(Corpus.PathOf("color/display_p3_v4_para3.icc"));
        byte[] jpeg;
        using (Image photo = Gradient(16, 8))
        {
            jpeg = Image.EditMetadata(photo.ToJpeg(new JpegEncoderOptions { Quality = 95 }), metadata =>
            {
                metadata.IccProfile = icc;
                metadata.Orientation = ExifOrientation.RightTop;
            });
        }

        byte[] data = TiledContainerFormat.Create(16, 8, 16, 8, jpeg);
        var options = new DecoderOptions
        {
            Configuration = WithTiles,
            ApplyExifOrientation = true,
            ColorConversion = ColorConversionMode.ToSrgb,
            LoadGainMap = true,
        };

        using Image image = Image.Load(data, options);
        using Image stored = Image.Load(jpeg, new DecoderOptions { IgnoreMetadata = true });
        using Image converted = Image.Load(jpeg, options with { Configuration = ImageConfiguration.Default, ApplyExifOrientation = false });
        await Assert.That(image.Size).IsEqualTo(new System.Drawing.Size(16, 8));
        await Assert.That(image.GetPixelSpan().SequenceEqual(stored.GetPixelSpan())).IsTrue();
        await Assert.That(converted.GetPixelSpan().SequenceEqual(stored.GetPixelSpan())).IsFalse();
        await Assert.That(image.Metadata.IccProfile).IsNull();

        // The outer load reduces the image; the tile itself is decoded at full size.
        using Image reduced = Image.Load(data, options with { TargetSize = new(4, 4) });
        await Assert.That(reduced.Width).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task Cancellation_BetweenTiles_StopsTheLoad()
    {
        using var cts = new CancellationTokenSource();
        ImageConfiguration configuration = Configure(new TiledContainerFormat(afterTile: i => { if (i == 0) cts.Cancel(); }));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Task.Run(() => Image.Load(Tiled, new DecoderOptions { Configuration = configuration }, cts.Token)));
    }

    [Test]
    public async Task CorruptTile_IsReportedAsCorrupt()
    {
        byte[] tile = Tile(4, 4, 0);
        byte[] data = TiledContainerFormat.Create(4, 4, 4, 4, tile[..^20]);

        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, new DecoderOptions { Configuration = WithTiles }));
        await Assert.That(error.Format).IsSameReferenceAs(ImageFormat.Png);
    }

    [Test]
    public async Task PartOptions_ReplaceTheLoadsSettingsForThePart()
    {
        var probe = new OptionsProbeFormat();
        var partOptions = new EmbeddedPartOptions { Jpeg = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.Plain }, MaxDegreeOfParallelism = 1 };
        var options = new DecoderOptions { Configuration = Configure(new TiledContainerFormat(partOptions: partOptions), probe), MaxDegreeOfParallelism = 3 };

        using Image image = Image.Load(TiledContainerFormat.Create(2, 1, 1, 1, OptionsProbeFormat.Part, OptionsProbeFormat.Part), options);
        await Assert.That(probe.Seen.Count).IsEqualTo(2);
        foreach (DecoderOptions part in probe.Seen)
        {
            await Assert.That(part.Jpeg.CmykStorage).IsEqualTo(JpegCmykStorage.Plain);
            await Assert.That(part.MaxDegreeOfParallelism).IsEqualTo(1);
            await Assert.That((part.MaxPixels, part.MaxFrames, part.IgnoreMetadata)).IsEqualTo((1L, 1, true));
        }
    }

    [Test]
    public async Task PartOptions_WithoutSettings_KeepTheLoads()
    {
        var probe = new OptionsProbeFormat();
        var jpeg = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.Plain };
        var options = new DecoderOptions
        {
            Configuration = Configure(new TiledContainerFormat(partOptions: new EmbeddedPartOptions()), probe),
            MaxDegreeOfParallelism = 3,
            Jpeg = jpeg,
        };

        using Image image = Image.Load(TiledContainerFormat.Create(1, 1, 1, 1, OptionsProbeFormat.Part), options);
        await Assert.That(probe.Seen.Single().Jpeg).IsSameReferenceAs(jpeg);
        await Assert.That(probe.Seen.Single().MaxDegreeOfParallelism).IsEqualTo(3);
    }

    [Test]
    [MethodDataSource(typeof(JpegCmykStorageTests), nameof(JpegCmykStorageTests.FourComponentFiles))]
    public async Task CmykJpegPart_DecodesWithThePartsStorage(string file)
    {
        byte[] jpeg = Corpus.Read(file);
        using Image stored = Image.Load(jpeg);
        var plain = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.Plain };
        byte[] data = TiledContainerFormat.Create(stored.Width, stored.Height, stored.Width, stored.Height, jpeg);
        var options = new DecoderOptions { Configuration = Configure(new TiledContainerFormat(partOptions: new EmbeddedPartOptions { Jpeg = plain })) };

        using Image image = Image.Load(data, options);
        using Image expected = Image.Load(jpeg, new DecoderOptions { Jpeg = plain });
        await Assert.That(image.GetPixelSpan().SequenceEqual(expected.GetPixelSpan())).IsTrue();
        await Assert.That(image.GetPixelSpan().SequenceEqual(stored.GetPixelSpan())).IsFalse();
    }

    [Test]
    public async Task PartOptions_RejectAnInvalidDegree()
    {
        await Assert.That(() => new EmbeddedPartOptions { MaxDegreeOfParallelism = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new EmbeddedPartOptions { MaxDegreeOfParallelism = -1 }.MaxDegreeOfParallelism).IsEqualTo(-1);
        await Assert.That(new EmbeddedPartOptions { MaxDegreeOfParallelism = null }.MaxDegreeOfParallelism).IsNull();
    }

    private static ImageConfiguration Configure(TiledContainerFormat format, OptionsProbeFormat? probe = null)
    {
        ImageConfigurationBuilder builder = ImageConfiguration.Default.ToBuilder().AddFormat(format);
        return (probe is null ? builder : builder.AddFormat(probe)).Build();
    }

    private static Rgba32 Color(int index) => new((byte)(40 * index), (byte)(255 - 30 * index), 100, 255);

    private static byte[] Tile(int width, int height, int index)
    {
        using var tile = new Image(width, height, Color(index));
        return tile.ToPng();
    }

    /// <summary>A 1 x 1 embedded format ("PROB") whose decoder records the options each part is decoded with.</summary>
    private sealed class OptionsProbeFormat : IImageFormatModule, IImageFormatDecoder
    {
        public static byte[] Part { get; } = [.. "PROB"u8];

        public List<DecoderOptions> Seen { get; } = [];

        public ImageFormat Format { get; } = ImageFormat.Create("PROB", "application/x-options-probe", ".probe");

        public int HeaderSize => 4;

        public IImageFormatDecoder Decoder => this;

        public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("PROB"u8);

        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => new() { Format = Format, Width = 1, Height = 1, Color = TestColor.Rgb8, Compression = ImageCompression.Lossless };

        public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
        {
            lock (Seen)
            {
                Seen.Add(context.Options);
            }

            return new Image(1, 1, Color(0));
        }
    }

    private static Image Gradient(int width, int height)
    {
        var image = new Image(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba32((byte)(x * 16), (byte)(y * 32), (byte)(128 + x * 4), 255);
            }
        }

        return image;
    }
}
