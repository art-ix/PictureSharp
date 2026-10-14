using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RasterSharp.Formats.WebP.Lossless;

/// <summary>Per-channel costs of a histogram (literal, red, blue, alpha, distance).</summary>
[InlineArray(5)]
internal struct Vp8LChannelCosts
{
    private long _element;
}

internal sealed partial class Vp8LHistogram
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

    // port: src/dsp/lossless_enc.c GetCombinedEntropyUnrefined_C, GetEntropyUnrefinedHelper; src/enc/histogram_enc.c BitsEntropyRefine, FinalHuffmanCost
    // Cost of x + y without materializing the sum. The population statistics and the streaks are gathered in
    // separate passes (integer sums, so the order does not matter); fewer live values keep the loops in registers.
    public static long PopulationCost(ReadOnlySpan<uint> x, ReadOnlySpan<uint> y)
    {
        int length = x.Length;
        y = y[..length];
        ref uint px = ref MemoryMarshal.GetReference(x), py = ref MemoryMarshal.GetReference(y);
        int i = SumMaxZerosSimd(ref px, ref py, length, out uint sum, out uint max, out int zeros);
        for (; i < length; i++)
        {
            uint value = Unsafe.Add(ref px, i) + Unsafe.Add(ref py, i);
            sum += value;
            max = Math.Max(max, value);
            if (value == 0) zeros++;
        }
        int nonzero = length - zeros;
        long entropy = Streaks(ref px, ref py, length, out long longZero, out long longNonzero);
        if (max >= Vp8LEntropy.SLogCacheSize)
        {
            // Streaks clamped the SLog of large values.
            for (i = 0; i < length; i++)
            {
                uint value = Unsafe.Add(ref px, i) + Unsafe.Add(ref py, i);
                if (value >= Vp8LEntropy.SLogCacheSize) entropy -= Vp8LEntropy.SLog(value) - Vp8LEntropy.SLogClamped(value);
            }
        }
        int zeroRuns = (int)(longZero >> 32), nonzeroRuns = (int)(longNonzero >> 32);
        int longZeroLength = (int)longZero, longNonzeroLength = (int)longNonzero;
        int shortZero = zeros - longZeroLength, shortNonzero = nonzero - longNonzeroLength;
        long overhead = 57 * Vp8LEntropy.One - Vp8LEntropy.DivideRound(91 * Vp8LEntropy.One, 10);
        overhead += (zeroRuns * 1600L + 240L * longZeroLength) << 13;
        overhead += (nonzeroRuns * 2640L + 720L * longNonzeroLength) << 13;
        overhead += 1840L * shortZero << 13;
        overhead += 3360L * shortNonzero << 13;
        return Vp8LEntropy.Refine(sum, max, nonzero, entropy) + overhead;
    }

    // -sum(SLog(value) * streak) with SLog clamped, and the streaks longer than 3 of zeros and non-zeros:
    // their count in the high and their total length in the low 32 bits.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Streaks(ref uint px, ref uint py, int length, out long longZero, out long longNonzero)
    {
        long entropy = 0, zeroStreaks = 0, nonzeroStreaks = 0;
        int runStart = 0, i = 1;
        uint value = px + py;
        // Starts of runs, eight positions at a time.
        for (; i + 8 <= length && TryRunStarts8(ref px, ref py, i, out uint starts); i += 8)
        {
            for (; starts != 0; starts &= starts - 1)
            {
                int end = i + BitOperations.TrailingZeroCount(starts);
                AddStreak(ref entropy, ref zeroStreaks, ref nonzeroStreaks, value, end - runStart);
                runStart = end;
                value = Unsafe.Add(ref px, end) + Unsafe.Add(ref py, end);
            }
        }
        for (; i < length; i++)
        {
            uint next = Unsafe.Add(ref px, i) + Unsafe.Add(ref py, i);
            if (next == value) continue;
            AddStreak(ref entropy, ref zeroStreaks, ref nonzeroStreaks, value, i - runStart);
            runStart = i;
            value = next;
        }
        AddStreak(ref entropy, ref zeroStreaks, ref nonzeroStreaks, value, length - runStart);
        longZero = zeroStreaks;
        longNonzero = nonzeroStreaks;
        return entropy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddStreak(ref long entropy, ref long zeroStreaks, ref long nonzeroStreaks, uint value, int run)
    {
        entropy -= Vp8LEntropy.SLogClamped(value) * run;
        if (run > 3)
        {
            if (value != 0) nonzeroStreaks += (1L << 32) + run;
            else zeroStreaks += (1L << 32) + run;
        }
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
