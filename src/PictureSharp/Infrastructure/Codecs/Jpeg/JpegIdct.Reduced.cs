namespace PictureSharp.Infrastructure.Codecs.Jpeg;

/// <summary>Reduced-size inverse DCTs producing 4x4, 2x2 or 1x1 samples, bit-exact with libjpeg-turbo.</summary>
internal static partial class JpegIdct
{
    private const long Fix0211164243 = 1730;
    private const long Fix0509795579 = 4176;
    private const long Fix0601344887 = 4926;
    private const long Fix0720959822 = 5906;
    private const long Fix0850430095 = 6967;
    private const long Fix1061594337 = 8697;
    private const long Fix1272758580 = 10426;
    private const long Fix1451774981 = 11893;
    private const long Fix2172734803 = 17799;
    private const long Fix3624509785 = 29692;

    /// <summary>Dequantizes a block and writes <paramref name="size"/>×<paramref name="size"/> samples (8, 4, 2 or 1).</summary>
    public static void Transform(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output, int stride, int size)
    {
        switch (size)
        {
            case 8:
                Transform(block, quant, output, stride);
                break;
            case 4:
                Transform4x4(block, quant, output, stride);
                break;
            case 2:
                Transform2x2(block, quant, output, stride);
                break;
            default:
                Transform1x1(block, quant, output);
                break;
        }
    }

    // port: libjpeg-turbo jidctred.c jpeg_idct_4x4
    internal static void Transform4x4(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output, int stride)
    {
        Span<int> ws = stackalloc int[8 * 4];
        byte[] range = RangeLimit;

        // Pass 1: columns from input into the work array; column 4 is not needed by the second pass.
        for (int c = 0; c < 8; c++)
        {
            if (c == 4)
            {
                continue;
            }

            if (block[8 + c] == 0 && block[16 + c] == 0 && block[24 + c] == 0
                && block[40 + c] == 0 && block[48 + c] == 0 && block[56 + c] == 0)
            {
                // AC terms all zero; term 4 does not contribute to 4x4 output.
                int dc = block[c] * quant[c] << Pass1Bits;
                ws[c] = dc;
                ws[8 + c] = dc;
                ws[16 + c] = dc;
                ws[24 + c] = dc;
                continue;
            }

            // Even part
            long tmp0 = (long)(block[c] * quant[c]) << (ConstBits + 1);
            long z2 = block[16 + c] * quant[16 + c];
            long z3 = block[48 + c] * quant[48 + c];
            long tmp2 = (z2 * Fix1847759065) + (z3 * -Fix0765366865);
            long tmp10 = tmp0 + tmp2;
            long tmp12 = tmp0 - tmp2;

            // Odd part
            (tmp0, tmp2) = OddPart4(block[56 + c] * quant[56 + c], block[40 + c] * quant[40 + c],
                block[24 + c] * quant[24 + c], block[8 + c] * quant[8 + c]);

            const int Shift1 = ConstBits - Pass1Bits + 1;
            ws[c] = (int)Descale(tmp10 + tmp2, Shift1);
            ws[24 + c] = (int)Descale(tmp10 - tmp2, Shift1);
            ws[8 + c] = (int)Descale(tmp12 + tmp0, Shift1);
            ws[16 + c] = (int)Descale(tmp12 - tmp0, Shift1);
        }

        // Pass 2: 4 rows from the work array into the output.
        const int Shift2 = ConstBits + Pass1Bits + 3 + 1;
        for (int r = 0; r < 4; r++)
        {
            ReadOnlySpan<int> w = ws.Slice(r * 8, 8);
            Span<byte> o = output.Slice(r * stride, 4);
            if (w[1] == 0 && w[2] == 0 && w[3] == 0 && w[5] == 0 && w[6] == 0 && w[7] == 0)
            {
                o.Fill(range[(int)Descale(w[0], Pass1Bits + 3) & 1023]);
                continue;
            }

            long tmp0 = (long)w[0] << (ConstBits + 1);
            long tmp2 = (w[2] * Fix1847759065) + (w[6] * -Fix0765366865);
            long tmp10 = tmp0 + tmp2;
            long tmp12 = tmp0 - tmp2;

            (tmp0, tmp2) = OddPart4(w[7], w[5], w[3], w[1]);

            o[0] = range[(int)Descale(tmp10 + tmp2, Shift2) & 1023];
            o[3] = range[(int)Descale(tmp10 - tmp2, Shift2) & 1023];
            o[1] = range[(int)Descale(tmp12 + tmp0, Shift2) & 1023];
            o[2] = range[(int)Descale(tmp12 - tmp0, Shift2) & 1023];
        }
    }

