using System.Buffers.Binary;

namespace WebpSharp.Tests.Bmp;

/// <summary>Assembles BMP files with arbitrary (also invalid) header fields for decoder tests.</summary>
internal sealed class BmpBuilder
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Bits { get; init; } = 24;
    public uint Compression { get; init; }
    public int HeaderSize { get; init; } = 40;
    public bool TopDown { get; init; }
    public int Planes { get; init; } = 1;
    public int PixelsPerMeter { get; init; }
    public Rgba32[]? Palette { get; init; }
    public uint? ColorsUsed { get; init; }
    public uint[]? Masks { get; init; }
    public uint? OffBits { get; init; }
    public uint ImageSize { get; init; }

    /// <summary>Header bytes written over the zero-initialized header from byte 40 on (V4/V5 fields).</summary>
    public Action<byte[]>? PatchHeader { get; init; }

    /// <summary>Rows top first; each is padded to 4 bytes and stored bottom-up unless <see cref="TopDown"/>.</summary>
    public byte[][]? Rows { get; init; }

    /// <summary>Raw pixel data (RLE streams, embedded PNG); used instead of <see cref="Rows"/>.</summary>
    public byte[]? Data { get; init; }

    public byte[]? Trailer { get; init; }

    public byte[] Build()
    {
        byte[] data = Data ?? PackRows();
        int entry = HeaderSize == 12 ? 3 : 4;
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)HeaderSize);
        if (HeaderSize == 12)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)Width);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)Height);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)Planes);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)Bits);
        }
        else
        {
            var full = new byte[Math.Max(HeaderSize, 124)];
            BinaryPrimitives.WriteUInt32LittleEndian(full, (uint)HeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(full.AsSpan(4), Width);
            BinaryPrimitives.WriteInt32LittleEndian(full.AsSpan(8), TopDown ? -Height : Height);
            BinaryPrimitives.WriteUInt16LittleEndian(full.AsSpan(12), (ushort)Planes);
            BinaryPrimitives.WriteUInt16LittleEndian(full.AsSpan(14), (ushort)Bits);
            BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(16), Compression);
            BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(20), ImageSize);
            BinaryPrimitives.WriteInt32LittleEndian(full.AsSpan(24), PixelsPerMeter);
            BinaryPrimitives.WriteInt32LittleEndian(full.AsSpan(28), PixelsPerMeter);
            BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(32), ColorsUsed ?? (uint)(Palette?.Length ?? 0));
            if (Masks is not null && HeaderSize > 40)
            {
                for (int i = 0; i < Masks.Length; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(40 + (4 * i)), Masks[i]);
                }
            }

            PatchHeader?.Invoke(full);
            full.AsSpan(0, HeaderSize).CopyTo(header);
        }

        var body = new List<byte>(header);
        if (Masks is not null && HeaderSize == 40)
        {
            foreach (uint m in Masks)
            {
                body.AddRange(BitConverter.GetBytes(m));
            }
        }

        foreach (Rgba32 c in Palette ?? [])
        {
            body.AddRange(entry == 3 ? [c.B, c.G, c.R] : [c.B, c.G, c.R, 0]);
        }

        uint offset = OffBits ?? (uint)(14 + body.Count);
        while (14 + body.Count < offset)
        {
            body.Add(0);
        }

        var file = new List<byte>();
        file.AddRange("BM"u8.ToArray());
        file.AddRange(BitConverter.GetBytes((uint)(14 + body.Count + data.Length)));
        file.AddRange(new byte[4]);
        file.AddRange(BitConverter.GetBytes(offset));
        file.AddRange(body);
        file.AddRange(data);
        file.AddRange(Trailer ?? []);
        return [.. file];
    }

    private byte[] PackRows()
    {
        byte[][] rows = Rows ?? [];
        int stride = Math.Max(((Width * Bits) + 31) / 32 * 4, rows.Length == 0 ? 0 : rows.Max(r => r.Length));
        var data = new byte[stride * rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int dst = TopDown ? i : rows.Length - 1 - i;
            rows[i].CopyTo(data, dst * stride);
        }

        return data;
    }
}
