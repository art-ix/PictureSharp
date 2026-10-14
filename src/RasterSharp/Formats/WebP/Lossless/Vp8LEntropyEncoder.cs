using System.Buffers;

namespace RasterSharp.Formats.WebP.Lossless;

[Flags]
internal enum Vp8LLz77 { Standard = 1, Rle = 2, Box = 4 }

internal readonly record struct Vp8LCrunchSubConfig(Vp8LLz77 Lz77, bool DoNoCache);

internal static class Vp8LEntropyEncoder
{
    // port: src/enc/vp8l_enc.c EncodeImageInternal
    public static void EncodeImageInternal(Vp8LBitWriter writer, uint[] pixels, int width, int height, int quality, bool lowEffort,
        ReadOnlySpan<Vp8LCrunchSubConfig> subConfigs, ref int cacheBits, int histoBits, int maxDegreeOfParallelism)
    {
        using var matches = new Vp8LBackwardRefs(pixels, width, height, quality, lowEffort, maxDegreeOfParallelism: maxDegreeOfParallelism);
        Vp8LBackwardRefs? box = null;
        // A non-zero value was set by the palette analysis.
        int cacheBitsInit = cacheBits == 0 ? Vp8LEncoder.MaxColorCacheBits : cacheBits;
        Vp8LBitWriter? best = null;
        try
        {
            foreach (Vp8LCrunchSubConfig sub in subConfigs)
            {
                var refs = GetBackwardReferences(pixels, width, height, quality, lowEffort, sub.Lz77, cacheBitsInit, sub.DoNoCache, matches, ref box, out int cacheBitsBest, maxDegreeOfParallelism);
                try
                {
                    for (int iCache = 0; iCache < (sub.DoNoCache ? 2 : 1); iCache++)
                    {
                        int cacheBitsTmp = iCache == 0 ? cacheBitsBest : 0;
                        // The no-cache case was already studied when the best cache size is 0.
                        if (iCache == 1 && cacheBitsBest == 0) break;
                        Vp8LBitWriter candidate = StoreImage(refs[iCache]!, width, height, quality, lowEffort, cacheBitsTmp, histoBits, maxDegreeOfParallelism);
                        if (best is null || candidate.ByteCount < best.ByteCount)
                        {
                            best?.Dispose();
                            best = candidate;
                            cacheBits = cacheBitsTmp;
                        }
                        else candidate.Dispose();
                    }
                }
                finally { refs[0]?.Dispose(); refs[1]?.Dispose(); }
            }
            writer.Append(best!);
        }
        finally { best?.Dispose(); box?.Dispose(); }
    }

    private static Vp8LBitWriter StoreImage(Vp8LRefs refs, int width, int height, int quality, bool lowEffort, int cacheBits, int histoBits,
        int maxDegreeOfParallelism)
    {
        var writer = new Vp8LBitWriter();
        var (set, groups, symbols) = Vp8LHistogramCluster.Build(refs, width, height, histoBits, cacheBits, quality, lowEffort, maxDegreeOfParallelism);
        Vp8LHuffmanEncoder[][] codes;
        using (set) codes = groups.Select(static h => h.Codes()).ToArray();
        if (cacheBits > 0) { writer.Put(1, 1); writer.Put(cacheBits, 4); }
        else writer.Put(0, 1);
        int imageSize = 0;
        uint[] histogramImage = new uint[symbols.Length];
        for (int i = 0; i < symbols.Length; i++)
        {
            imageSize = Math.Max(imageSize, symbols[i] + 1);
            histogramImage[i] = (uint)symbols[i] << 8;
        }
        int histogramBits = histoBits;
        bool writeHistogramImage = imageSize > 1;
        writer.Put(writeHistogramImage ? 1 : 0, 1);
        if (writeHistogramImage)
        {
            Vp8LForwardTransforms.OptimizeSampling(histogramImage, width, height, histoBits, Vp8LEncoder.MaxHuffmanBits, out histogramBits);
            writer.Put(histogramBits - 2, 3);
            EncodeImageNoHuffman(writer, histogramImage, Vp8LDecoder.SubSampleSize(width, histogramBits), Vp8LDecoder.SubSampleSize(height, histogramBits), quality, lowEffort);
        }
        for (int i = 0; i < imageSize; i++) foreach (Vp8LHuffmanEncoder code in codes[i]) code.Store(writer);
        StoreImageToBitMask(writer, width, histogramBits, refs, histogramImage, codes);
        return writer;
    }

