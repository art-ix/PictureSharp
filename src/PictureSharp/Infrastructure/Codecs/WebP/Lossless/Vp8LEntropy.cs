using System.Numerics;
using System.Runtime.CompilerServices;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

internal static partial class Vp8LEntropy
{
    public const long One = 1L << 23;

    // port: src/dsp/lossless_common.h DivRound (libwebp 1.6.0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DivideRound(long a, long b) => ((a < 0) == (b < 0) ? a + b / 2 : a - b / 2) / b;

    // Most histogram counts are small; a table of the exact values avoids the poorly predicted branch below.
    public const int SLogCacheSize = 4096;
    private static readonly long[] SLogCache = BuildSLogCache();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SLog(uint v) => v < SLogCacheSize ? SLogCache[v] : SLogSlow(v);

    // SLog(min(v, SLogCacheSize - 1)): no call in loops that correct the few larger values afterwards.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SLogClamped(uint v) => SLogCache[(int)Math.Min(v, SLogCacheSize - 1u)];

    private static long[] BuildSLogCache()
    {
        var cache = new long[SLogCacheSize];
        for (uint v = 0; v < SLogCacheSize; v++) cache[v] = SLogSlow(v);
        return cache;
    }

    // port: src/dsp/lossless_enc.c VP8LFastSLog2, FastSLog2Slow_C, FastLog2Slow_C
    private static long SLogSlow(uint v)
    {
        if (v < 256) return SLogTable[(int)v];
        if (v >= 65536) return (long)(12102203.161561485379934310913085937500 * v * Math.Log(v) + 0.5);
        int shift = BitOperations.Log2(v) - 7;
        long correction = 12102203L * (v & ((1u << shift) - 1));
        return v * (LogTable[(int)(v >> shift)] + shift * One) + correction;
    }

    public static long Log(uint v)
    {
        if (v < 256) return LogTable[(int)v];
        if (v >= 65536) return (long)(12102203.161561485379934310913085937500 * Math.Log(v) + 0.5);
        int shift = BitOperations.Log2(v) - 7;
        long result = LogTable[(int)(v >> shift)] + shift * One;
        if (v >= 4096) result += DivideRound(12102203L * (v & ((1u << shift) - 1)), v);
        return result;
    }

    // port: src/dsp/lossless_enc.c CombinedShannonEntropy_C
    public static long Combined(ReadOnlySpan<uint> x, ReadOnlySpan<uint> y)
    {
        long result = 0;
        uint sumX = 0, sumXY = 0;
        for (int i = 0; i < 256; i++)
        {
            if (x[i] != 0) { sumX += x[i]; result -= SLog(x[i]); sumXY += x[i] + y[i]; result -= SLog(x[i] + y[i]); }
            else if (y[i] != 0) { sumXY += y[i]; result -= SLog(y[i]); }
        }
        return result + SLog(sumX) + SLog(sumXY);
    }

    // The terms of CombinedShannonEntropy that depend only on y: SLog of each count, their sum and the
    // population. Integer sums are exact, so Combined with them equals Combined(x, y) for every x.
    public static long PrepareCombined(ReadOnlySpan<uint> y, Span<long> sLogY, out uint sumY)
    {
        long total = 0;
        sumY = 0;
        for (int i = 0; i < 256; i++)
        {
            uint count = y[i];
            long value = SLog(count);
            sLogY[i] = value;
            total += value;
            sumY += count;
        }
        return total;
    }

    // Adds x to y and keeps the terms prepared by PrepareCombined up to date; only the non-zero entries of x change.
    public static void AddToCombined(ReadOnlySpan<uint> x, Span<uint> y, Span<long> sLogY, ref uint sumY, ref long sLogYTotal)
    {
        x = x[..256]; y = y[..256]; sLogY = sLogY[..256];
        for (int block = 0; block < 256; block += 32)
        {
            for (uint mask = NonZeroMask32(x.Slice(block, 32)); mask != 0; mask &= mask - 1)
            {
                int i = block + BitOperations.TrailingZeroCount(mask);
                uint count = x[i];
                y[i] += count;
                sumY += count;
                long value = SLog(y[i]);
                sLogYTotal += value - sLogY[i];
                sLogY[i] = value;
            }
        }
    }

