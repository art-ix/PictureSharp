using System.Buffers.Binary;

namespace RasterSharp.Tests.Pcx;

/// <summary>Builds a PCX file from an uncompressed scanline per row (Planes * BytesPerLine bytes).</summary>
internal sealed class PcxBuilder
{
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    public int XMin { get; init; }
    public int YMin { get; init; }
    public int Bits { get; init; } = 8;
    public int Planes { get; init; } = 1;
    public int Version { get; init; } = 5;
    public int Encoding { get; init; } = 1;
    public int BytesPerLine { get; init; }
    public int HorizontalDpi { get; init; }
    public int VerticalDpi { get; init; }
    public byte[]? EgaPalette { get; init; }
    public byte[][]? Rows { get; init; }
    public byte[]? Data { get; init; }
    public byte[]? VgaPalette { get; init; }
    public byte[]? Trailing { get; init; }

    public byte[] Build()
    {
        int stride = BytesPerLine != 0 ? BytesPerLine : Even(Width, Bits);
        var header = new byte[128];
        header[0] = 0x0A;
        header[1] = (byte)Version;
        header[2] = (byte)Encoding;
        header[3] = (byte)Bits;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)XMin);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)YMin);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)(XMin + Width - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)(YMin + Height - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)HorizontalDpi);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)VerticalDpi);
        if (EgaPalette is not null)
        {
            EgaPalette.AsSpan(0, Math.Min(EgaPalette.Length, 48)).CopyTo(header.AsSpan(16));
        }

        header[65] = (byte)Planes;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), (ushort)stride);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(68), 1);

        var file = new List<byte>(128 + (Height * stride * Math.Max(Planes, 1)));
        file.AddRange(header);
        if (Data is not null)
        {
            file.AddRange(Data);
        }
        else if (Rows is not null)
        {
            foreach (byte[] row in Rows)
            {
                file.AddRange(Encoding == 0 ? row : Encode(row));
            }
        }

        if (VgaPalette is not null)
        {
            file.Add(0x0C);
            file.AddRange(VgaPalette);
        }

        if (Trailing is not null)
        {
            file.AddRange(Trailing);
        }

        return [.. file];
    }

    public static int Even(int width, int bits)
    {
        int raw = (width * bits + 7) / 8;
        return raw + (raw & 1);
    }

    public static byte[] Rgb(params Rgba32[] colors)
    {
        var bytes = new byte[48];
        for (int i = 0; i < colors.Length && i < 16; i++)
        {
            bytes[i * 3] = colors[i].R;
            bytes[(i * 3) + 1] = colors[i].G;
            bytes[(i * 3) + 2] = colors[i].B;
        }

        return bytes;
    }

    public static byte[] Vga(params Rgba32[] colors)
    {
        var bytes = new byte[768];
        for (int i = 0; i < colors.Length && i < 256; i++)
        {
            bytes[i * 3] = colors[i].R;
            bytes[(i * 3) + 1] = colors[i].G;
            bytes[(i * 3) + 2] = colors[i].B;
        }

        return bytes;
    }

    private static byte[] Encode(ReadOnlySpan<byte> row)
    {
        var encoded = new byte[row.Length * 2];
        int written = 0;
        for (int i = 0; i < row.Length;)
        {
            byte value = row[i];
            int count = 1;
            while (count < 63 && i + count < row.Length && row[i + count] == value)
            {
                count++;
            }

            if (count > 1 || value >= 0xC0)
            {
                encoded[written++] = (byte)(0xC0 | count);
                encoded[written++] = value;
            }
            else
            {
                encoded[written++] = value;
            }

            i += count;
        }

        return encoded[..written];
    }
}
