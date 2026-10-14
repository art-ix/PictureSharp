namespace RasterSharp.Formats.Jpeg;

/// <summary>Accurate integer inverse DCT (libjpeg's "islow"), bit-exact with libjpeg-turbo.</summary>
internal static partial class JpegIdct
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;

    private const long Fix0298631336 = 2446;
    private const long Fix0390180644 = 3196;
    private const long Fix0541196100 = 4433;
    private const long Fix0765366865 = 6270;
    private const long Fix0899976223 = 7373;
    private const long Fix1175875602 = 9633;
    private const long Fix1501321110 = 12299;
    private const long Fix1847759065 = 15137;
    private const long Fix1961570560 = 16069;
    private const long Fix2053119869 = 16819;
    private const long Fix2562915447 = 20995;
    private const long Fix3072711026 = 25172;

    /// <summary>
    /// Post-IDCT range limiting indexed by (value &amp; 1023), including the level shift by 128. Values
    /// far outside the sample range wrap around exactly like libjpeg's table does.
    /// </summary>
    // port: libjpeg-turbo jdmaster.c prepare_range_limit_table (IDCT_range_limit)
    private static readonly byte[] RangeLimit = BuildRangeLimit();

    private static byte[] BuildRangeLimit()
    {
        var table = new byte[1024];
        for (int i = 0; i < 1024; i++)
        {
            table[i] = i switch
            {
                < 128 => (byte)(i + 128),
                < 512 => 255,
                < 896 => 0,
                _ => (byte)(i - 896),
            };
        }

        return table;
    }

    private static long Descale(long x, int n) => (x + (1L << (n - 1))) >> n;

    /// <summary>Dequantizes a block (natural order) and writes the 8x8 samples to <paramref name="output"/>.</summary>
    public static void Transform(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output, int stride)
    {
        if (!TransformSimd(block, quant, output, stride))
        {
            TransformScalar(block, quant, output, stride);
        }
    }

    // port: libjpeg-turbo jidctint.c jpeg_idct_islow
    internal static void TransformScalar(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output, int stride)
    {
        Span<int> ws = stackalloc int[64];
        byte[] range = RangeLimit;

        // Pass 1: columns from input into the work array, scaled up by sqrt(8) * 2^Pass1Bits.
        for (int c = 0; c < 8; c++)
        {
            if (block[8 + c] == 0 && block[16 + c] == 0 && block[24 + c] == 0 && block[32 + c] == 0
                && block[40 + c] == 0 && block[48 + c] == 0 && block[56 + c] == 0)
            {
                int dc = block[c] * quant[c] << Pass1Bits;
                for (int r = 0; r < 64; r += 8)
                {
                    ws[r + c] = dc;
                }

                continue;
            }

            long z2 = block[16 + c] * quant[16 + c];
            long z3 = block[48 + c] * quant[48 + c];
            long z1 = (z2 + z3) * Fix0541196100;
            long tmp2 = z1 + (z3 * -Fix1847759065);
            long tmp3 = z1 + (z2 * Fix0765366865);

            z2 = block[c] * quant[c];
            z3 = block[32 + c] * quant[32 + c];
            long tmp0 = (z2 + z3) << ConstBits;
            long tmp1 = (z2 - z3) << ConstBits;

            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;

            tmp0 = block[56 + c] * quant[56 + c];
            tmp1 = block[40 + c] * quant[40 + c];
            tmp2 = block[24 + c] * quant[24 + c];
            tmp3 = block[8 + c] * quant[8 + c];
            OddPart(ref tmp0, ref tmp1, ref tmp2, ref tmp3);

            ws[c] = (int)Descale(tmp10 + tmp3, ConstBits - Pass1Bits);
            ws[56 + c] = (int)Descale(tmp10 - tmp3, ConstBits - Pass1Bits);
            ws[8 + c] = (int)Descale(tmp11 + tmp2, ConstBits - Pass1Bits);
            ws[48 + c] = (int)Descale(tmp11 - tmp2, ConstBits - Pass1Bits);
            ws[16 + c] = (int)Descale(tmp12 + tmp1, ConstBits - Pass1Bits);
            ws[40 + c] = (int)Descale(tmp12 - tmp1, ConstBits - Pass1Bits);
            ws[24 + c] = (int)Descale(tmp13 + tmp0, ConstBits - Pass1Bits);
            ws[32 + c] = (int)Descale(tmp13 - tmp0, ConstBits - Pass1Bits);
        }

        // Pass 2: rows from the work array into the output, undoing the scaling and level-shifting by 128.
        const int Shift = ConstBits + Pass1Bits + 3;
        for (int r = 0; r < 8; r++)
        {
            ReadOnlySpan<int> w = ws.Slice(r * 8, 8);
            Span<byte> o = output.Slice(r * stride, 8);
            if (w[1] == 0 && w[2] == 0 && w[3] == 0 && w[4] == 0 && w[5] == 0 && w[6] == 0 && w[7] == 0)
            {
                o.Fill(range[(int)Descale(w[0], Pass1Bits + 3) & 1023]);
                continue;
            }

            long z2 = w[2];
            long z3 = w[6];
            long z1 = (z2 + z3) * Fix0541196100;
            long tmp2 = z1 + (z3 * -Fix1847759065);
            long tmp3 = z1 + (z2 * Fix0765366865);

            long tmp0 = ((long)w[0] + w[4]) << ConstBits;
            long tmp1 = ((long)w[0] - w[4]) << ConstBits;

            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;

            tmp0 = w[7];
            tmp1 = w[5];
            tmp2 = w[3];
            tmp3 = w[1];
            OddPart(ref tmp0, ref tmp1, ref tmp2, ref tmp3);

            o[0] = range[(int)Descale(tmp10 + tmp3, Shift) & 1023];
            o[7] = range[(int)Descale(tmp10 - tmp3, Shift) & 1023];
            o[1] = range[(int)Descale(tmp11 + tmp2, Shift) & 1023];
            o[6] = range[(int)Descale(tmp11 - tmp2, Shift) & 1023];
            o[2] = range[(int)Descale(tmp12 + tmp1, Shift) & 1023];
            o[5] = range[(int)Descale(tmp12 - tmp1, Shift) & 1023];
            o[3] = range[(int)Descale(tmp13 + tmp0, Shift) & 1023];
            o[4] = range[(int)Descale(tmp13 - tmp0, Shift) & 1023];
        }
    }

    /// <summary>Odd part per figure 8 of the IJG notes; on entry tmp0..tmp3 are y7, y5, y3, y1.</summary>
    private static void OddPart(ref long tmp0, ref long tmp1, ref long tmp2, ref long tmp3)
    {
        long z1 = tmp0 + tmp3;
        long z2 = tmp1 + tmp2;
        long z3 = tmp0 + tmp2;
        long z4 = tmp1 + tmp3;
        long z5 = (z3 + z4) * Fix1175875602;

        tmp0 *= Fix0298631336;
        tmp1 *= Fix2053119869;
        tmp2 *= Fix3072711026;
        tmp3 *= Fix1501321110;
        z1 *= -Fix0899976223;
        z2 *= -Fix2562915447;
        z3 *= -Fix1961570560;
        z4 *= -Fix0390180644;

        z3 += z5;
        z4 += z5;

        tmp0 += z1 + z3;
        tmp1 += z2 + z4;
        tmp2 += z2 + z3;
        tmp3 += z1 + z4;
    }
}
