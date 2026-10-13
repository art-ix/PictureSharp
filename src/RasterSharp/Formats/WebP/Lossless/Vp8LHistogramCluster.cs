namespace RasterSharp.Formats.WebP.Lossless;

internal static class Vp8LHistogramCluster
{
    private readonly record struct Pair(int A, int B, long Difference);

    // port: src/enc/histogram_enc.c VP8LGetHistoImageSymbols (libwebp 1.6.0)
    public static (Vp8LHistogram[] Groups, int[] Symbols) Build(Vp8LRefs refs, int width, int height, int bits, int cacheBits, int quality)
    {
        int tw = Vp8LDecoder.SubSampleSize(width, bits), th = Vp8LDecoder.SubSampleSize(height, bits), count = tw * th;
        var originals = new Vp8LHistogram?[count];
        int pos = 0;
        foreach (Vp8LToken token in refs.Tokens)
        {
            int tile = ((pos / width) >> bits) * tw + ((pos % width) >> bits);
            (originals[tile] ??= new Vp8LHistogram(cacheBits)).Add(token, width);
            pos += token.Length;
        }
        var groups = new List<Vp8LHistogram>();
        foreach (Vp8LHistogram? h in originals)
        {
            if (h is null) continue;
            var copy = new Vp8LHistogram(cacheBits); copy.Add(h); groups.Add(copy);
        }
        if (groups.Count > 128 && quality < 100) CombineBins(groups, count, quality);
        int threshold = 1 + (int)Vp8LEntropy.DivideRound(quality * quality * quality * 99, 1000000);
        if (CombineStochastic(groups, threshold)) CombineGreedy(groups);
        var map = new int[count];
        for (int i = 0; i < count; i++)
        {
            Vp8LHistogram? h = originals[i];
            if (h is null) { map[i] = i > 0 ? map[i - 1] : 0; continue; }
            long best = long.MaxValue;
            for (int j = 0; j < groups.Count; j++)
            {
                long delta = groups[j].CombinedCost(h) - groups[j].ClusterCost;
                if (delta < best) { best = delta; map[i] = j; }
            }
        }
        var rebuilt = new Vp8LHistogram[groups.Count];
        for (int i = 0; i < rebuilt.Length; i++) rebuilt[i] = new Vp8LHistogram(cacheBits);
        for (int i = 0; i < count; i++) if (originals[i] is { } h) rebuilt[map[i]].Add(h);
        return (rebuilt, map);
    }

    // port: src/enc/histogram_enc.c HistogramAnalyzeEntropyBin, HistogramCombineEntropyBin
    private static void CombineBins(List<Vp8LHistogram> groups, int count, int quality)
    {
        long[] min = [long.MaxValue, long.MaxValue, long.MaxValue], max = new long[3];
        foreach (Vp8LHistogram h in groups)
            for (int c = 0; c < 3; c++) { min[c] = Math.Min(min[c], h.ChannelCosts[c]); max[c] = Math.Max(max[c], h.ChannelCosts[c]); }
        var bins = new List<int>(groups.Count);
        foreach (Vp8LHistogram h in groups)
        {
            int bin = 0;
            for (int c = 0; c < 3; c++) bin = bin * 4 + (max[c] == min[c] ? 0 : (int)(3.999999 * (h.ChannelCosts[c] - min[c]) / (max[c] - min[c])));
            bins.Add(bin);
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
            long difference = groups[a].CombinedCost(groups[i]) - groups[a].ClusterCost - groups[i].ClusterCost;
            if (difference < -Vp8LEntropy.DivideRound(groups[i].ClusterCost * factor, 100))
            {
                bool trivial = true;
                for (int c = 1; c <= 3; c++) trivial &= groups[a].Symbols[c] >= 0 && groups[a].Symbols[c] == groups[i].Symbols[c];
                bool nontrivialA = false, nontrivialB = false;
                for (int c = 1; c <= 3; c++) { nontrivialA |= groups[a].Symbols[c] < 0; nontrivialB |= groups[i].Symbols[c] < 0; }
                if (trivial || (nontrivialA && nontrivialB) || failures[bin] >= 32)
                {
                    Merge(groups, a, i); bins[i] = bins[^1]; bins.RemoveAt(bins.Count - 1); continue;
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
            Merge(groups, first, second);
            for (int j = 0; j < queue.Count;)
            {
                Pair p = queue[j];
                bool a = p.A == first || p.A == second, b = p.B == first || p.B == second;
                if (a && b) { Remove(queue, j); continue; }
                if (a || b)
                {
                    p = Fix(p, second, first);
                    Pair current = Fix(p, groups.Count, second);
                    long delta = groups[current.A].CombinedCost(groups[current.B]) - groups[current.A].ClusterCost - groups[current.B].ClusterCost;
                    if (delta >= 0) { Remove(queue, j); continue; }
                    p = p with { Difference = delta };
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
            Merge(groups, a, b);
            for (int i = 0; i < queue.Count;)
            {
                Pair p = queue[i];
                if (p.A == a || p.B == a || p.A == b || p.B == b) Remove(queue, i);
                else { queue[i] = Fix(p, groups.Count, b); UpdateHead(queue, i); i++; }
            }
            for (int i = 0; i < groups.Count; i++) if (i != a) Push(groups, queue, a, i, 0);
        }
    }

    private static void Merge(List<Vp8LHistogram> groups, int a, int b)
    {
        groups[a].Add(groups[b]); groups[b] = groups[^1]; groups.RemoveAt(groups.Count - 1);
    }

    private static long Push(List<Vp8LHistogram> groups, List<Pair> queue, int a, int b, long threshold)
    {
        if (a > b) (a, b) = (b, a);
        long delta = groups[a].CombinedCost(groups[b]) - groups[a].ClusterCost - groups[b].ClusterCost;
        if (delta >= threshold) return 0;
        queue.Add(new Pair(a, b, delta)); UpdateHead(queue, queue.Count - 1); return delta;
    }

    private static Pair Fix(Pair p, int from, int to)
    {
        int a = p.A == from ? to : p.A, b = p.B == from ? to : p.B;
        return new Pair(Math.Min(a, b), Math.Max(a, b), p.Difference);
    }

    private static void Remove(List<Pair> queue, int index) { queue[index] = queue[^1]; queue.RemoveAt(queue.Count - 1); }
    private static void UpdateHead(List<Pair> queue, int index)
    {
        if (queue[index].Difference < queue[0].Difference) (queue[index], queue[0]) = (queue[0], queue[index]);
    }
}
