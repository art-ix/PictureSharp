using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.WebP.Lossy;

internal sealed partial class Vp8EncoderProba
{
    public byte[] Coefficients { get; } = Vp8Tables.CoeffsProba0.ToArray();
    public uint[] Statistics { get; } = new uint[1056];
    public ushort[] LevelCosts { get; } = new ushort[4 * 8 * 3 * 68];
    public byte[] SegmentProbas { get; } = [255, 255, 255];
    public bool Dirty { get; set; } = true;
    public int SkipCount { get; set; }
    public int SkipProba { get; private set; } = 255;
    public bool UseSkip { get; set; }
    public bool UpdateSegments { get; private set; }
    public int SegmentSize { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BitCost(bool bit, int probability) => Vp8EncoderTables.EntropyCost[bit ? 255 - probability : probability];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Offset(int type, int band, int context) => 11 * (context + 3 * (band + 8 * type));

    // port: src/enc/cost_enc.h VP8RecordStats
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Record(bool bit, int index)
    {
        uint p = Statistics[index];
        if (p >= 0xfffe0000) p = ((p + 1) >> 1) & 0x7fff7fff;
        Statistics[index] = p + 0x10000u + (bit ? 1u : 0);
        return bit;
    }

    // port: src/enc/cost_enc.c VP8CalculateLevelCosts, VariableLevelCost
    public void CalculateLevelCosts()
    {
        if (!Dirty) return;
        for (int type = 0; type < 4; type++)
        for (int band = 0; band < 8; band++)
        for (int context = 0; context < 3; context++)
        {
            int p = Offset(type, band, context), t = p / 11 * 68;
            int cost0 = context > 0 ? BitCost(true, Coefficients[p]) : 0;
            int costBase = BitCost(true, Coefficients[p + 1]) + cost0;
            LevelCosts[t] = (ushort)(BitCost(false, Coefficients[p + 1]) + cost0);
            for (int level = 1; level <= 67; level++)
            {
                int pattern = Vp8EncoderTables.LevelCodes[2 * (level - 1)], bits = Vp8EncoderTables.LevelCodes[2 * (level - 1) + 1], cost = costBase;
                for (int i = 2; pattern != 0; i++, pattern >>= 1, bits >>= 1)
                    if ((pattern & 1) != 0) cost += BitCost((bits & 1) != 0, Coefficients[p + i]);
                LevelCosts[t + level] = (ushort)cost;
            }
        }
        Dirty = false;
    }

    // port: src/dsp/cost.c GetResidualCost_C
    public int ResidualCost(int type, int first, int context, ReadOnlySpan<short> levels)
    {
        int last = Last(levels), n = first, p = Offset(type, first, context);
        if (last < 0) return BitCost(false, Coefficients[p]);
        int cost = context == 0 ? BitCost(true, Coefficients[p]) : 0;
        for (; n <= last; n++)
        {
            int v = Math.Abs((int)levels[n]);
            cost += LevelCost(type, n, context, v);
            context = Math.Min(v, 2);
        }
        if (last < 15) cost += BitCost(false, Coefficients[Offset(type, Vp8Tables.Bands[n], context)]);
        return cost;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int LevelCost(int type, int n, int context, int value) => Vp8EncoderTables.LevelFixedCosts[value] + LevelCosts[(3 * (8 * type + Vp8Tables.Bands[n]) + context) * 68 + Math.Min(value, 67)];
    private static int LastScalar(ReadOnlySpan<short> levels)
    {
        for (int n = 15; n >= 0; n--) if (levels[n] != 0) return n;
        return -1;
    }

    // port: src/enc/frame_enc.c FinalizeTokenProbas, CalcTokenProba, BranchCost
    public int FinalizeTokenProbas()
    {
        bool changed = false; int size = 0;
        for (int i = 0; i < 1056; i++)
        {
            int ones = (int)(Statistics[i] & 0xffff), total = (int)(Statistics[i] >> 16);
            int update = Vp8Tables.CoeffsUpdateProba[i], old = Vp8Tables.CoeffsProba0[i];
            int value = ones == 0 ? 255 : 255 - ones * 255 / total;
            int oldCost = ones * BitCost(true, old) + (total - ones) * BitCost(false, old) + BitCost(false, update);
            int newCost = ones * BitCost(true, value) + (total - ones) * BitCost(false, value) + BitCost(true, update) + 8 * 256;
            bool useNew = oldCost > newCost;
            size += BitCost(useNew, update);
            Coefficients[i] = (byte)(useNew ? value : old);
            if (useNew) { changed |= value != old; size += 8 * 256; }
        }
        Dirty = changed;
        return size;
    }

    // port: src/enc/frame_enc.c FinalizeSkipProba
    public int FinalizeSkipProba(int macroblocks)
    {
        SkipProba = macroblocks == 0 ? 255 : (int)((long)(macroblocks - SkipCount) * 255 / macroblocks);
        UseSkip = SkipProba < 250;
        return 256 + (UseSkip ? SkipCount * BitCost(true, SkipProba) + (macroblocks - SkipCount) * BitCost(false, SkipProba) + 8 * 256 : 0);
    }

    // port: src/enc/frame_enc.c SetSegmentProbas, GetProba
    public void SetSegmentProbas(Vp8EncoderContext context)
    {
        if (context.SegmentCount <= 1) { UpdateSegments = false; SegmentSize = 0; return; }
        Span<int> counts = stackalloc int[4]; counts.Clear();
        for (int i = 0; i < context.MbCount; i++) counts[context.Blocks[i].Segment]++;
        if (context.SegmentCount > 1)
        {
            SegmentProbas[0] = Probability(counts[0] + counts[1], counts[2] + counts[3]);
            SegmentProbas[1] = Probability(counts[0], counts[1]);
            SegmentProbas[2] = Probability(counts[2], counts[3]);
            UpdateSegments = SegmentProbas[0] != 255 || SegmentProbas[1] != 255 || SegmentProbas[2] != 255;
            if (!UpdateSegments) for (int i = 0; i < context.MbCount; i++) context.Blocks[i].Segment = 0;
        }
        SegmentSize = counts[0] * (BitCost(false, SegmentProbas[0]) + BitCost(false, SegmentProbas[1]))
                    + counts[1] * (BitCost(false, SegmentProbas[0]) + BitCost(true, SegmentProbas[1]))
                    + counts[2] * (BitCost(true, SegmentProbas[0]) + BitCost(false, SegmentProbas[2]))
                    + counts[3] * (BitCost(true, SegmentProbas[0]) + BitCost(true, SegmentProbas[2]));
    }
    private static byte Probability(int a, int b) => a + b == 0 ? (byte)255 : (byte)((255 * a + (a + b) / 2) / (a + b));
}