    // CombinedShannonEntropy_C rewritten over the non-zero entries of x only:
    // sum(x == 0) SLog(y) = sLogYTotal - sum(x != 0) SLog(y), and sumXY = sumX + sumY.
    public static long Combined(ReadOnlySpan<uint> x, ReadOnlySpan<uint> y, ReadOnlySpan<long> sLogY, uint sumY, long sLogYTotal)
    {
        x = x[..256]; y = y[..256]; sLogY = sLogY[..256];
        long result = 0;
        uint sumX = 0;
        for (int block = 0; block < 256; block += 32)
        {
            for (uint mask = NonZeroMask32(x.Slice(block, 32)); mask != 0; mask &= mask - 1)
            {
                int i = block + BitOperations.TrailingZeroCount(mask);
                uint count = x[i];
                sumX += count;
                result += SLog(count) + SLog(count + y[i]) - sLogY[i];
            }
        }
        return SLog(sumX) + SLog(sumX + sumY) - result - sLogYTotal;
    }

    // port: src/enc/predictor_enc.c PredictionCostBias
    // The exponents depend only on the initial value, so the two used ones are precomputed.
    public static long Spatial(ReadOnlySpan<uint> counts, int weight, int exponent)
    {
        ReadOnlySpan<long> exp = exponent == 94 ? Exponents94 : exponent == 240 ? Exponents240 : SpatialExponents(exponent);
        counts = counts[..256];
        long bits = weight * (long)counts[0] * One;
        for (int i = 1; i < 16; i++) bits += DivideRound(exp[i] * (counts[i] + counts[256 - i]), 100);
        return -DivideRound(bits, 10);
    }

    private static readonly long[] Exponents94 = SpatialExponents(94), Exponents240 = SpatialExponents(240);

    private static long[] SpatialExponents(int exponent)
    {
        var result = new long[16];
        long exp = exponent * One;
        for (int i = 1; i < 16; i++)
        {
            result[i] = exp;
            exp = DivideRound(6 * exp, 10);
        }
        return result;
    }

    // port: src/enc/histogram_enc.c VP8LBitsEntropy; src/dsp/lossless_enc.c VP8LBitsEntropyUnrefined
    public static long Bits(ReadOnlySpan<uint> counts)
    {
        uint sum = 0, max = 0;
        int nonzero = 0;
        long entropy = 0;
        foreach (uint count in counts)
        {
            if (count == 0) continue;
            sum += count; nonzero++; entropy -= SLog(count);
            if (max < count) max = count;
        }
        return Refine(sum, max, nonzero, entropy);
    }

    // port: src/dsp/lossless_enc.c ShannonEntropy_C
    public static long Shannon(ReadOnlySpan<uint> counts)
    {
        long result = 0;
        uint sum = 0;
        foreach (uint count in counts)
        {
            if (count == 0) continue;
            sum += count; result += SLog(count);
        }
        return SLog(sum) - result;
    }

    // port: src/enc/histogram_enc.c BitsEntropyRefine
    // Accumulated sum, maximum, non-zero count and -sum(SLog(count)) of a population.
    public static long Refine(uint sum, uint max, int nonzero, long entropy)
    {
        if (nonzero <= 1) return 0;
        entropy += SLog(sum);
        if (nonzero == 2) return DivideRound(99L * sum * One + entropy, 100);
        int mix = nonzero == 3 ? 950 : nonzero == 4 ? 700 : 627;
        long minimum = (2L * sum - max) * One;
        return Math.Max(entropy, DivideRound(mix * minimum + (1000 - mix) * entropy, 1000));
    }

