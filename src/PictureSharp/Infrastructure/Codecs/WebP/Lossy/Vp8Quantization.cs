namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal static class Vp8Quantization
{
    // port: src/enc/quant_enc.c VP8SetSegmentParams, QualityToCompression, SetupFilterStrength
    public static void SetSegmentParams(Vp8EncoderContext context, float quality)
    {
        double amplitude = .9 * context.Config.SnsStrength / 100.0 / 128.0;
        double q = quality / 100.0;
        double cBase = Math.Pow(q < .75 ? q * (2.0 / 3.0) : 2 * q - 1, 1.0 / 3.0);
        for (int i = 0; i < context.SegmentCount; i++)
            context.Segments[i].Quant = Math.Clamp((int)(127 * (1 - Math.Pow(cBase, 1 - amplitude * context.Segments[i].Alpha))), 0, 127);
        context.BaseQuant = context.Segments[0].Quant;
        for (int i = context.SegmentCount; i < 4; i++) context.Segments[i].Quant = context.BaseQuant;
        context.DeltaUvAc = Math.Clamp(((context.UvAlpha - 64) * 10 / 70) * context.Config.SnsStrength / 100, -4, 6);
        context.DeltaUvDc = Math.Clamp(-4 * context.Config.SnsStrength / 100, -15, 15);
        for (int i = 0; i < 4; i++)
        {
            Vp8Segment segment = context.Segments[i];
            int strength = FilterStrengthFromDelta(context.FilterSharpness, Vp8Tables.AcTable[segment.Quant] >> 2);
            int f = strength * 5 * context.Config.FilterStrength / (256 + segment.Beta);
            segment.FilterStrength = f < 2 ? 0 : Math.Min(f, 63);
        }
        context.FilterLevel = context.Segments[0].FilterStrength;
        context.FilterSharpness = context.Config.FilterSharpness;
        if (context.SegmentCount > 1) SimplifySegments(context);
        SetupMatrices(context);
    }

    // port: src/enc/quant_enc.c SimplifySegments
    private static void SimplifySegments(Vp8EncoderContext context)
    {
        Span<int> map = stackalloc int[4]; map[0] = 0;
        int count = context.SegmentCount, final = 1;
        for (int i = 1; i < count; i++)
        {
            Vp8Segment segment = context.Segments[i];
            int j = 0;
            for (; j < final; j++) if (segment.Quant == context.Segments[j].Quant && segment.FilterStrength == context.Segments[j].FilterStrength) break;
            map[i] = j;
            if (j == final)
            {
                if (i != final) (context.Segments[final], context.Segments[i]) = (context.Segments[i], context.Segments[final]);
                final++;
            }
        }
        if (final == count) return;
        for (int i = 0; i < context.MbCount; i++) context.Blocks[i].Segment = (byte)map[context.Blocks[i].Segment];
        context.SegmentCount = final;
        for (int i = final; i < count; i++)
        {
            Vp8Segment last = context.Segments[final - 1], target = context.Segments[i];
            target.Alpha = last.Alpha; target.Beta = last.Beta; target.Quant = last.Quant; target.FilterStrength = last.FilterStrength;
        }
    }

    // port: src/enc/quant_enc.c SetupMatrices
    private static void SetupMatrices(Vp8EncoderContext context)
    {
        for (int i = 0; i < context.SegmentCount; i++)
        {
            Vp8Segment m = context.Segments[i]; int q = m.Quant;
            int qi4 = m.Y1.Expand(Vp8Tables.DcTable[q], Vp8Tables.AcTable[q], 0);
            int qi16 = m.Y2.Expand(2 * Vp8Tables.DcTable[q], AcTable2[q], 1);
            int quv = m.Uv.Expand(Vp8Tables.DcTable[Math.Clamp(q + context.DeltaUvDc, 0, 117)], Vp8Tables.AcTable[Math.Clamp(q + context.DeltaUvAc, 0, 127)], 2);
            m.LambdaI4 = Math.Max(1, 3 * qi4 * qi4 >> 7);
            m.LambdaI16 = Math.Max(1, 3 * qi16 * qi16);
            m.LambdaUv = Math.Max(1, 3 * quv * quv >> 6);
            m.LambdaMode = Math.Max(1, qi4 * qi4 >> 7);
            m.LambdaTrellisI4 = Math.Max(1, 7 * qi4 * qi4 >> 3);
            m.LambdaTrellisI16 = Math.Max(1, qi16 * qi16 >> 2);
            m.LambdaTrellisUv = Math.Max(1, quv * quv << 1);
            m.TLambda = Math.Max(1, ((context.Config.Options.Method >= 4 ? context.Config.SnsStrength : 0) * qi4) >> 5);
            m.MinDisto = 20 * m.Y1.Q[0]; m.MaxEdge = 0;
            m.I4Penalty = 1000 * qi4 * qi4;
        }
    }

    // port: src/enc/filter_enc.c VP8FilterStrengthFromDelta
    public static int FilterStrengthFromDelta(int sharpness, int delta) => LevelsFromDelta[sharpness * 64 + Math.Min(delta, 63)];

    // port: src/enc/quant_enc.c kAcTable2
    private static ReadOnlySpan<ushort> AcTable2 => [

  8,     8,   9,  10,  12,  13,  15,  17,
  18,   20,  21,  23,  24,  26,  27,  29,
  31,   32,  34,  35,  37,  38,  40,  41,
  43,   44,  46,  48,  49,  51,  52,  54,
  55,   57,  58,  60,  62,  63,  65,  66,
  68,   69,  71,  72,  74,  75,  77,  79,
  80,   82,  83,  85,  86,  88,  89,  93,
  96,   99, 102, 105, 108, 111, 114, 117,
  120, 124, 127, 130, 133, 136, 139, 142,
  145, 148, 151, 155, 158, 161, 164, 167,
  170, 173, 176, 179, 184, 189, 193, 198,
  203, 207, 212, 217, 221, 226, 230, 235,
  240, 244, 249, 254, 258, 263, 268, 274,
  280, 286, 292, 299, 305, 311, 317, 323,
  330, 336, 342, 348, 354, 362, 370, 379,
  385, 393, 401, 409, 416, 424, 432, 440
    ];

    // port: src/enc/filter_enc.c kLevelsFromDelta
    private static ReadOnlySpan<byte> LevelsFromDelta => [

   0,   1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
    16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
    32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
    48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63 ,
   0,  1,  2,  3,  5,  6,  7,  8,  9, 11, 12, 13, 14, 15, 17, 18,
    20, 21, 23, 24, 26, 27, 29, 30, 32, 33, 35, 36, 38, 39, 41, 42,
    44, 45, 47, 48, 50, 51, 53, 54, 56, 57, 59, 60, 62, 63, 63, 63,
    63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63 ,
    0,  1,  2,  3,  5,  6,  7,  8,  9, 11, 12, 13, 14, 16, 17, 19,
    20, 22, 23, 25, 26, 28, 29, 31, 32, 34, 35, 37, 38, 40, 41, 43,
    44, 46, 47, 49, 50, 52, 53, 55, 56, 58, 59, 61, 62, 63, 63, 63,
    63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63 ,
    0,  1,  2,  3,  5,  6,  7,  8,  9, 11, 12, 13, 15, 16, 18, 19,
    21, 22, 24, 25, 27, 28, 30, 31, 33, 34, 36, 37, 39, 40, 42, 43,
    45, 46, 48, 49, 51, 52, 54, 55, 57, 58, 60, 61, 63, 63, 63, 63,
    63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63 ,
    0,  1,  2,  3,  5,  6,  7,  8,  9, 11, 12, 14, 15, 17, 18, 20,
    21, 23, 24, 26, 27, 29, 30, 32, 33, 35, 36, 38, 39, 41, 42, 44,
    45, 47, 48, 50, 51, 53, 54, 56, 57, 59, 60, 62, 63, 63, 63, 63,
    63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63 ,
    0,  1,  2,  4,  5,  7,  8,  9, 11, 12, 13, 15, 16, 17, 19, 20,
    22, 23, 25, 26, 28, 29, 31, 32, 34, 35, 37, 38, 40, 41, 43, 44,
    46, 47, 49, 50, 52, 53, 55, 56, 58, 59, 61, 62, 63, 63, 63, 63,
    63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63 ,
    0,  1,  2,  4,  5,  7,  8,  9, 11, 12, 13, 15, 16, 18, 19, 21,
    22, 24, 25, 27, 28, 30, 31, 33, 34, 36, 37, 39, 40, 42, 43, 45,
    46, 48, 49, 51, 52, 54, 55, 57, 58, 60, 61, 63, 63, 63, 63, 63,
    63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63 ,
    0,  1,  2,  4,  5,  7,  8,  9, 11, 12, 14, 15, 17, 18, 20, 21,
    23, 24, 26, 27, 29, 30, 32, 33, 35, 36, 38, 39, 41, 42, 44, 45,
    47, 48, 50, 51, 53, 54, 56, 57, 59, 60, 62, 63, 63, 63, 63, 63,
    63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63
    ];
}
