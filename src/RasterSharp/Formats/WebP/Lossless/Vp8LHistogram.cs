namespace RasterSharp.Formats.WebP.Lossless;

internal sealed class Vp8LHistogram
{
    public uint[][] Counts { get; }
    private readonly long[] _channelCosts = new long[5];
    private bool _analyzed;
    private readonly int[] _symbols = new int[5];
    public long[] ChannelCosts { get { Analyze(); return _channelCosts; } }
    public int[] Symbols { get { Analyze(); return _symbols; } }
    public long ClusterCost => ChannelCosts.Sum();

    private void Analyze()
    {
        if (_analyzed) return;
        _analyzed = true;
        for (int c = 0; c < 5; c++)
        {
            _channelCosts[c] = PopulationCost(Counts[c]);
            int symbol = -1;
            for (int i = 0; i < Counts[c].Length; i++)
                if (Counts[c][i] != 0) { if (symbol >= 0) { symbol = -2; break; } symbol = i; }
            _symbols[c] = symbol;
        }
    }
    public Vp8LHistogram(int cacheBits) => Counts = [new uint[280 + (cacheBits == 0 ? 0 : 1 << cacheBits)], new uint[256], new uint[256], new uint[256], new uint[40]];

    // port: src/enc/histogram_enc.c VP8LHistogramAddSinglePixOrCopy
    public void Add(Vp8LToken token, int width)
    {
        _analyzed = false;
        if (token.Kind == 0)
        {
            Counts[0][(token.Value >> 8) & 255]++; Counts[1][(token.Value >> 16) & 255]++;
            Counts[2][token.Value & 255]++; Counts[3][token.Value >> 24]++;
        }
        else if (token.Kind == 2) Counts[0][280 + token.Value]++;
        else
        {
            Counts[0][256 + Vp8LBackwardRefs.Prefix(token.Length).Code]++;
            Counts[4][Vp8LBackwardRefs.Prefix(Vp8LBackwardRefs.PlaneCode(width, (int)token.Value)).Code]++;
        }
    }

    public void Add(Vp8LHistogram other)
    {
        _analyzed = false;
        for (int c = 0; c < 5; c++) for (int i = 0; i < Counts[c].Length; i++) Counts[c][i] += other.Counts[c][i];
    }

    // port: src/enc/backward_references_cost_enc.c ConvertPopulationCountTableToBitEstimates
    public long[][] Costs()
    {
        var result = new long[5][];
        for (int c = 0; c < 5; c++)
        {
            result[c] = new long[Counts[c].Length];
            uint sum = 0; int nonzero = 0;
            foreach (uint count in Counts[c]) { sum += count; if (count != 0) nonzero++; }
            if (nonzero <= 1) continue;
            long log = Vp8LEntropy.Log(sum);
            for (int i = 0; i < Counts[c].Length; i++) result[c][i] = log - Vp8LEntropy.Log(Counts[c][i]);
        }
        return result;
    }

    // port: src/enc/histogram_enc.c PopulationCost, FinalHuffmanCost
    public static long PopulationCost(ReadOnlySpan<uint> counts)
    {
        long bits = Vp8LEntropy.Bits(counts), overhead = 57 * Vp8LEntropy.One - Vp8LEntropy.DivideRound(91 * Vp8LEntropy.One, 10);
        int i = 0, zeroRuns = 0, nonzeroRuns = 0, shortZero = 0, longZero = 0, shortNonzero = 0, longNonzero = 0;
        while (i < counts.Length)
        {
            int end = i + 1;
            while (end < counts.Length && counts[end] == counts[i]) end++;
            int run = end - i;
            if (counts[i] == 0) { if (run > 3) { zeroRuns++; longZero += run; } else shortZero += run; }
            else { if (run > 3) { nonzeroRuns++; longNonzero += run; } else shortNonzero += run; }
            i = end;
        }
        overhead += (zeroRuns * 1600L + 240L * longZero) << 13;
        overhead += (nonzeroRuns * 2640L + 720L * longNonzero) << 13;
        overhead += 1840L * shortZero << 13;
        overhead += 3360L * shortNonzero << 13;
        return bits + overhead;
    }

    // port: src/enc/histogram_enc.c VP8LHistogramEstimateBits
    public long Estimate()
    {
        long bits = 0;
        foreach (uint[] counts in Counts) bits += PopulationCost(counts);
        return bits + ExtraCost();
    }

    public long ExtraCost()
    {
        long bits = 0;
        for (int i = 4; i < 24; i++) bits += Counts[0][256 + i] * ((i - 2) >> 1);
        for (int i = 4; i < 40; i++) bits += Counts[4][i] * ((i - 2) >> 1);
        return bits * Vp8LEntropy.One;
    }

    // port: src/enc/histogram_enc.c GetCombinedHistogramEntropy
    public long CombinedCost(Vp8LHistogram other)
    {
        Span<uint> sum = stackalloc uint[Counts[0].Length];
        long bits = 0;
        for (int c = 0; c < 5; c++)
        {
            int a = Symbols[c], b = other.Symbols[c];
            if (a == -1) bits += other.ChannelCosts[c];
            else if (b == -1 || (a >= 0 && a == b)) bits += ChannelCosts[c];
            else
            {
                for (int i = 0; i < Counts[c].Length; i++) sum[i] = Counts[c][i] + other.Counts[c][i];
                bits += PopulationCost(sum[..Counts[c].Length]);
            }
        }
        return bits;
    }

    public Vp8LHuffmanEncoder[] Codes() => Counts.Select(c => new Vp8LHuffmanEncoder(c)).ToArray();
}
