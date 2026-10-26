using System.Drawing;
using PictureSharp.Formats;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Extensibility;

public class FormatMetadataTests
{
    private static readonly ImageConfiguration WithCursors = ImageConfiguration.Default.ToBuilder().AddFormat(new CursorFormat()).Build();

    private static readonly Rgba32 Pixel = new(200, 40, 10, 255);

    [Test]
    public async Task Entries_AreKeyedByType()
    {
        var metadata = new ImageMetadata();
        await Assert.That(metadata.FormatMetadata.Count).IsEqualTo(0);
        await Assert.That(metadata.GetFormatMetadata<CursorMetadata>()).IsNull();

        metadata.SetFormatMetadata(new CursorMetadata(new Point(1, 2)));
        metadata.SetFormatMetadata(new AuthorMetadata("Ada"));
        IReadOnlyList<FormatMetadata> before = metadata.FormatMetadata;
        metadata.SetFormatMetadata(new CursorMetadata(new Point(3, 4)));

        await Assert.That(metadata.GetFormatMetadata<CursorMetadata>()).IsEqualTo(new CursorMetadata(new Point(3, 4)));
        await Assert.That(metadata.GetFormatMetadata<FormatMetadata>()).IsEqualTo(new CursorMetadata(new Point(3, 4)));
        await Assert.That(metadata.FormatMetadata.SequenceEqual([new CursorMetadata(new Point(3, 4)), new AuthorMetadata("Ada")])).IsTrue();
        // A list read earlier does not change, and the returned list cannot be cast back and written.
        await Assert.That(before[0]).IsEqualTo(new CursorMetadata(new Point(1, 2)));
        await Assert.That(metadata.FormatMetadata is FormatMetadata[]).IsFalse();

        await Assert.That(metadata.RemoveFormatMetadata<CursorMetadata>()).IsTrue();
        await Assert.That(metadata.RemoveFormatMetadata<CursorMetadata>()).IsFalse();
        await Assert.That(metadata.FormatMetadata.SequenceEqual([new AuthorMetadata("Ada")])).IsTrue();
        Assert.ThrowsExactly<ArgumentNullException>(() => metadata.SetFormatMetadata(null!));
    }