    // port: src/enc/vp8l_enc.c EncodeImageNoHuffman
    public static void EncodeImageNoHuffman(Vp8LBitWriter writer, uint[] pixels, int width, int height, int quality, bool lowEffort)
    {
        using var matches = new Vp8LBackwardRefs(pixels, width, height, quality, lowEffort);
        Vp8LBackwardRefs? box = null;
        var refs = GetBackwardReferences(pixels, width, height, quality, false, Vp8LLz77.Standard | Vp8LLz77.Rle, 0, false, matches, ref box, out _, 1);
        try
        {
            var histogram = new Vp8LHistogram(0);
            foreach (Vp8LToken token in refs[0]!.Tokens) histogram.Add(token, width);
            Vp8LHuffmanEncoder[] codes = histogram.Codes();
            // No color cache, no Huffman image.
            writer.Put(0, 1);
            foreach (Vp8LHuffmanEncoder code in codes) code.Store(writer);
            foreach (Vp8LToken token in refs[0]!.Tokens) WriteToken(writer, token, width, codes);
        }
        finally { refs[0]?.Dispose(); refs[1]?.Dispose(); box?.Dispose(); }
    }

    // port: src/enc/backward_references_enc.c VP8LGetBackwardReferences, GetBackwardReferences, GetBackwardReferencesLowEffort
    // Returns the references with a color cache (index 0) and, when requested, without one (index 1).
    // Their distances are raw; plane codes are applied when the tokens are stored, as BackwardReferences2DLocality does.
    private static Vp8LRefs?[] GetBackwardReferences(uint[] pixels, int width, int height, int quality, bool lowEffort, Vp8LLz77 types,
        int cacheBitsMax, bool doNoCache, Vp8LBackwardRefs matches, ref Vp8LBackwardRefs? box, out int cacheBitsBest, int maxDegreeOfParallelism)
    {
        var refs = new Vp8LRefs?[2];
        if (lowEffort)
        {
            cacheBitsBest = 0;
            refs[0] = matches.Greedy(false);
            return refs;
        }
        cacheBitsBest = 0;
        Span<long> bestCosts = [long.MaxValue, long.MaxValue];
        Span<Vp8LLz77> bestTypes = [0, 0];
        try
        {
            for (Vp8LLz77 type = Vp8LLz77.Standard; type <= Vp8LLz77.Box; type = (Vp8LLz77)((int)type << 1))
            {
                if ((types & type) == 0) continue;
                Vp8LRefs tmp = type switch
                {
                    Vp8LLz77.Rle => matches.Greedy(true),
                    Vp8LLz77.Standard => matches.Greedy(false),
                    _ => (box ??= new Vp8LBackwardRefs(pixels, width, height, quality, false, box: true, maxDegreeOfParallelism)).Greedy(false),
                };
                long bitCost = 0;
                try
                {
                    // Start with the no color cache case.
                    for (int i = 1; i >= 0; i--)
                    {
                        if (i == 1 && !doNoCache) continue;
                        int cacheBits = i == 1 ? 0 : cacheBitsMax;
                        if (i == 0)
                        {
                            cacheBits = CalculateBestCacheSize(pixels, quality, tmp, cacheBits);
                            if (cacheBits > 0)
                            {
                                Vp8LRefs cached = matches.WithCache(tmp, cacheBits);
                                tmp.Dispose();
                                tmp = cached;
                            }
                        }
                        // The cost for i == 0 without a cache was already computed at i == 1.
                        if (!(i == 0 && doNoCache && cacheBits == 0)) bitCost = Estimate(tmp, width, cacheBits);
                        if (bitCost < bestCosts[i])
                        {
                            refs[i]?.Dispose();
                            if (i == 1) refs[1] = tmp.Clone();
                            else { refs[0] = tmp; tmp = null!; }
                            bestCosts[i] = bitCost;
                            bestTypes[i] = type;
                            if (i == 0) cacheBitsBest = cacheBits;
                        }
                    }
                }
                finally { tmp?.Dispose(); }
            }
            // Improve on simple LZ77, but only for high quality: TraceBackwards is costly.
            for (int i = 1; i >= 0; i--)
            {
                if (i == 1 && !doNoCache) continue;
                if (bestTypes[i] is Vp8LLz77.Standard or Vp8LLz77.Box && quality >= 25)
                {
                    Vp8LBackwardRefs chain = bestTypes[i] == Vp8LLz77.Standard ? matches : box!;
                    int cacheBits = i == 1 ? 0 : cacheBitsBest;
                    Vp8LRefs trace = chain.Trace(refs[i]!, cacheBits);
                    if (Estimate(trace, width, cacheBits) < bestCosts[i]) { refs[i]!.Dispose(); refs[i] = trace; }
                    else trace.Dispose();
                }
                if (i == 1 && bestTypes[0] == bestTypes[1] && cacheBitsBest == 0)
                {
                    // Same LZ77 and no cache: reuse the no-cache references.
                    refs[0]!.Dispose();
                    refs[0] = refs[1]!.Clone();
                    break;
                }
            }
            return refs;
        }
        catch
        {
            refs[0]?.Dispose(); refs[1]?.Dispose();
            throw;
        }
    }

