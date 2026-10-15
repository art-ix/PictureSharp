using System.Buffers.Binary;

namespace PictureSharp.Compression;

/// <summary>CRC-32 (ISO 3309 / ITU-T V.42, as used by PNG and gzip), slicing-by-8 or carry-less multiplication.</summary>
internal static partial class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        for (int n = 0; n < 256; n++)
        {
            for (int s = 1; s < 8; s++)
            {
                uint prev = table[((s - 1) * 256) + n];
                table[(s * 256) + n] = (prev >> 8) ^ table[prev & 0xff];
            }
        }

        return table;
    }

    /// <summary>Computes the CRC of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>Continues a CRC computation (pass the previous result as <paramref name="crc"/>).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        int done = UpdateSimd(ref crc, data);
        return UpdateScalar(crc, data[done..]);
    }

    /// <summary>Slicing-by-8 <see cref="Update"/>; the vector path is in <c>Crc32.Simd.cs</c>.</summary>
    internal static uint UpdateScalar(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Table;
        crc = ~crc;
        while (data.Length >= 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = t[(7 * 256) + (one & 0xff)] ^ t[(6 * 256) + ((one >> 8) & 0xff)] ^
                  t[(5 * 256) + ((one >> 16) & 0xff)] ^ t[(4 * 256) + (one >> 24)] ^
                  t[(3 * 256) + (two & 0xff)] ^ t[(2 * 256) + ((two >> 8) & 0xff)] ^
                  t[(1 * 256) + ((two >> 16) & 0xff)] ^ t[two >> 24];
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = t[(crc ^ b) & 0xff] ^ (crc >> 8);
        }

        return ~crc;
    }
}
