using System.Buffers.Binary;

namespace PictureSharp.Formats.Bmp;

/// <summary>Decoder for Windows and OS/2 bitmaps.</summary>
internal sealed partial class BmpDecoder : IImageDecoder
{
    private static readonly Rgba32 Black = new(0, 0, 0, 255);

    public static BmpDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Bmp;

    private static ImageFormatException Error(string message) => new(ImageFormat.Bmp, message);

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        BmpHeader h = BmpHeader.Parse(data.Span);
        bool hasAlpha = h.HasAlpha;
        if (h.Compression == BmpCompression.Embedded)
        {
            hasAlpha = EmbeddedDecoder(data.Span, h).Identify(EmbeddedData(data, h), options).HasAlpha;
        }

        (float dpiX, float dpiY) = Resolution(h);
        return new ImageInfo
        {
            Format = ImageFormat.Bmp,
            Width = h.Width,
            Height = h.Height,
            HasAlpha = hasAlpha,
            HorizontalResolution = dpiX,
            VerticalResolution = dpiY,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ReadOnlySpan<byte> file = data.Span;
        BmpHeader h = BmpHeader.Parse(file);
        options.ValidateDimensions(ImageFormat.Bmp, h.Width, h.Height);

        Image image;
        if (h.Compression == BmpCompression.Embedded)
        {
            image = DecodeEmbedded(data, h, options);
        }
        else
        {
            Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(h.Width * h.Height);
            switch (h.Compression)
            {
                case BmpCompression.Rle8:
                case BmpCompression.Rle4:
                    DecodeRle(file, h, ReadPalette(file, h), pixels);
                    break;
                case BmpCompression.Rle24:
                    DecodeRle24(file, h, pixels);
                    break;
                default:
                    DecodeUncompressed(file, h, pixels);
                    break;
            }

            List<ImageFrame> frames = [new ImageFrame(h.Width, h.Height, pixels)];
            image = new Image(frames) { HasAlpha = h.HasAlpha };
        }

        image.SourceFormat = ImageFormat.Bmp;
        if (h.Compression != BmpCompression.Embedded || (h.XPelsPerMeter > 0 && h.YPelsPerMeter > 0))
        {
            (float dpiX, float dpiY) = Resolution(h);
            (image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution) = (dpiX, dpiY);
        }

        if (!options.IgnoreMetadata && h.IccOffset >= 0)
        {
            image.Metadata.IccProfile = file.Slice(h.IccOffset, h.IccLength).ToArray();
        }

        return image;
    }

    private static (float X, float Y) Resolution(BmpHeader h) =>
        h.XPelsPerMeter > 0 && h.YPelsPerMeter > 0
            ? ((float)(h.XPelsPerMeter * 0.0254), (float)(h.YPelsPerMeter * 0.0254))
            : (96f, 96f);

    // ------------------------------------------------------------------ embedded PNG/JPEG

    private static ReadOnlyMemory<byte> EmbeddedData(ReadOnlyMemory<byte> data, BmpHeader h)
    {
        int start = (int)h.DataOffset;
        int available = data.Length - start;
        int length = h.ImageSize > 0 && h.ImageSize < available ? (int)h.ImageSize : available;
        return data.Slice(start, length);
    }

    private static IImageDecoder EmbeddedDecoder(ReadOnlySpan<byte> file, BmpHeader h)
    {
        ImageFormat expected = h.RawCompression == BmpConstants.Png ? ImageFormat.Png : ImageFormat.Jpeg;
        if (CodecRegistry.GetDetector(expected)?.IsSupported(file[(int)h.DataOffset..]) != true)
        {
            throw Error($"The BMP does not contain the {expected.Name} data its header declares.");
        }

        return CodecRegistry.GetDecoder(expected)!;
    }

    private static Image DecodeEmbedded(ReadOnlyMemory<byte> data, BmpHeader h, DecoderOptions options)
    {
        return EmbeddedDecoder(data.Span, h).Decode(EmbeddedData(data, h), options);
    }

    // ------------------------------------------------------------------ palette

    private static Rgba32[] ReadPalette(ReadOnlySpan<byte> file, BmpHeader h)
    {
        // Indices beyond the stored palette map to black.
        var palette = new Rgba32[1 << h.BitCount];
        palette.AsSpan().Fill(Black);
        ReadOnlySpan<byte> src = file.Slice(h.PaletteOffset, h.PaletteCount * h.PaletteEntrySize);
        for (int i = 0; i < h.PaletteCount; i++)
        {
            ReadOnlySpan<byte> e = src.Slice(i * h.PaletteEntrySize, 3);
            palette[i] = new Rgba32(e[2], e[1], e[0], 255);
        }

        return palette;
    }

    // ------------------------------------------------------------------ uncompressed
}
