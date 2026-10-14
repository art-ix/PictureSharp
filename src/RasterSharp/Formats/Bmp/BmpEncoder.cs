using System.Buffers.Binary;
using RasterSharp.Formats.Metadata;

namespace RasterSharp.Formats.Bmp;

/// <summary>Encoder for uncompressed bottom-up BMP files.</summary>
internal sealed class BmpEncoder : IImageEncoder<BmpEncoderOptions>
{
    public static BmpEncoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Bmp;

    public void Encode(Image image, Stream output, BmpEncoderOptions options)
    {
        ImageFrame frame = image.Frames.RootFrame;
        ReadOnlySpan<Rgba32> pixels = frame.GetPixelSpan();
        int width = frame.Width;
        int height = frame.Height;

        (int bits, Rgba32[]? palette) = ChooseLayout(pixels, options.BitsPerPixel);
        byte[]? icc = MetadataSelection.Select(image.Metadata.IccProfile, options.Metadata, MetadataKinds.Icc);

        // BITMAPINFOHEADER is the most widely readable; the alpha mask and the ICC profile need BITMAPV5HEADER.
        int headerSize = bits == 32 || icc is not null ? BmpConstants.V5HeaderSize : BmpConstants.InfoHeaderSize;
        int paletteSize = (palette?.Length ?? 0) * 4;
        long stride = (((long)width * bits) + 31) / 32 * 4;
        long imageSize = stride * height;
        long dataOffset = BmpConstants.FileHeaderSize + headerSize + paletteSize;
        long fileSize = dataOffset + imageSize + (icc?.Length ?? 0);
        if (fileSize > uint.MaxValue)
        {
            throw new NotSupportedException("The image is too large for the BMP format.");
        }

        var header = new byte[dataOffset];
        Span<byte> f = header;
        f[0] = (byte)'B';
        f[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(f[2..], (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(f[10..], (uint)dataOffset);

        Span<byte> h = f.Slice(BmpConstants.FileHeaderSize, headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], (ushort)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], bits == 32 ? BmpConstants.BitFields : BmpConstants.Rgb);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], ToPixelsPerMeter(image.Metadata.HorizontalResolution));
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], ToPixelsPerMeter(image.Metadata.VerticalResolution));
        BinaryPrimitives.WriteUInt32LittleEndian(h[32..], (uint)(palette?.Length ?? 0));
        if (headerSize == BmpConstants.V5HeaderSize)
        {
            if (bits == 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h[40..], 0x00FF0000);
                BinaryPrimitives.WriteUInt32LittleEndian(h[44..], 0x0000FF00);
                BinaryPrimitives.WriteUInt32LittleEndian(h[48..], 0x000000FF);
                BinaryPrimitives.WriteUInt32LittleEndian(h[52..], 0xFF000000);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(h[56..], icc is null ? BmpConstants.ColorSpaceSrgb : BmpConstants.ProfileEmbedded);
            BinaryPrimitives.WriteUInt32LittleEndian(h[108..], BmpConstants.IntentImages);
            if (icc is not null)
            {
                // The profile follows the pixel data; its offset is relative to the start of the header.
                BinaryPrimitives.WriteUInt32LittleEndian(h[112..], (uint)(dataOffset - BmpConstants.FileHeaderSize + imageSize));
                BinaryPrimitives.WriteUInt32LittleEndian(h[116..], (uint)icc.Length);
            }
        }

        if (palette is not null)
        {
            Span<byte> pal = f[(BmpConstants.FileHeaderSize + headerSize)..];
            for (int i = 0; i < palette.Length; i++)
            {
                pal[4 * i] = palette[i].B;
                pal[(4 * i) + 1] = palette[i].G;
                pal[(4 * i) + 2] = palette[i].R;
            }
        }

        output.Write(header);

        var row = new byte[stride];
        Dictionary<uint, byte>? lookup = palette is null ? null : BuildLookup(palette);
        for (int y = height - 1; y >= 0; y--)
        {
            WriteRow(pixels.Slice(y * width, width), row, bits, lookup);
            output.Write(row);
        }

        if (icc is not null)
        {
            output.Write(icc);
        }
    }

    private static int ToPixelsPerMeter(float dpi) => (int)Math.Round(dpi / 0.0254);

    /// <summary>Picks the bit depth and, for 1/4/8 bits, a palette of the image's colors (alpha ignored).</summary>
    private static (int Bits, Rgba32[]? Palette) ChooseLayout(ReadOnlySpan<Rgba32> pixels, int requested)
    {
        if (requested is 24 or 32)
        {
            return (requested, null);
        }

        bool hasAlpha = false;
        var colors = new List<Rgba32>();
        var seen = new HashSet<uint>();
        int limit = requested == 0 ? 256 : 1 << requested;
        foreach (Rgba32 p in pixels)
        {
            hasAlpha |= p.A != 255;
            if (requested == 0 && hasAlpha)
            {
                return (32, null);
            }

            var opaque = new Rgba32(p.R, p.G, p.B, 255);
            if (colors.Count <= limit && seen.Add(opaque.PackedValue))
            {
                colors.Add(opaque);
            }
        }

        if (colors.Count > limit)
        {
            if (requested == 0)
            {
                return (24, null);
            }

            throw new InvalidOperationException(
                $"The image has more than {limit} colors and cannot be saved with {requested} bits per pixel.");
        }

        int bits = requested != 0 ? requested : colors.Count <= 2 ? 1 : colors.Count <= 16 ? 4 : 8;
        return (bits, [.. colors]);
    }

    private static Dictionary<uint, byte> BuildLookup(Rgba32[] palette)
    {
        var lookup = new Dictionary<uint, byte>(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            lookup[palette[i].PackedValue] = (byte)i;
        }

        return lookup;
    }

    private static void WriteRow(ReadOnlySpan<Rgba32> src, Span<byte> row, int bits, Dictionary<uint, byte>? lookup)
    {
        row.Clear();
        switch (bits)
        {
            case 32:
                for (int x = 0, i = 0; x < src.Length; x++, i += 4)
                {
                    Rgba32 p = src[x];
                    row[i] = p.B;
                    row[i + 1] = p.G;
                    row[i + 2] = p.R;
                    row[i + 3] = p.A;
                }

                break;
            case 24:
                for (int x = 0, i = 0; x < src.Length; x++, i += 3)
                {
                    Rgba32 p = src[x];
                    row[i] = p.B;
                    row[i + 1] = p.G;
                    row[i + 2] = p.R;
                }

                break;
            default:
                int perByte = 8 / bits;
                for (int x = 0; x < src.Length; x++)
                {
                    Rgba32 p = src[x];
                    int index = lookup![new Rgba32(p.R, p.G, p.B, 255).PackedValue];
                    row[x / perByte] |= (byte)(index << (8 - (bits * ((x % perByte) + 1))));
                }

                break;
        }
    }
}
