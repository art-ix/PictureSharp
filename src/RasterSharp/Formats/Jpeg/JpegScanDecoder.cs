namespace RasterSharp.Formats.Jpeg;

/// <summary>Parameters of one scan (SOS segment).</summary>
internal sealed class JpegScan
{
    public required JpegComponent[] Components { get; init; }
    public int Ss { get; init; }
    public int Se { get; init; }
    public int Ah { get; init; }
    public int Al { get; init; }
    public int RestartInterval { get; init; }
}

/// <summary>Huffman entropy decoding of sequential and progressive scans.</summary>
internal static class JpegScanDecoder
{
    /// <summary>Decodes a scan starting at <paramref name="position"/>; returns the position where its data ends.</summary>
    public static int Decode(ReadOnlySpan<byte> data, int position, JpegFrame frame, JpegScan scan)
    {
        var reader = new JpegBitReader(data, position);
        foreach (JpegComponent c in scan.Components)
        {
            c.DcPredictor = 0;
        }

        if (frame.Progressive)
        {
            DecodeProgressive(ref reader, frame, scan);
        }
        else
        {
            DecodeSequential(ref reader, frame, scan);
        }

        return reader.Position;
    }

    private delegate void McuAction(ref JpegBitReader reader, int mcuX, int mcuY, ref int eobRun);

