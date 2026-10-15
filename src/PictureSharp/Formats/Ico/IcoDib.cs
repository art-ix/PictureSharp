using System.Buffers;
using System.Buffers.Binary;

namespace PictureSharp.Formats.Ico;

internal static class IcoDib
{
    internal readonly record struct Header(int Width, int Height, int Bits, bool TopDown, int PaletteCount,
        int PixelOffset, int Stride, int MaskStride, bool Supported);

    public static Header Parse(ReadOnlySpan<byte> data, int width, int height)
    {
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (size > data.Length || size < 12)
        {
            throw IcoDirectory.Error("Invalid DIB header size.");
        }

        if (size == 12)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != width
                || BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) != 2 * height
                || BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) != 1)
            {
                throw IcoDirectory.Error("Invalid legacy ICO DIB dimensions or planes.");
            }

            return new(width, height, BinaryPrimitives.ReadUInt16LittleEndian(data[10..]), false, 0, 0, 0, 0, false);
        }

        if (size < 40)
        {
            throw new NotSupportedException("This ICO DIB header is not supported.");
        }

        int w = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int h = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (w != width || Math.Abs((long)h) != 2L * height || BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) != 1)
        {
            throw IcoDirectory.Error("ICO directory and DIB dimensions or planes disagree.");
        }

        if (bits is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw IcoDirectory.Error("Invalid DIB bit depth.");
        }

        bool supported = size == 40 && compression == 0 && bits != 16;
        if (compression != 0)
        {
            return new(width, height, bits, h < 0, 0, 0, 0, 0, false);
        }

        uint colors = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        if (colors > (bits <= 8 ? 1u << bits : 256u))
        {
            throw IcoDirectory.Error("Invalid ICO palette size.");
        }

        int paletteCount = colors == 0 && bits <= 8 ? 1 << bits : (int)colors;
        int pixelOffset = checked((int)size + (4 * paletteCount));
        int stride = ((width * bits + 31) / 32) * 4;
        int maskStride = ((width + 31) / 32) * 4;
        if ((long)pixelOffset + ((long)(stride + maskStride) * height) > data.Length)
        {
            throw IcoDirectory.Error("Truncated ICO raster or AND mask.");
        }

        return new(width, height, bits, h < 0, paletteCount, pixelOffset, stride, maskStride, supported);
    }

    public static Image Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        Header h = Parse(data, width, height);
        if (!h.Supported)
        {
            throw new NotSupportedException("This ICO DIB variant is not supported.");
        }

        Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(width * height);
        bool anyAlpha = false;
        for (int y = 0; y < height; y++)
        {
            int row = h.TopDown ? y : height - 1 - y;
            ReadOnlySpan<byte> source = data.Slice(h.PixelOffset + (row * h.Stride), h.Stride);
            for (int x = 0; x < width; x++)
            {
                Rgba32 pixel;
                if (h.Bits <= 8)
                {
                    int bit = x * h.Bits;
                    int index = (source[bit / 8] >> (8 - h.Bits - (bit % 8))) & ((1 << h.Bits) - 1);
                    if (index >= h.PaletteCount)
                    {
                        throw IcoDirectory.Error("ICO pixel references a missing palette entry.");
                    }

                    int p = 40 + (index * 4);
                    pixel = new(data[p + 2], data[p + 1], data[p]);
                }
                else
                {
                    int p = x * (h.Bits / 8);
                    byte alpha = h.Bits == 32 ? source[p + 3] : (byte)255;
                    pixel = new(source[p + 2], source[p + 1], source[p], alpha);
                    anyAlpha |= h.Bits == 32 && alpha != 0;
                }

                pixels[(y * width) + x] = pixel;
            }
        }

        // Windows alpha icons ignore AND for rendering; legacy all-zero alpha uses the mask.
        if (!anyAlpha)
        {
            int maskOffset = h.PixelOffset + (h.Stride * height);
            for (int y = 0; y < height; y++)
            {
                int row = h.TopDown ? y : height - 1 - y;
                ReadOnlySpan<byte> mask = data.Slice(maskOffset + (row * h.MaskStride), h.MaskStride);
                for (int x = 0; x < width; x++)
                {
                    pixels[(y * width) + x].A = (mask[x / 8] & (0x80 >> (x % 8))) != 0 ? (byte)0 : (byte)255;
                }
            }
        }

        return new Image([new ImageFrame(width, height, pixels)]) { SourceFormat = ImageFormat.Ico, HasAlpha = true };
    }

    public static void Encode(Image image, Stream output)
    {
        int width = image.Width, height = image.Height;
        int stride = width * 4, maskStride = ((width + 31) / 32) * 4;
        Span<byte> header = stackalloc byte[40];
        header.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], (stride + maskStride) * height);
        output.Write(header);
        byte[] rented = ArrayPool<byte>.Shared.Rent(stride);
        try
        {
            Span<byte> rowBytes = rented.AsSpan(0, stride);
            for (int y = height - 1; y >= 0; y--)
            {
                ReadOnlySpan<Rgba32> row = image.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    Rgba32 p = row[x];
                    rowBytes[x * 4] = p.B;
                    rowBytes[(x * 4) + 1] = p.G;
                    rowBytes[(x * 4) + 2] = p.R;
                    rowBytes[(x * 4) + 3] = p.A;
                }

                output.Write(rowBytes);
            }

            Span<byte> mask = rented.AsSpan(0, maskStride);
            for (int y = height - 1; y >= 0; y--)
            {
                mask.Clear();
                ReadOnlySpan<Rgba32> row = image.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    if (row[x].A == 0)
                    {
                        mask[x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }

                output.Write(mask);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
