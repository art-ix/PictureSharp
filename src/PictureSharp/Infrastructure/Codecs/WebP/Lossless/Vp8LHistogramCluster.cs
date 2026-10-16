namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

internal static class Vp8LHistogramCluster
{
    private record struct Pair(int A, int B, long Difference, Vp8LChannelCosts Costs);

    // port: src/enc/histogram_enc.c VP8LGetHistoImageSymbols (libwebp 1.6.0)
    private const int NumPartitions = 4, BinSize = NumPartitions * NumPartitions * NumPartitions, MaxHistoGreedy = 100;

    // The returned groups live in the returned set, which the caller disposes after use.
    public static (Vp8LHistogramSet Set, Vp8LHistogram[] Groups, int[] Symbols) Build(Vp8LRefs refs, int width, int height, int bits, int cacheBits,
        int quality, bool lowEffort, int maxDegreeOfParallelism = 1, CancellationToken cancellationToken = default)
    {
        int tw = Vp8LDecoder.SubSampleSize(width, bits), th = Vp8LDecoder.SubSampleSize(height, bits), count = tw * th;
        var originals = new Vp8LHistogram?[count];
        using var originalSet = new Vp8LHistogramSet(count, cacheBits);
        int pos = 0;
        foreach (Vp8LToken token in refs.Tokens)
        {
            int tile = ((pos / width) >> bits) * tw + ((pos % width) >> bits);
            (originals[tile] ??= originalSet.Create()).Add(token, width);
            pos += token.Length;
        }
        using var groupSet = new Vp8LHistogramSet(count, cacheBits);
        var groups = new List<Vp8LHistogram>();
        foreach (Vp8LHistogram? h in originals)
        {
            if (h is null) continue;
            Vp8LHistogram copy = groupSet.Create(); copy.Add(h); groups.Add(copy);
        }
        // The bin heuristic is skipped for few histograms (sparse bins) and at q == 100 (to keep the gains).
        bool entropyCombine = groups.Count > (lowEffort ? NumPartitions : BinSize) * 2 && quality < 100;
        if (entropyCombine) CombineBins(groups, count, quality, lowEffort);
        if (!lowEffort || !entropyCombine)
        {
            // Cubic ramp between 1 and MaxHistoGreedy.
            int threshold = 1 + (int)Vp8LEntropy.DivideRound(quality * quality * quality * (MaxHistoGreedy - 1), 100 * 100 * 100);
            if (CombineStochastic(groups, threshold)) CombineGreedy(groups);
        }
        // port: src/enc/histogram_enc.c HistogramRemap
        // Tiles are independent; the groups are analyzed up front so that threads only read them.
        var map = new int[count];
        foreach (Vp8LHistogram group in groups) _ = group.ClusterCost;
        if (maxDegreeOfParallelism != 1 && count * groups.Count >= 4096)
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken }, i => Remap(i));
        else
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Remap(i);
            }
        // Empty tiles take the previous symbol.
        for (int i = 0; i < count; i++) if (originals[i] is null) map[i] = i > 0 ? map[i - 1] : 0;
        var rebuiltSet = new Vp8LHistogramSet(groups.Count, cacheBits);
        var rebuilt = new Vp8LHistogram[groups.Count];
        for (int i = 0; i < rebuilt.Length; i++) rebuilt[i] = rebuiltSet.Create();
        for (int i = 0; i < count; i++) if (originals[i] is { } h) rebuilt[map[i]].Add(h);
        return (rebuiltSet, rebuilt, map);

        void Remap(int i)
        {
            Vp8LHistogram? h = originals[i];
            if (h is null) return;
            long best = long.MaxValue;
            Vp8LChannelCosts costs = default;
            for (int j = 0; j < groups.Count; j++)
            {
                long cluster = groups[j].ClusterCost;
                if (groups[j].TryCombinedCost(h, SaturatingAdd(best, cluster), ref costs, out long combined))
                {
                    best = combined - cluster; map[i] = j;
                }
            }
        }
    }

    // port: src/enc/histogram_enc.c HistogramAnalyzeEntropyBin, GetHistoBinIndex, GetBinIdForEntropy, HistogramCombineEntropyBin
    private static void CombineBins(List<Vp8LHistogram> groups, int count, int quality, bool lowEffort)
    {
        long[] min = [long.MaxValue, long.MaxValue, long.MaxValue], max = new long[3];
        foreach (Vp8LHistogram h in groups)
            for (int c = 0; c < 3; c++) { min[c] = Math.Min(min[c], h.ChannelCost(c)); max[c] = Math.Max(max[c], h.ChannelCost(c)); }
        var bins = new List<int>(groups.Count);
        foreach (Vp8LHistogram h in groups)
        {
            // Literal, red and blue are the dominant costs; low effort bins on the literal cost only.
            int bin = 0;
            for (int c = 0; c < (lowEffort ? 1 : 3); c++)
                bin = bin * NumPartitions + (max[c] == min[c] ? 0 : (int)((NumPartitions - 1e-6) * (ulong)(h.ChannelCost(c) - min[c]) / (ulong)(max[c] - min[c])));
            bins.Add(bin);
        }
        if (lowEffort)
        {
            int[] firsts = new int[NumPartitions]; Array.Fill(firsts, -1);
            for (int i = 0; i < groups.Count;)
            {
                int head = firsts[bins[i]];
                if (head < 0) { firsts[bins[i]] = i++; continue; }
                groups[head].Add(groups[i]); groups[i] = groups[^1]; groups.RemoveAt(groups.Count - 1);
                bins[i] = bins[^1]; bins.RemoveAt(bins.Count - 1);
            }
            return;
        }
        int factor = 16;
        if (quality < 90)
        {
            if (count > 256) factor /= 2;
            if (count > 512) factor /= 2;
            if (count > 1024) factor /= 2;
            if (quality <= 50) factor /= 2;
        }
        int[] first = new int[64], failures = new int[64]; Array.Fill(first, -1);
        for (int i = 0; i < groups.Count;)
        {
            int bin = bins[i], a = first[bin];
            if (a < 0) { first[bin] = i++; continue; }
            Vp8LChannelCosts costs = default;
            long threshold = groups[a].ClusterCost + groups[i].ClusterCost - Vp8LEntropy.DivideRound(groups[i].ClusterCost * factor, 100);
            if (groups[a].TryCombinedCost(groups[i], threshold, ref costs, out _))
            {
                bool trivial = true;
                for (int c = 1; c <= 3; c++) trivial &= groups[a].Symbol(c) >= 0 && groups[a].Symbol(c) == groups[i].Symbol(c);
                bool nontrivialA = false, nontrivialB = false;
                for (int c = 1; c <= 3; c++) { nontrivialA |= groups[a].Symbol(c) < 0; nontrivialB |= groups[i].Symbol(c) < 0; }
                if (trivial || (nontrivialA && nontrivialB) || failures[bin] >= 32)
                {
                    Merge(groups, a, i, costs); bins[i] = bins[^1]; bins.RemoveAt(bins.Count - 1); continue;
                }
                failures[bin]++;
            }
            i++;
        }
    }

    // port: src/enc/histogram_enc.c HistogramCombineStochastic, MyRand
    private static bool CombineStochastic(List<Vp8LHistogram> groups, int threshold)
    {
        uint seed = 1;
        int outer = groups.Count, failures = 0;
        var queue = new List<Pair>(9);
        for (int iteration = 0; iteration < outer && groups.Count >= threshold && ++failures < outer / 2; iteration++)
        {
            long best = queue.Count == 0 ? 0 : queue[0].Difference;
            uint range = (uint)((groups.Count - 1) * groups.Count);
            for (int j = 0; groups.Count >= 2 && j < groups.Count / 2; j++)
            {
                seed = (uint)((ulong)seed * 48271 % 2147483647);
                uint r = seed % range;
                int a = (int)(r / (groups.Count - 1)), b = (int)(r % (groups.Count - 1));
                if (b >= a) b++;
                long difference = Push(groups, queue, a, b, best);
                if (difference < 0) { best = difference; if (queue.Count == 9) break; }
            }
            if (queue.Count == 0) continue;
            int first = queue[0].A, second = queue[0].B;
            Merge(groups, first, second, queue[0].Costs);
            for (int j = 0; j < queue.Count;)
            {
                Pair p = queue[j];
                bool a = p.A == first || p.A == second, b = p.B == first || p.B == second;
                if (a && b) { Remove(queue, j); continue; }
                if (a || b)
                {
                    p = Fix(p, second, first);
                    Pair current = Fix(p, groups.Count, second);
                    if (!TryPair(groups, current.A, current.B, 0, out Pair updated)) { Remove(queue, j); continue; }
                    p = p with { Difference = updated.Difference, Costs = updated.Costs };
                }
                queue[j] = Fix(p, groups.Count, second); UpdateHead(queue, j); j++;
            }
            failures = 0;
        }
        return groups.Count <= threshold;
    }

    // port: src/enc/histogram_enc.c HistogramCombineGreedy
    private static void CombineGreedy(List<Vp8LHistogram> groups)
    {
        var queue = new List<Pair>();
        for (int i = 0; i < groups.Count; i++) for (int j = i + 1; j < groups.Count; j++) Push(groups, queue, i, j, 0);
        while (queue.Count > 0)
        {
            int a = queue[0].A, b = queue[0].B;
            Merge(groups, a, b, queue[0].Costs);
            for (int i = 0; i < queue.Count;)
            {
                Pair p = queue[i];
                if (p.A == a || p.B == a || p.A == b || p.B == b) Remove(queue, i);
                else { queue[i] = Fix(p, groups.Count, b); UpdateHead(queue, i); i++; }
            }
            for (int i = 0; i < groups.Count; i++) if (i != a) Push(groups, queue, a, i, 0);
        }
    }

    private static void Merge(List<Vp8LHistogram> groups, int a, int b, in Vp8LChannelCosts costs)
    {
        groups[a].Add(groups[b], costs); groups[b] = groups[^1]; groups.RemoveAt(groups.Count - 1);
    }

    // port: src/enc/histogram_enc.c HistoQueuePush
    private static long Push(List<Vp8LHistogram> groups, List<Pair> queue, int a, int b, long threshold)
    {
        if (a > b) (a, b) = (b, a);
        if (!TryPair(groups, a, b, threshold, out Pair pair)) return 0;
        queue.Add(pair); UpdateHead(queue, queue.Count - 1); return pair.Difference;
    }

    // port: src/enc/histogram_enc.c HistoQueueUpdatePair
    private static bool TryPair(List<Vp8LHistogram> groups, int a, int b, long threshold, out Pair pair)
    {
        long sum = groups[a].ClusterCost + groups[b].ClusterCost;
        Vp8LChannelCosts costs = default;
        bool better = groups[a].TryCombinedCost(groups[b], SaturatingAdd(threshold, sum), ref costs, out long combined);
        pair = new Pair(a, b, combined - sum, costs);
        return better;
    }

    private static long SaturatingAdd(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;

    private static Pair Fix(Pair p, int from, int to)
    {
        int a = p.A == from ? to : p.A, b = p.B == from ? to : p.B;
        return p with { A = Math.Min(a, b), B = Math.Max(a, b) };
    }

    private static void Remove(List<Pair> queue, int index) { queue[index] = queue[^1]; queue.RemoveAt(queue.Count - 1); }
    private static void UpdateHead(List<Pair> queue, int index)
    {
        if (queue[index].Difference < queue[0].Difference) (queue[index], queue[0]) = (queue[0], queue[index]);
    }
}