    // port: src/dsp/lossless_enc.c kLog2Table, kSLog2Table
    private static ReadOnlySpan<uint> LogTable => [
         0,        0,  8388608, 13295629, 16777216, 19477745, 21684237,
  23549800, 25165824, 26591258, 27866353, 29019816, 30072845, 31041538,
  31938408, 32773374, 33554432, 34288123, 34979866, 35634199, 36254961,
  36845429, 37408424, 37946388, 38461453, 38955489, 39430146, 39886887,
  40327016, 40751698, 41161982, 41558811, 41943040, 42315445, 42676731,
  43027545, 43368474, 43700062, 44022807, 44337167, 44643569, 44942404,
  45234037, 45518808, 45797032, 46069003, 46334996, 46595268, 46850061,
  47099600, 47344097, 47583753, 47818754, 48049279, 48275495, 48497560,
  48715624, 48929828, 49140306, 49347187, 49550590, 49750631, 49947419,
  50141058, 50331648, 50519283, 50704053, 50886044, 51065339, 51242017,
  51416153, 51587818, 51757082, 51924012, 52088670, 52251118, 52411415,
  52569616, 52725775, 52879946, 53032177, 53182516, 53331012, 53477707,
  53622645, 53765868, 53907416, 54047327, 54185640, 54322389, 54457611,
  54591338, 54723604, 54854440, 54983876, 55111943, 55238669, 55364082,
  55488208, 55611074, 55732705, 55853126, 55972361, 56090432, 56207362,
  56323174, 56437887, 56551524, 56664103, 56775645, 56886168, 56995691,
  57104232, 57211808, 57318436, 57424133, 57528914, 57632796, 57735795,
  57837923, 57939198, 58039632, 58139239, 58238033, 58336027, 58433234,
  58529666, 58625336, 58720256, 58814437, 58907891, 59000628, 59092661,
  59183999, 59274652, 59364632, 59453947, 59542609, 59630625, 59718006,
  59804761, 59890898, 59976426, 60061354, 60145690, 60229443, 60312620,
  60395229, 60477278, 60558775, 60639726, 60720140, 60800023, 60879382,
  60958224, 61036555, 61114383, 61191714, 61268554, 61344908, 61420785,
  61496188, 61571124, 61645600, 61719620, 61793189, 61866315, 61939001,
  62011253, 62083076, 62154476, 62225457, 62296024, 62366182, 62435935,
  62505289, 62574248, 62642816, 62710997, 62778797, 62846219, 62913267,
  62979946, 63046260, 63112212, 63177807, 63243048, 63307939, 63372484,
  63436687, 63500551, 63564080, 63627277, 63690146, 63752690, 63814912,
  63876816, 63938405, 63999682, 64060650, 64121313, 64181673, 64241734,
  64301498, 64360969, 64420148, 64479040, 64537646, 64595970, 64654014,
  64711782, 64769274, 64826495, 64883447, 64940132, 64996553, 65052711,
  65108611, 65164253, 65219641, 65274776, 65329662, 65384299, 65438691,
  65492840, 65546747, 65600416, 65653847, 65707044, 65760008, 65812741,
  65865245, 65917522, 65969575, 66021404, 66073013, 66124403, 66175575,
  66226531, 66277275, 66327806, 66378127, 66428240, 66478146, 66527847,
  66577345, 66626641, 66675737, 66724635, 66773336, 66821842, 66870154,
  66918274, 66966204, 67013944, 67061497
];

