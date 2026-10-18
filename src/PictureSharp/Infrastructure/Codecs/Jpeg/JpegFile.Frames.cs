using System.Buffers;
using System.Buffers.Binary;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal sealed partial class JpegFile
{
    // port: libjpeg-turbo jdmarker.c get_sof
    private void ReadFrame(ReadOnlySpan<byte> s, bool progressive)
    {
        if (Frame is not null)
        {
            throw Error("Invalid JPEG file structure: two SOF markers.");
        }

        if (s.Length < 6)
        {
            throw Error("Invalid SOF segment length.");
        }

        int precision = s[0];
        int height = BinaryPrimitives.ReadUInt16BigEndian(s[1..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(s[3..]);
        int count = s[5];
        if (precision != 8)
        {
            throw precision is 12 or 16
                ? new NotSupportedException($"{precision}-bit JPEG is not supported.")
                : Error($"Unsupported JPEG data precision {precision}.");
        }

        if (height == 0)
        {
            throw new NotSupportedException("JPEG files that define their height in a DNL marker are not supported.");
        }

        if (width == 0 || count == 0)
        {
            throw Error("Empty JPEG image.");
        }

        if (s.Length != 6 + (count * 3))
        {
            throw Error("Invalid SOF segment length.");
        }

        if (count is 2 or > 4)
        {
            throw new NotSupportedException($"JPEG images with {count} components are not supported.");
        }

        var components = new JpegComponent[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> c = s.Slice(6 + (i * 3), 3);
            int h = c[1] >> 4, v = c[1] & 15;
            if (h is < 1 or > 4 || v is < 1 or > 4)
            {
                throw Error("Invalid JPEG sampling factors.");
            }

            if (c[2] > 3)
            {
                throw Error("Invalid JPEG quantization table index.");
            }

            for (int j = 0; j < i; j++)
            {
                if (components[j].Id == c[0])
                {
                    throw Error("Duplicate JPEG component identifier.");
                }
            }

            components[i] = new JpegComponent { Id = c[0], Index = i, H = h, V = v, QuantTable = c[2] };
        }

        Frame = new JpegFrame { Progressive = progressive, Width = width, Height = height, Components = components };
        Frame.ComputeLayout();
    }

    private void Allocate()
    {
        JpegFrame frame = Frame!;
        // The planes and coefficients only live until the color conversion (ReturnBuffers). Progressive images keep
        // the coefficients of every block at full size whatever the scale; the planes shrink with it.
        foreach (JpegComponent c in frame.Components)
        {
            int size = checked((int)((long)c.BlocksPerLine * c.BlocksPerColumn * 64));
            int planeLength = c.PlaneLength;
            c.Plane = ArrayPool<byte>.Shared.Rent(planeLength);
            // Blocks never decoded (truncated data, or a progressive component without scans) come out mid-gray,
            // as libjpeg's zeroed blocks do.
            c.Plane.AsSpan(0, planeLength).Fill(128);
            if (frame.Progressive)
            {
                c.Coefficients = ArrayPool<short>.Shared.Rent(size);
                c.Coefficients.AsSpan(0, size).Clear();
            }
        }
    }

    /// <summary>Gives the rented planes, coefficients and Huffman lookups back once the pixels are converted.</summary>
    public void ReturnBuffers()
    {
        foreach (JpegHuffmanTable?[] tables in (ReadOnlySpan<JpegHuffmanTable?[]>)[_dcTables, _acTables])
        {
            for (int i = 0; i < tables.Length; i++)
            {
                tables[i]?.Return();
                tables[i] = null;
            }
        }

        foreach (JpegComponent c in Frame?.Components ?? [])
        {
            if (c.Plane is { Length: > 0 } plane)
            {
                ArrayPool<byte>.Shared.Return(plane);
                c.Plane = [];
            }

            if (c.Coefficients is { } coefficients)
            {
                ArrayPool<short>.Shared.Return(coefficients);
                c.Coefficients = null;
            }
        }
    }

    // port: libjpeg-turbo jdmarker.c get_sos, jdphuff.c start_pass_phuff_decoder, jdhuff.c start_pass_huff_decoder
    private JpegScan ReadScan(ReadOnlySpan<byte> s)
    {
        JpegFrame frame = Frame!;
        int count = s.Length > 0 ? s[0] : 0;
        if (count is < 1 or > 4 || s.Length != 4 + (count * 2))
        {
            throw Error("Invalid SOS segment.");
        }

        var comps = new JpegComponent[count];
        int blocksInMcu = 0;
        for (int i = 0; i < count; i++)
        {
            int id = s[1 + (i * 2)];
            int tables = s[2 + (i * 2)];
            JpegComponent c = Array.Find(frame.Components, x => x.Id == id)
                ?? throw Error($"Scan references unknown component {id}.");
            if (Array.IndexOf(comps, c, 0, i) >= 0)
            {
                throw Error($"Scan references component {id} twice.");
            }

            comps[i] = c;
            blocksInMcu += c.H * c.V;
            c.DcTable = Table(_dcTables, tables >> 4, dc: true);
            c.AcTable = Table(_acTables, tables & 15, dc: false);
        }

        if (count > 1 && blocksInMcu > MaxBlocksInMcu)
        {
            throw Error("Too many blocks in a JPEG MCU.");
        }

        int ss = s[1 + (count * 2)];
        int se = s[2 + (count * 2)];
        int ah = s[3 + (count * 2)] >> 4;
        int al = s[3 + (count * 2)] & 15;
        if (frame.Progressive)
        {
            bool dc = ss == 0;
            bool bad = dc ? se != 0 : ss > se || se > 63 || count != 1;
            if ((ah != 0 && al != ah - 1) || al > 13)
            {
                bad = true;
            }

            if (bad)
            {
                throw Error($"Invalid progressive parameters Ss={ss} Se={se} Ah={ah} Al={al}.");
            }

            foreach (JpegComponent c in comps)
            {
                if (dc ? ah == 0 && c.DcTable is null : c.AcTable is null)
                {
                    throw Error("Scan uses an undefined Huffman table.");
                }
            }
        }
        else if (comps.Any(c => c.DcTable is null || c.AcTable is null))
        {
            throw Error("Scan uses an undefined Huffman table.");
        }

        // Quantization tables are latched when a component first appears in a scan.
        foreach (JpegComponent c in comps)
        {
            if (c.Quant is null)
            {
                ushort[] q = _quantTables[c.QuantTable] ?? throw Error($"Quantization table {c.QuantTable} is not defined.");
                c.Quant = Array.ConvertAll(q, v => (short)v);
            }
        }

        return new JpegScan { Components = comps, Ss = ss, Se = se, Ah = ah, Al = al, RestartInterval = _restartInterval };
    }
}