    /// <summary>Calls <paramref name="decodeMcu"/> for every MCU of the scan, handling restart intervals.</summary>
    private static void ForEachMcu(ref JpegBitReader reader, JpegFrame frame, JpegScan scan, McuAction decodeMcu)
    {
        int eobRun = 0;
        int mcusX, mcusY;
        if (scan.Components.Length == 1)
        {
            // A non-interleaved scan ignores the sampling factors: one block per MCU.
            mcusX = scan.Components[0].WidthInBlocks;
            mcusY = scan.Components[0].HeightInBlocks;
        }
        else
        {
            mcusX = frame.McusPerLine;
            mcusY = frame.McuRows;
        }

        int restartsToGo = scan.RestartInterval;
        int nextRestart = 0;
        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                if (scan.RestartInterval > 0)
                {
                    if (restartsToGo == 0)
                    {
                        reader.Restart(nextRestart);
                        nextRestart = (nextRestart + 1) & 7;
                        foreach (JpegComponent c in scan.Components)
                        {
                            c.DcPredictor = 0;
                        }

                        eobRun = 0;
                        restartsToGo = scan.RestartInterval;
                    }

                    restartsToGo--;
                }

                // Once the data has run out the remaining blocks keep their current (zero) coefficients.
                if (!reader.InsufficientData)
                {
                    decodeMcu(ref reader, mx, my, ref eobRun);
                }
            }
        }
    }

    // ------------------------------------------------------------------ sequential

    private static void DecodeSequential(ref JpegBitReader reader, JpegFrame frame, JpegScan scan)
    {
        JpegComponent[] comps = scan.Components;
        short[] block = new short[64];
        bool single = comps.Length == 1;

        void DecodeMcu(ref JpegBitReader r, int mx, int my, ref int eobRun)
        {
            foreach (JpegComponent c in comps)
            {
                int h = single ? 1 : c.H;
                int v = single ? 1 : c.V;
                for (int by = 0; by < v; by++)
                {
                    for (int bx = 0; bx < h; bx++)
                    {
                        Array.Clear(block);
                        DecodeBlockSequential(ref r, c, block);
                        int blockX = (mx * h) + bx;
                        int blockY = (my * v) + by;
                        JpegIdct.Transform(block, c.Quant!, c.Plane.AsSpan((blockY * 8 * c.PlaneStride) + (blockX * 8)), c.PlaneStride);
                    }
                }
            }
        }

        // Blocks skipped after the data ran out stay at the plane's initial mid-gray.
        ForEachMcu(ref reader, frame, scan, DecodeMcu);
    }

    // port: libjpeg-turbo jdhuff.c decode_mcu_slow (one block)
    private static void DecodeBlockSequential(ref JpegBitReader r, JpegComponent c, Span<short> block)
    {
        int s = r.Decode(c.DcTable!);
        int diff = s == 0 ? 0 : r.Receive(s);
        int dc = c.DcPredictor + diff;
        c.DcPredictor = dc;
        block[0] = (short)dc;

        JpegHuffmanTable ac = c.AcTable!;
        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        for (int k = 1; k < 64; k++)
        {
            s = r.Decode(ac);
            int run = s >> 4;
            s &= 15;
            if (s != 0)
            {
                k += run;
                block[natural[k]] = (short)r.Receive(s);
            }
            else
            {
                if (run != 15)
                {
                    break;
                }

                k += 15;
            }
        }
    }

    // ------------------------------------------------------------------ progressive

    private static void DecodeProgressive(ref JpegBitReader reader, JpegFrame frame, JpegScan scan)
    {
        JpegComponent[] comps = scan.Components;
        bool single = comps.Length == 1;
        int ss = scan.Ss, se = scan.Se, al = scan.Al;
        bool dc = ss == 0;
        bool first = scan.Ah == 0;

        void DecodeMcu(ref JpegBitReader r, int mx, int my, ref int eobRun)
        {
            foreach (JpegComponent c in comps)
            {
                int h = single ? 1 : c.H;
                int v = single ? 1 : c.V;
                for (int by = 0; by < v; by++)
                {
                    for (int bx = 0; bx < h; bx++)
                    {
                        int blockIndex = (((my * v) + by) * c.BlocksPerLine) + (mx * h) + bx;
                        Span<short> block = c.Coefficients!.AsSpan(blockIndex * 64, 64);
                        if (dc)
                        {
                            if (first)
                            {
                                // port: libjpeg-turbo jdphuff.c decode_mcu_DC_first
                                int s = r.Decode(c.DcTable!);
                                int diff = s == 0 ? 0 : r.Receive(s);
                                int value = c.DcPredictor + diff;
                                c.DcPredictor = value;
                                block[0] = (short)(value << al);
                            }
                            else if (r.GetBit() != 0)
                            {
                                // port: libjpeg-turbo jdphuff.c decode_mcu_DC_refine
                                block[0] |= (short)(1 << al);
                            }
                        }
                        else if (first)
                        {
                            DecodeAcFirst(ref r, c.AcTable!, block, ss, se, al, ref eobRun);
                        }
                        else
                        {
                            DecodeAcRefine(ref r, c.AcTable!, block, ss, se, al, ref eobRun);
                        }
                    }
                }
            }
        }

        ForEachMcu(ref reader, frame, scan, DecodeMcu);
    }

    // port: libjpeg-turbo jdphuff.c decode_mcu_AC_first
    private static void DecodeAcFirst(ref JpegBitReader r, JpegHuffmanTable table, Span<short> block, int ss, int se, int al,
                                      ref int eobRun)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return;
        }

        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        for (int k = ss; k <= se; k++)
        {
            int s = r.Decode(table);
            int run = s >> 4;
            s &= 15;
            if (s != 0)
            {
                k += run;
                block[natural[k]] = (short)(r.Receive(s) << al);
            }
            else if (run == 15)
            {
                k += 15;
            }
            else
            {
                eobRun = 1 << run;
                if (run != 0)
                {
                    eobRun += r.GetBits(run);
                }

                eobRun--;
                break;
            }
        }
    }

    // port: libjpeg-turbo jdphuff.c decode_mcu_AC_refine
    private static void DecodeAcRefine(ref JpegBitReader r, JpegHuffmanTable table, Span<short> block, int ss, int se, int al,
                                       ref int eobRun)
    {
        int p1 = 1 << al;
        int m1 = -1 << al;
        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        int k = ss;
        if (eobRun == 0)
        {
            for (; k <= se; k++)
            {
                int s = r.Decode(table);
                int run = s >> 4;
                s &= 15;
                if (s != 0)
                {
                    // Only magnitude 1 is valid here; libjpeg warns and carries on otherwise.
                    s = r.GetBit() != 0 ? p1 : m1;
                }
                else if (run != 15)
                {
                    eobRun = 1 << run;
                    if (run != 0)
                    {
                        eobRun += r.GetBits(run);
                    }

                    break;
                }

                // Advance over already-nonzero coefficients (appending correction bits) and run still-zero ones.
                do
                {
                    int pos = natural[k];
                    if (block[pos] != 0)
                    {
                        Refine(ref r, ref block[pos], p1, m1);
                    }
                    else if (--run < 0)
                    {
                        break;
                    }

                    k++;
                }
                while (k <= se);

                if (s != 0)
                {
                    block[natural[k]] = (short)s;
                }
            }
        }

        if (eobRun > 0)
        {
            // Correction bits for the remaining nonzero coefficients of a block inside an end-of-band run.
            for (; k <= se; k++)
            {
                int pos = natural[k];
                if (block[pos] != 0)
                {
                    Refine(ref r, ref block[pos], p1, m1);
                }
            }

            eobRun--;
        }
    }

    private static void Refine(ref JpegBitReader r, ref short coef, int p1, int m1)
    {
        if (r.GetBit() != 0 && (coef & p1) == 0)
        {
            coef += (short)(coef >= 0 ? p1 : m1);
        }
    }
}