    [Test]
    public async Task CloneClearAndIsEmpty_IncludeEntries()
    {
        var metadata = new ImageMetadata();
        metadata.SetFormatMetadata(new AuthorMetadata("Ada"));
        await Assert.That(metadata.IsEmpty).IsFalse();

        ImageMetadata copy = metadata.Clone();
        copy.SetFormatMetadata(new AuthorMetadata("Grace"));
        await Assert.That(metadata.GetFormatMetadata<AuthorMetadata>()!.Name).IsEqualTo("Ada");
        await Assert.That(copy.GetFormatMetadata<AuthorMetadata>()!.Name).IsEqualTo("Grace");

        metadata.Clear();
        await Assert.That(metadata.IsEmpty).IsTrue();
        await Assert.That(metadata.FormatMetadata.Count).IsEqualTo(0);
        await Assert.That(copy.FormatMetadata.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Plugin_KeepsItsMetadataFromLoadToSave()
    {
        byte[] file = CursorFormat.Create(4, 3, new Point(2, 1));
        var options = new DecoderOptions { Configuration = WithCursors };

        using Image image = Image.Load(file, options);
        await Assert.That(image.Metadata.GetFormatMetadata<CursorMetadata>()).IsEqualTo(new CursorMetadata(new Point(2, 1)));
        await Assert.That(Save(image)).IsEquivalentTo(file);
        // An explicit encoder option wins over the image's metadata.
        await Assert.That(Save(image, new Point(0, 0))).IsEquivalentTo(CursorFormat.Create(4, 3, new Point(0, 0)));

        using Image ignored = Image.Load(file, options with { IgnoreMetadata = true });
        await Assert.That(ignored.Metadata.FormatMetadata.Count).IsEqualTo(0);
        await Assert.That(Save(ignored)).IsEquivalentTo(CursorFormat.Create(4, 3, null));
    }

    [Test]
    [Arguments("Resize")]
    [Arguments("Crop")]
    [Arguments("Pad")]
    [Arguments("Rotate90")]
    [Arguments("Rotate180")]
    [Arguments("Flip")]
    [Arguments("RotateDegrees")]
    [Arguments("AutoCrop")]
    [Arguments("ProcessorWithOtherSize")]
    public async Task OperationsThatMovePixels_RemoveEntries(string operation)
    {
        using Image image = WithEntry();
        image.Mutate(x => _ = operation switch
        {
            "Resize" => x.Resize(2, 2),
            "Crop" => x.Crop(1, 1, 2, 2),
            "Pad" => x.Pad(6, 6),
            "Rotate90" => x.Rotate(RotateMode.Rotate90),
            "Rotate180" => x.Rotate(RotateMode.Rotate180),
            "Flip" => x.Flip(FlipMode.Horizontal),
            "RotateDegrees" => x.Rotate(15f),
            "AutoCrop" => x.Pad(6, 6).AutoCrop(),
            _ => x.ApplyProcessor(new ScriptedProcessor(size => new Size(size.Width + 1, size.Height))),
        });

        await Assert.That(image.Metadata.FormatMetadata.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ColorAndPixelOperations_KeepEntries()
    {
        using Image image = WithEntry();
        image.Mutate(x => x.Brightness(1.2f).Grayscale().ApplyProcessor(new ScriptedProcessor(size => size)));
        using Image clone = image.Clone(x => x.Contrast(1.1f));

        await Assert.That(image.Metadata.GetFormatMetadata<AuthorMetadata>()).IsNotNull();
        await Assert.That(clone.Metadata.GetFormatMetadata<AuthorMetadata>()).IsNotNull();
    }

    [Test]
    public async Task AutoOrient_RemovesEntriesOnlyWhenItRotates()
    {
        using Image upright = WithEntry();
        upright.Metadata.Orientation = ExifOrientation.TopLeft;
        upright.Mutate(x => x.AutoOrient());
        await Assert.That(upright.Metadata.FormatMetadata.Count).IsEqualTo(1);

        using Image rotated = WithEntry();
        rotated.Metadata.Orientation = ExifOrientation.RightTop;
        rotated.Mutate(x => x.AutoOrient());
        await Assert.That(rotated.Metadata.FormatMetadata.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CanceledOperation_KeepsEntries()
    {
        using Image image = WithEntry();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(() => image.Mutate(x => x.Resize(2, 2), canceled.Token));
        await Assert.That(image.Metadata.FormatMetadata.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TargetSize_RemovesEntries()
    {
        byte[] file = CursorFormat.Create(8, 8, new Point(2, 1));
        var options = new DecoderOptions { Configuration = WithCursors, TargetSize = new Size(4, 4) };

        using Image reduced = Image.Load(file, options);
        await Assert.That(reduced.Width).IsEqualTo(4);
        await Assert.That(reduced.Metadata.FormatMetadata.Count).IsEqualTo(0);
        // A target the image already fits leaves it as stored.
        using Image stored = Image.Load(file, options with { TargetSize = new Size(16, 16) });
        await Assert.That(stored.Metadata.FormatMetadata.Count).IsEqualTo(1);
    }

    private static Image WithEntry()
    {
        var image = new Image(4, 4, Pixel);
        image.Metadata.SetFormatMetadata(new AuthorMetadata("Ada"));
        return image;
    }

    private static byte[] Save(Image image, Point? hotspot = null)
    {
        using var output = new MemoryStream();
        image.Save(output, new CursorEncoderOptions { Configuration = WithCursors, Hotspot = hotspot });
        return output.ToArray();
    }

    private sealed record AuthorMetadata(string Name) : FormatMetadata;
}

/// <summary>The hotspot of the test cursor format, as a plugin defines its own metadata.</summary>
public sealed record CursorMetadata(Point Hotspot) : FormatMetadata;

public sealed record CursorEncoderOptions : EncoderOptions
{
    public override ImageFormat Format => CursorFormat.Format;

    /// <summary>The hotspot to write instead of the image's <see cref="CursorMetadata"/>.</summary>
    public Point? Hotspot { get; init; }
}

/// <summary>
/// A cursor-like test format: "CURS", width, height, hotspot x and y (one byte each, 255 = no hotspot), then RGBA
/// pixels. The hotspot is format metadata the core cannot represent otherwise.
/// </summary>
public sealed class CursorFormat : IImageFormatModule, IImageFormatDecoder, IImageFormatEncoder<CursorEncoderOptions>
{
    public static ImageFormat Format { get; } = ImageFormat.Create("CURS", "application/x-test-cursor", ".curs");

    ImageFormat IImageFormatModule.Format => Format;

    public int HeaderSize => 4;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("CURS"u8);

    public IImageFormatDecoder Decoder => this;

    public IImageFormatEncoder Encoder => this;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        new() { Format = Format, Width = data.Span[4], Height = data.Span[5], Color = TestColor.Rgba8, Compression = ImageCompression.Lossless };

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> span = data.Span;
        int width = span[4], height = span[5];
        context.ReservePixels(width, height);
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            ReadOnlySpan<byte> rgba = span.Slice(8 + i * 4, 4);
            pixels[i] = new Rgba32(rgba[0], rgba[1], rgba[2], rgba[3]);
        }

        if (span[6] != 255 && !context.Options.IgnoreMetadata)
        {
            image.Metadata.SetFormatMetadata(new CursorMetadata(new Point(span[6], span[7])));
        }

        return image;
    }

    public void Encode(Image image, Stream output, CursorEncoderOptions options, CancellationToken cancellationToken)
    {
        Point? hotspot = options.Hotspot ?? image.Metadata.GetFormatMetadata<CursorMetadata>()?.Hotspot;
        output.Write(Header(image.Width, image.Height, hotspot));
        foreach (Rgba32 pixel in image.GetPixelSpan())
        {
            output.Write([pixel.R, pixel.G, pixel.B, pixel.A]);
        }
    }

    /// <summary>A cursor of one color.</summary>
    public static byte[] Create(int width, int height, Point? hotspot)
    {
        var output = new List<byte>(Header(width, height, hotspot));
        for (int i = 0; i < width * height; i++)
        {
            output.AddRange([200, 40, 10, 255]);
        }

        return [.. output];
    }

    private static byte[] Header(int width, int height, Point? hotspot) =>
        [.. "CURS"u8, (byte)width, (byte)height, (byte)(hotspot?.X ?? 255), (byte)(hotspot?.Y ?? 255)];
}