    private static ReadOnlySpan<long> SLogTable => [
               0,              0,       16777216,       39886887,
        67108864,       97388723,      130105423,      164848600,
       201326592,      239321324,      278663526,      319217973,
       360874141,      403539997,      447137711,      491600606,
       536870912,      582898099,      629637592,      677049776,
       725099212,      773754010,      822985323,      872766924,
       923074875,      973887230,     1025183802,     1076945958,
      1129156447,     1181799249,     1234859451,     1288323135,
      1342177280,     1396409681,     1451008871,     1505964059,
      1561265072,     1616902301,     1672866655,     1729149526,
      1785742744,     1842638548,     1899829557,     1957308741,
      2015069397,     2073105127,     2131409817,  2189977618L,
   2248802933L,  2307880396L,  2367204859L,  2426771383L,
   2486575220L,  2546611805L,  2606876748L,  2667365819L,
   2728074942L,  2789000187L,  2850137762L,  2911484006L,
   2973035382L,  3034788471L,  3096739966L,  3158886666L,
   3221225472L,  3283753383L,  3346467489L,  3409364969L,
   3472443085L,  3535699182L,  3599130679L,  3662735070L,
   3726509920L,  3790452862L,  3854561593L,  3918833872L,
   3983267519L,  4047860410L,  4112610476L,  4177515704L,
   4242574127L,  4307783833L,  4373142952L,  4438649662L,
   4504302186L,  4570098787L,  4636037770L,  4702117480L,
   4768336298L,  4834692645L,  4901184974L,  4967811774L,
   5034571569L,  5101462912L,  5168484389L,  5235634615L,
   5302912235L,  5370315922L,  5437844376L,  5505496324L,
   5573270518L,  5641165737L,  5709180782L,  5777314477L,
   5845565671L,  5913933235L,  5982416059L,  6051013057L,
   6119723161L,  6188545324L,  6257478518L,  6326521733L,
   6395673979L,  6464934282L,  6534301685L,  6603775250L,
   6673354052L,  6743037185L,  6812823756L,  6882712890L,
   6952703725L,  7022795412L,  7092987118L,  7163278025L,
   7233667324L,  7304154222L,  7374737939L,  7445417707L,
   7516192768L,  7587062379L,  7658025806L,  7729082328L,
   7800231234L,  7871471825L,  7942803410L,  8014225311L,
   8085736859L,  8157337394L,  8229026267L,  8300802839L,
   8372666477L,  8444616560L,  8516652476L,  8588773618L,
   8660979393L,  8733269211L,  8805642493L,  8878098667L,
   8950637170L,  9023257446L,  9095958945L,  9168741125L,
   9241603454L,  9314545403L,  9387566451L,  9460666086L,
   9533843800L,  9607099093L,  9680431471L,  9753840445L,
   9827325535L,  9900886263L,  9974522161L, 10048232765L,
  10122017615L, 10195876260L, 10269808253L, 10343813150L,
  10417890516L, 10492039919L, 10566260934L, 10640553138L,
  10714916116L, 10789349456L, 10863852751L, 10938425600L,
  11013067604L, 11087778372L, 11162557513L, 11237404645L,
  11312319387L, 11387301364L, 11462350205L, 11537465541L,
  11612647010L, 11687894253L, 11763206912L, 11838584638L,
  11914027082L, 11989533899L, 12065104750L, 12140739296L,
  12216437206L, 12292198148L, 12368021795L, 12443907826L,
  12519855920L, 12595865759L, 12671937032L, 12748069427L,
  12824262637L, 12900516358L, 12976830290L, 13053204134L,
  13129637595L, 13206130381L, 13282682202L, 13359292772L,
  13435961806L, 13512689025L, 13589474149L, 13666316903L,
  13743217014L, 13820174211L, 13897188225L, 13974258793L,
  14051385649L, 14128568535L, 14205807192L, 14283101363L,
  14360450796L, 14437855239L, 14515314443L, 14592828162L,
  14670396151L, 14748018167L, 14825693972L, 14903423326L,
  14981205995L, 15059041743L, 15136930339L, 15214871554L,
  15292865160L, 15370910930L, 15449008641L, 15527158071L,
  15605359001L, 15683611210L, 15761914485L, 15840268608L,
  15918673369L, 15997128556L, 16075633960L, 16154189373L,
  16232794589L, 16311449405L, 16390153617L, 16468907026L,
  16547709431L, 16626560636L, 16705460444L, 16784408661L,
  16863405094L, 16942449552L, 17021541845L, 17100681785L
];
}
