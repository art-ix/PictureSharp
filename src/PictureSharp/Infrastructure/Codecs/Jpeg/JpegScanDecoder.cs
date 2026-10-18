using System.Numerics;
using System.Runtime.CompilerServices;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

/// <summary>Huffman entropy decoding of sequential and progressive scans.</summary>
internal static class JpegScanDecoder
{
    /// <summary>Decodes a scan starting at <paramref name="position"/>; returns the position where its data ends.</summary>
    public static int Decode(ReadOnlySpan<byte> data, int position, JpegFrame frame, JpegScan scan, CancellationToken cancellationToken)
    {
        var reader = new JpegBitReader(data, position);
        foreach (JpegComponent c in scan.Components)
        {
            c.DcPredictor = 0;
        }

        if (frame.Progressive)
        {
            DecodeProgressive(ref reader, frame, scan, cancellationToken);
        }
        else
        {
            DecodeSequential(ref reader, frame, scan, cancellationToken);
        }

        return reader.Position;
    }

    private delegate void McuAction(ref JpegBitReader reader, int mcuX, int mcuY, ref int eobRun);

    /// <summary>Calls <paramref name="decodeMcu"/> for every MCU of the scan, handling restart intervals.</summary>
    private static void ForEachMcu(ref JpegBitReader reader, JpegFrame frame, JpegScan scan, McuAction decodeMcu,
        CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
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

    private static void DecodeSequential(ref JpegBitReader reader, JpegFrame frame, JpegScan scan, CancellationToken cancellationToken)
    {
        JpegComponent[] comps = scan.Components;
        bool single = comps.Length == 1;
        int blocks = single ? 1 : comps.Sum(c => c.H * c.V);
        short[] mcu = new short[blocks * 64];
        int[] predictors = new int[4];

        void DecodeMcu(ref JpegBitReader r, int mx, int my, ref int eobRun)
        {
            // Away from markers the whole MCU is decoded by the fast path; when the data runs short inside it, the MCU
            // is decoded again from its start by the checked path, which handles markers and missing data.
            bool fast = false;
            if (!r.AtMarker)
            {
                JpegBitReader start = r;
                for (int i = 0; i < comps.Length; i++)
                {
                    predictors[i] = comps[i].DcPredictor;
                }

                Array.Clear(mcu);
                fast = DecodeMcuFast(ref r, comps, single, mcu);
                if (!fast)
                {
                    r = start;
                    for (int i = 0; i < comps.Length; i++)
                    {
                        comps[i].DcPredictor = predictors[i];
                    }
                }
            }

            if (!fast)
            {
                Array.Clear(mcu);
                int index = 0;
                foreach (JpegComponent c in comps)
                {
                    for (int b = single ? 1 : c.H * c.V; b > 0; b--, index += 64)
                    {
                        DecodeBlockSequential(ref r, c, mcu.AsSpan(index, 64));
                    }
                }
            }

            // Full-size blocks are transformed in pairs, which the vector IDCT handles at once.
            int offset = 0;
            int pending = -1, pendingOutput = 0;
            JpegComponent? pendingComponent = null;
            foreach (JpegComponent c in comps)
            {
                int h = single ? 1 : c.H;
                int v = single ? 1 : c.V;
                for (int by = 0; by < v; by++)
                {
                    for (int bx = 0; bx < h; bx++, offset += 64)
                    {
                        int blockX = (mx * h) + bx;
                        int blockY = (my * v) + by;
                        int size = c.BlockSize;
                        int output = (blockY * size * c.PlaneStride) + (blockX * size);
                        if (size != 8)
                        {
                            JpegIdct.Transform(mcu.AsSpan(offset, 64), c.Quant!, c.Plane.AsSpan(output), c.PlaneStride, size);
                        }
                        else if (pending < 0)
                        {
                            (pending, pendingOutput, pendingComponent) = (offset, output, c);
                        }
                        else
                        {
                            JpegIdct.Transform(mcu.AsSpan(pending, 64), pendingComponent!.Quant!, pendingComponent.Plane.AsSpan(pendingOutput),
                                pendingComponent.PlaneStride, mcu.AsSpan(offset, 64), c.Quant!, c.Plane.AsSpan(output), c.PlaneStride);
                            pending = -1;
                        }
                    }
                }
            }

            if (pending >= 0)
            {
                JpegIdct.Transform(mcu.AsSpan(pending, 64), pendingComponent!.Quant!, pendingComponent.Plane.AsSpan(pendingOutput),
                    pendingComponent.PlaneStride);
            }
        }

        // Blocks skipped after the data ran out stay at the plane's initial mid-gray.
        ForEachMcu(ref reader, frame, scan, DecodeMcu, cancellationToken);
    }

    private static bool DecodeMcuFast(ref JpegBitReader r, JpegComponent[] comps, bool single, short[] mcu)
    {
        int index = 0;
        foreach (JpegComponent c in comps)
        {
            for (int b = single ? 1 : c.H * c.V; b > 0; b--, index += 64)
            {
                if (!DecodeBlockFast(ref r, c, mcu.AsSpan(index, 64)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // port: libjpeg-turbo jdhuff.c decode_mcu_fast (one block), with short codes and their values from one lookup
    private static bool DecodeBlockFast(ref JpegBitReader r, JpegComponent c, Span<short> block)
    {
        if (!r.EnsureFast())
        {
            return false;
        }

        int s = r.DecodeFast(c.DcTable!);
        int diff = s == 0 ? 0 : r.ReceiveFast(s);
        int dc = c.DcPredictor + diff;
        c.DcPredictor = dc;
        block[0] = (short)dc;

        JpegHuffmanTable ac = c.AcTable!;
        int[] fastAc = ac.FastAc;
        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        for (int k = 1; k < 64; k++)
        {
            if (!r.EnsureFast())
            {
                return false;
            }

            int entry = fastAc[r.Peek];
            if (entry != 0)
            {
                r.Skip(entry & 0xFF);
                k += (entry >> 8) & 0xFF;
                block[natural[k]] = (short)(entry >> 16);
                continue;
            }

            s = r.DecodeFast(ac);
            int run = s >> 4;
            s &= 15;
            if (s != 0)
            {
                k += run;
                block[natural[k]] = (short)r.ReceiveFast(s);
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

        return true;
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

    private static void DecodeProgressive(ref JpegBitReader reader, JpegFrame frame, JpegScan scan, CancellationToken cancellationToken)
    {
        JpegComponent[] comps = scan.Components;
        bool single = comps.Length == 1;
        int ss = scan.Ss, se = scan.Se, al = scan.Al;
        bool dc = ss == 0;
        bool first = scan.Ah == 0;
        short[] saved = new short[64];

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
                        else
                        {
                            // As in sequential scans, the fast path runs away from markers and the checked path decodes
                            // the block again from its start if the data runs short.
                            if (!r.AtMarker)
                            {
                                JpegBitReader start = r;
                                int startRun = eobRun;
                                if (!first)
                                {
                                    block.CopyTo(saved);
                                }

                                if (first ? DecodeAcFirstFast(ref r, c.AcTable!, block, ss, se, al, ref eobRun)
                                          : DecodeAcRefineFast(ref r, c.AcTable!, block, ss, se, al, ref eobRun))
                                {
                                    continue;
                                }

                                r = start;
                                eobRun = startRun;
                                if (!first)
                                {
                                    saved.CopyTo(block);
                                }
                            }

                            if (first)
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
        }

        ForEachMcu(ref reader, frame, scan, DecodeMcu, cancellationToken);
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

    // port: libjpeg-turbo jdphuff.c decode_mcu_AC_first, after EnsureFast checks; false when the data ran short
    private static bool DecodeAcFirstFast(ref JpegBitReader r, JpegHuffmanTable table, Span<short> block, int ss, int se, int al,
                                          ref int eobRun)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return true;
        }

        int[] fastAc = table.FastAc;
        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        for (int k = ss; k <= se; k++)
        {
            if (!r.EnsureFast())
            {
                return false;
            }

            int entry = fastAc[r.Peek];
            if (entry != 0)
            {
                r.Skip(entry & 0xFF);
                k += (entry >> 8) & 0xFF;
                block[natural[k]] = (short)((entry >> 16) << al);
                continue;
            }

            int s = r.DecodeFast(table);
            int run = s >> 4;
            s &= 15;
            if (s != 0)
            {
                k += run;
                block[natural[k]] = (short)(r.ReceiveFast(s) << al);
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
                    eobRun += r.GetBitsFast(run);
                }

                eobRun--;
                break;
            }
        }

        return true;
    }

    // port: libjpeg-turbo jdphuff.c decode_mcu_AC_refine, after EnsureFast checks; false when the data ran short.
    // The coefficients that were already non-zero are found from a bit mask instead of a test at every position.
    private static bool DecodeAcRefineFast(ref JpegBitReader r, JpegHuffmanTable table, Span<short> block, int ss, int se, int al,
                                           ref int eobRun)
    {
        int p1 = 1 << al;
        int m1 = -1 << al;
        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        ulong nonZero = ZigzagNonZeroMask(block);
        ulong upTo = se == 63 ? ulong.MaxValue : (2UL << se) - 1;
        int k = ss;
        if (eobRun == 0)
        {
            for (; k <= se; k++)
            {
                if (!r.EnsureFast())
                {
                    return false;
                }

                int s = r.DecodeFast(table);
                int run = s >> 4;
                s &= 15;
                if (s != 0)
                {
                    s = r.GetBitsFast(1) != 0 ? p1 : m1;
                }
                else if (run != 15)
                {
                    eobRun = 1 << run;
                    if (run != 0)
                    {
                        eobRun += r.GetBitsFast(run);
                    }

                    break;
                }

                // Stop at the zero coefficient after 'run' others (or past Se), refining the non-zero ones before it.
                ulong band = upTo & (ulong.MaxValue << k);
                ulong zeros = ~nonZero & band;
                for (; run > 0 && zeros != 0; run--)
                {
                    zeros &= zeros - 1;
                }

                int stop = zeros == 0 ? se + 1 : BitOperations.TrailingZeroCount(zeros);
                if (!RefineAll(ref r, block, nonZero & band & (stop == 64 ? ulong.MaxValue : (1UL << stop) - 1), al))
                {
                    return false;
                }

                k = stop;
                if (s != 0)
                {
                    block[natural[k]] = (short)s;
                }
            }
        }

        if (eobRun > 0)
        {
            if (k <= se && !RefineAll(ref r, block, nonZero & upTo & (ulong.MaxValue << k), al))
            {
                return false;
            }

            eobRun--;
        }

        return true;
    }

    // Appends a correction bit to each coefficient in 'positions' (zigzag bits), in order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RefineAll(ref JpegBitReader r, Span<short> block, ulong positions, int al)
    {
        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        while (positions != 0)
        {
            if (!r.EnsureFast())
            {
                return false;
            }

            RefineFast(ref r, ref block[natural[BitOperations.TrailingZeroCount(positions)]], al);
            positions &= positions - 1;
        }

        return true;
    }

    // Bit k is set when the coefficient at zigzag index k is not zero.
    private static ulong ZigzagNonZeroMask(ReadOnlySpan<short> block)
    {
        ulong naturalMask = JpegScanEncoder.NonZeroMask(block);
        ReadOnlySpan<byte> zigzag = JpegTables.ZigzagOrder;
        ulong mask = 0;
        while (naturalMask != 0)
        {
            mask |= 1UL << zigzag[BitOperations.TrailingZeroCount(naturalMask)];
            naturalMask &= naturalMask - 1;
        }

        return mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RefineFast(ref JpegBitReader r, ref short coef, int al)
    {
        // Branch-free form of Refine: the correction bits are close to random.
        int c = coef;
        int sign = c >> 31;
        int apply = -(r.GetBitsFast(1) & ~(c >> al));
        coef = (short)(c + ((((1 << al) ^ sign) - sign) & apply));
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