    // port: src/enc/histogram_enc.c VP8LHistogramCreate, VP8LHistogramEstimateBits (raw distances)
    private static long Estimate(Vp8LRefs refs, int width, int cacheBits)
    {
        var histogram = new Vp8LHistogram(cacheBits);
        foreach (Vp8LToken token in refs.Tokens) histogram.Add(token, width, planeCodes: false);
        return histogram.Estimate();
    }

    // port: src/enc/backward_references_enc.c CalculateBestCacheSize
    // Every cache size sees all the literals except its hits, so the literal counts are accumulated once and
    // each size only records the literals it turns into cache hits; the final counts are identical.
    private static int CalculateBestCacheSize(uint[] pixels, int quality, Vp8LRefs refs, int cacheBitsMax)
    {
        if (quality <= 25) cacheBitsMax = 0;
        if (cacheBitsMax == 0) return 0;
        int levels = cacheBitsMax + 1, cacheSize = 2 << cacheBitsMax;
        int bufferSize = 2 * cacheSize + (levels + 1) * 1024 + 24;
        uint[] buffer = ArrayPool<uint>.Shared.Rent(bufferSize);
        try
        {
            Span<uint> all = buffer.AsSpan(0, bufferSize);
            all.Clear();
            // Caches (and their hit counts) of all sizes back to back: size i starts at 2^i (index 0 is unused).
            Span<uint> caches = all[..cacheSize], hits = all.Slice(cacheSize, cacheSize);
            Span<uint> literals = all.Slice(2 * cacheSize, 1024), hitLiterals = all.Slice(2 * cacheSize + 1024, levels * 1024);
            Span<uint> lengths = all.Slice(2 * cacheSize + (levels + 1) * 1024, 24);
            int pos = 0;
            foreach (Vp8LToken token in refs.Tokens)
            {
                if (token.Kind == 0)
                {
                    uint pix = pixels[pos++];
                    int a = (int)(pix >> 24), r = (int)((pix >> 16) & 255), g = (int)((pix >> 8) & 255), b = (int)(pix & 255);
                    // The keys of the smaller caches derive from the longest one.
                    int key = (int)(unchecked(pix * 0x1e35a7bdu) >> (32 - cacheBitsMax));
                    literals[g]++; literals[256 + r]++; literals[512 + b]++; literals[768 + a]++;
                    for (int i = cacheBitsMax; i >= 1; i--, key >>= 1)
                    {
                        int slot = (1 << i) + key;
                        if (caches[slot] == pix)
                        {
                            hits[slot]++;
                            int offset = i * 1024;
                            hitLiterals[offset + g]++; hitLiterals[offset + 256 + r]++; hitLiterals[offset + 512 + b]++; hitLiterals[offset + 768 + a]++;
                        }
                        else caches[slot] = pix;
                    }
                }
                else
                {
                    // Distances contribute the same for every cache size; only the length prefix matters.
                    lengths[Vp8LBackwardRefs.Prefix(token.Length).Code]++;
                    uint previous = pixels[pos] ^ 0xffffffffu;
                    for (int k = 0; k < token.Length; k++, pos++)
                    {
                        uint pix = pixels[pos];
                        if (pix == previous) continue;
                        int key = (int)(unchecked(pix * 0x1e35a7bdu) >> (32 - cacheBitsMax));
                        for (int i = cacheBitsMax; i >= 1; i--, key >>= 1) caches[(1 << i) + key] = pix;
                        previous = pix;
                    }
                }
            }
            int best = 0;
            long minimum = long.MaxValue;
            for (int i = 0; i <= cacheBitsMax; i++)
            {
                var histogram = new Vp8LHistogram(i);
                Span<uint> literal = histogram.Counts(0), red = histogram.Counts(1);
                Span<uint> blue = histogram.Counts(2), alpha = histogram.Counts(3);
                for (int v = 0; v < 256; v++)
                {
                    int offset = i * 1024 + v;
                    literal[v] = literals[v] - hitLiterals[offset];
                    red[v] = literals[256 + v] - hitLiterals[offset + 256];
                    blue[v] = literals[512 + v] - hitLiterals[offset + 512];
                    alpha[v] = literals[768 + v] - hitLiterals[offset + 768];
                }
                lengths.CopyTo(literal[256..]);
                if (i > 0) hits.Slice(1 << i, 1 << i).CopyTo(literal[280..]);
                long entropy = histogram.Estimate();
                if (i == 0 || entropy < minimum) { minimum = entropy; best = i; }
            }
            return best;
        }
        finally { ArrayPool<uint>.Shared.Return(buffer); }
    }

