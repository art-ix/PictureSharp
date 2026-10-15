using System.Buffers.Binary;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal sealed partial class JpegFile
{
    private static JpegHuffmanTable? Table(JpegHuffmanTable?[] tables, int index, bool dc)
    {
        if (index > 3)
        {
            throw Error($"Invalid Huffman table index {index}.");
        }

        if (tables[index] is null && index < 2)
        {
            // Motion JPEG omits DHT; libjpeg-turbo installs the standard tables (Annex K.3) as defaults.
            tables[index] = (dc, index) switch
            {
                (true, 0) => JpegHuffmanTable.Build(JpegTables.DcLuminanceCounts, JpegTables.DcValues),
                (true, _) => JpegHuffmanTable.Build(JpegTables.DcChrominanceCounts, JpegTables.DcValues),
                (false, 0) => JpegHuffmanTable.Build(JpegTables.AcLuminanceCounts, JpegTables.AcLuminanceValues),
                _ => JpegHuffmanTable.Build(JpegTables.AcChrominanceCounts, JpegTables.AcChrominanceValues),
            };
        }

        JpegHuffmanTable? table = tables[index];
        if (dc && table is { MaxSymbol: > 15 })
        {
            throw Error("Bogus DC Huffman table definition.");
        }

        return table;
    }

    // port: libjpeg-turbo jdmarker.c get_dht
    private void ReadHuffmanTables(ReadOnlySpan<byte> s)
    {
        while (s.Length > 16)
        {
            int index = s[0];
            ReadOnlySpan<byte> counts = s.Slice(1, 16);
            int total = 0;
            foreach (byte n in counts)
            {
                total += n;
            }

            s = s[17..];
            if (total > 256 || total > s.Length)
            {
                throw Error("Bogus Huffman table definition.");
            }

            if ((index & ~0x13) != 0)
            {
                throw Error($"Invalid Huffman table index 0x{index:X2}.");
            }

            JpegHuffmanTable table = JpegHuffmanTable.Build(counts, s[..total]);
            (index >= 0x10 ? _acTables : _dcTables)[index & 3] = table;
            s = s[total..];
        }

        if (s.Length != 0)
        {
            throw Error("Invalid DHT segment length.");
        }
    }

    // port: libjpeg-turbo jdmarker.c get_dqt
    private void ReadQuantTables(ReadOnlySpan<byte> s)
    {
        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        while (s.Length > 0)
        {
            int precision = s[0] >> 4;
            int index = s[0] & 15;
            int size = precision == 0 ? 64 : 128;
            if (index > 3 || precision > 1)
            {
                throw Error("Invalid DQT segment.");
            }

            if (s.Length < 1 + size)
            {
                throw Error("Invalid DQT segment length.");
            }

            var table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                table[natural[i]] = precision == 0 ? s[1 + i] : BinaryPrimitives.ReadUInt16BigEndian(s[(1 + (2 * i))..]);
            }

            _quantTables[index] = table;
            s = s[(1 + size)..];
        }
    }
}
