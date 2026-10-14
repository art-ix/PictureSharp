using System.Runtime.CompilerServices;

namespace RasterSharp.Formats.WebP.Lossless;

/// <summary>Per-channel costs of a histogram (literal, red, blue, alpha, distance).</summary>
[InlineArray(5)]
internal struct Vp8LChannelCosts
{
    private long _element;
}

internal sealed class Vp8LHistogram
{
    private static readonly uint[] Zeros = new uint[280 + (1 << 11)];
    public uint[][] Counts { get; }
    private readonly long[] _channelCosts = new long[5];
    private readonly int[] _symbols = new int[5];
    private long _clusterCost;
    private bool _analyzed;
    public long[] ChannelCosts { get { Analyze(); return _channelCosts; } }
    public int[] Symbols { get { Analyze(); return _symbols; } }
    public long ClusterCost { get { Analyze(); return _clusterCost; } }

    private void Analyze()
    {
        if (_analyzed) return;
        _analyzed = true;
        _clusterCost = 0;
        for (int c = 0; c < 5; c++)
        {
            _channelCosts[c] = PopulationCost(Counts[c]);
            _clusterCost += _channelCosts[c];
            int symbol = -1;
            for (int i = 0; i < Counts[c].Length; i++)
                if (Counts[c][i] != 0) { if (symbol >= 0) { symbol = -2; break; } symbol = i; }
            _symbols[c] = symbol;
        }
    }
    public Vp8LHistogram(int cacheBits) => Counts = [new uint[280 + (cacheBits == 0 ? 0 : 1 << cacheBits)], new uint[256], new uint[256], new uint[256], new uint[40]];

    // port: src/enc/histogram_enc.c HistogramAddSinglePixOrCopy
    // Distances are mapped to plane codes unless the references still hold raw distances for estimation.
    public void Add(Vp8LToken token, int width, bool planeCodes = true)
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
            int distance = planeCodes ? Vp8LBackwardRefs.PlaneCode(width, (int)token.Value) : (int)token.Value;
            Counts[4][Vp8LBackwardRefs.Prefix(distance).Code]++;
        }
    }

    public void Add(Vp8LHistogram other)
    {
        _analyzed = false;
        for (int c = 0; c < 5; c++) AddCounts(Counts[c], other.Counts[c]);
    }

    // port: src/enc/histogram_enc.c HistogramAdd, UpdateHistogramCost
    // Merges with costs already computed by TryCombinedCost; they equal a fresh analysis of the sum.
    public void Add(Vp8LHistogram other, in Vp8LChannelCosts costs)
    {
        Analyze();
        other.Analyze();
        _clusterCost = 0;
        for (int c = 0; c < 5; c++)
        {
            AddCounts(Counts[c], other.Counts[c]);
            int a = _symbols[c], b = other._symbols[c];
            _symbols[c] = a == -1 ? b : b == -1 || a == b ? a : -2;
            _channelCosts[c] = costs[c];
            _clusterCost += costs[c];
        }
    }

    private static void AddCounts(Span<uint> destination, ReadOnlySpan<uint> source)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] += source[i];
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
    public static long PopulationCost(ReadOnlySpan<uint> counts) => PopulationCost(counts, Zeros.AsSpan(0, counts.Length));

    // port: src/dsp/lossless_enc.c GetCombinedEntropyUnrefined_C; src/enc/histogram_enc.c BitsEntropyRefine, FinalHuffmanCost
    // Cost of x + y in one pass, without materializing the sum.
    public static long PopulationCost(ReadOnlySpan<uint> x, ReadOnlySpan<uint> y)
    {
        y = y[..x.Length];
        uint sum = 0, max = 0;
        int nonzero = 0, zeroRuns = 0, nonzeroRuns = 0, shortZero = 0, longZero = 0, shortNonzero = 0, longNonzero = 0;
        long entropy = 0;
        int i = 0;
        while (i < x.Length)
        {
            uint value = x[i] + y[i];
            int end = i + 1;
            while (end < x.Length && x[end] + y[end] == value) end++;
            int run = end - i;
            if (value == 0) { if (run > 3) { zeroRuns++; longZero += run; } else shortZero += run; }
            else
            {
                if (run > 3) { nonzeroRuns++; longNonzero += run; } else shortNonzero += run;
                sum += value * (uint)run; nonzero += run; max = Math.Max(max, value);
                entropy -= Vp8LEntropy.SLog(value) * run;
            }
            i = end;
        }
        long overhead = 57 * Vp8LEntropy.One - Vp8LEntropy.DivideRound(91 * Vp8LEntropy.One, 10);
        overhead += (zeroRuns * 1600L + 240L * longZero) << 13;
        overhead += (nonzeroRuns * 2640L + 720L * longNonzero) << 13;
        overhead += 1840L * shortZero << 13;
        overhead += 3360L * shortNonzero << 13;
        return Vp8LEntropy.Refine(sum, max, nonzero, entropy) + overhead;
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

    public long CombinedCost(Vp8LHistogram other)
    {
        Vp8LChannelCosts costs = default;
        TryCombinedCost(other, long.MaxValue, ref costs, out long cost);
        return cost;
    }

    // port: src/enc/histogram_enc.c GetCombinedHistogramEntropy, GetCombinedEntropy
    // Channel costs are non-negative, so stopping once the partial sum reaches the threshold
    // gives the same accept/reject decision as the full cost.
    public bool TryCombinedCost(Vp8LHistogram other, long threshold, ref Vp8LChannelCosts costs, out long cost)
    {
        Analyze();
        other.Analyze();
        cost = 0;
        for (int c = 0; c < 5; c++)
        {
            int a = _symbols[c], b = other._symbols[c];
            long channel = a == -1 ? other._channelCosts[c]
                : b == -1 || (a >= 0 && a == b) ? _channelCosts[c]
                : PopulationCost(Counts[c], other.Counts[c]);
            costs[c] = channel;
            cost += channel;
            if (cost >= threshold) return false;
        }
        return true;
    }

    public Vp8LHuffmanEncoder[] Codes() => Counts.Select(c => new Vp8LHuffmanEncoder(c)).ToArray();
}