    // port: src/enc/vp8l_enc.c StoreImageToBitMask
    private static void StoreImageToBitMask(Vp8LBitWriter writer, int width, int histoBits, Vp8LRefs refs, uint[] histogramImage, Vp8LHuffmanEncoder[][] codes)
    {
        int histoWidth = Vp8LDecoder.SubSampleSize(width, histoBits), tileMask = -(1 << histoBits);
        int x = 0, y = 0, tileX = 0, tileY = 0;
        Vp8LHuffmanEncoder[] current = codes[(histogramImage[0] >> 8) & 0xffff];
        foreach (Vp8LToken token in refs.Tokens)
        {
            if (tileX != (x & tileMask) || tileY != (y & tileMask))
            {
                tileX = x & tileMask; tileY = y & tileMask;
                current = codes[(histogramImage[(y >> histoBits) * histoWidth + (x >> histoBits)] >> 8) & 0xffff];
            }
            WriteToken(writer, token, width, current);
            x += token.Length;
            while (x >= width) { x -= width; y++; }
        }
    }

    // port: src/enc/vp8l_enc.c StoreImageToBitMask (single token)
    internal static void WriteToken(Vp8LBitWriter writer, Vp8LToken token, int width, Vp8LHuffmanEncoder[] codes)
    {
        if (token.Kind == 0)
        {
            codes[0].Write(writer, (int)((token.Value >> 8) & 255));
            codes[1].Write(writer, (int)((token.Value >> 16) & 255));
            codes[2].Write(writer, (int)(token.Value & 255));
            codes[3].Write(writer, (int)(token.Value >> 24));
        }
        else if (token.Kind == 2) codes[0].Write(writer, 280 + (int)token.Value);
        else
        {
            var length = Vp8LBackwardRefs.Prefix(token.Length);
            codes[0].Write(writer, 256 + length.Code); writer.Put(length.Extra, length.Bits);
            var distance = Vp8LBackwardRefs.Prefix(Vp8LBackwardRefs.PlaneCode(width, (int)token.Value));
            codes[4].Write(writer, distance.Code); writer.Put(distance.Extra, distance.Bits);
        }
    }
}