    /// <summary>Odd part of the 4-point output; inputs are the terms 7, 5, 3 and 1.</summary>
    private static (long Tmp0, long Tmp2) OddPart4(long z1, long z2, long z3, long z4) =>
        ((z1 * -Fix0211164243) + (z2 * Fix1451774981) + (z3 * -Fix2172734803) + (z4 * Fix1061594337),
         (z1 * -Fix0509795579) + (z2 * -Fix0601344887) + (z3 * Fix0899976223) + (z4 * Fix2562915447));

    // port: libjpeg-turbo jidctred.c jpeg_idct_2x2
    internal static void Transform2x2(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output, int stride)
    {
        Span<int> ws = stackalloc int[8 * 2];
        byte[] range = RangeLimit;

        // Pass 1: columns from input; columns 2, 4 and 6 are not needed by the second pass.
        for (int c = 0; c < 8; c++)
        {
            if (c is 2 or 4 or 6)
            {
                continue;
            }

            if (block[8 + c] == 0 && block[24 + c] == 0 && block[40 + c] == 0 && block[56 + c] == 0)
            {
                // AC terms all zero; terms 2, 4 and 6 do not contribute to 2x2 output.
                int dc = block[c] * quant[c] << Pass1Bits;
                ws[c] = dc;
                ws[8 + c] = dc;
                continue;
            }

            long tmp10 = (long)(block[c] * quant[c]) << (ConstBits + 2);
            long tmp0 = OddPart2(block[56 + c] * quant[56 + c], block[40 + c] * quant[40 + c],
                block[24 + c] * quant[24 + c], block[8 + c] * quant[8 + c]);

            const int Shift1 = ConstBits - Pass1Bits + 2;
            ws[c] = (int)Descale(tmp10 + tmp0, Shift1);
            ws[8 + c] = (int)Descale(tmp10 - tmp0, Shift1);
        }

        // Pass 2: 2 rows from the work array into the output.
        const int Shift2 = ConstBits + Pass1Bits + 3 + 2;
        for (int r = 0; r < 2; r++)
        {
            ReadOnlySpan<int> w = ws.Slice(r * 8, 8);
            Span<byte> o = output.Slice(r * stride, 2);
            if (w[1] == 0 && w[3] == 0 && w[5] == 0 && w[7] == 0)
            {
                o.Fill(range[(int)Descale(w[0], Pass1Bits + 3) & 1023]);
                continue;
            }

            long tmp10 = (long)w[0] << (ConstBits + 2);
            long tmp0 = OddPart2(w[7], w[5], w[3], w[1]);
            o[0] = range[(int)Descale(tmp10 + tmp0, Shift2) & 1023];
            o[1] = range[(int)Descale(tmp10 - tmp0, Shift2) & 1023];
        }
    }

    /// <summary>Odd part of the 2-point output; inputs are the terms 7, 5, 3 and 1.</summary>
    private static long OddPart2(long z7, long z5, long z3, long z1) =>
        (z7 * -Fix0720959822) + (z5 * Fix0850430095) + (z3 * -Fix1272758580) + (z1 * Fix3624509785);

    // port: libjpeg-turbo jidctred.c jpeg_idct_1x1
    internal static void Transform1x1(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output) =>
        output[0] = RangeLimit[(int)Descale(block[0] * quant[0], 3) & 1023];
}
