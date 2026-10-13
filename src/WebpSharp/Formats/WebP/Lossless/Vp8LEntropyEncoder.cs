namespace WebpSharp.Formats.WebP.Lossless;

internal static class Vp8LEntropyEncoder
{
    // port: src/enc/vp8l_enc.c EncodeImageNoHuffman, EncodeImageInternal
    public static Vp8LBitWriter Encode(uint[] pixels, int width, int height, int quality, int method, bool level0, int histoBits)
    {
        using var matches = new Vp8LBackwardRefs(pixels, width, height, quality, method == 0);
        Vp8LBitWriter? best = null;
        try
        {
            for (int kind = 0; kind < (method == 0 ? 1 : 2); kind++)
            {
                using Vp8LRefs greedy = matches.Greedy(kind == 1);
                int cacheBits = 0;
                long bestCost = long.MaxValue;
                for (int bits = 0; bits <= (level0 && quality > 25 && method > 0 ? 11 : 0); bits++)
                {
                    using Vp8LRefs cached = matches.WithCache(greedy, bits);
                    var histogram = new Vp8LHistogram(bits);
                    foreach (Vp8LToken token in cached.Tokens) histogram.Add(token, width);
                    long cost = histogram.Estimate();
                    if (cost < bestCost) { bestCost = cost; cacheBits = bits; }
                }
                using Vp8LRefs refs = matches.WithCache(greedy, cacheBits);
                Choose(ref best, Store(refs, width, height, cacheBits, level0, histoBits, quality, method));
                if (method > 0 && quality >= 25 && kind == 0)
                {
                    using Vp8LRefs trace = matches.Trace(refs, cacheBits);
                    Choose(ref best, Store(trace, width, height, cacheBits, level0, histoBits, quality, method));
                }
            }
            Vp8LBitWriter result = best!; best = null; return result;
        }
        finally { best?.Dispose(); }
    }

    internal static void Choose(ref Vp8LBitWriter? best, Vp8LBitWriter candidate)
    {
        if (best is null || candidate.BitCount < best.BitCount) { best?.Dispose(); best = candidate; }
        else candidate.Dispose();
    }

    private static Vp8LBitWriter Store(Vp8LRefs refs, int width, int height, int cacheBits, bool level0, int histoBits, int quality, int method)
    {
        var histogram = new Vp8LHistogram(cacheBits);
        foreach (Vp8LToken token in refs.Tokens) histogram.Add(token, width);
        var writer = new Vp8LBitWriter();
        writer.Put(cacheBits > 0 ? 1 : 0, 1);
        if (cacheBits > 0) writer.Put(cacheBits, 4);
        if (level0) writer.Put(0, 1);
        Vp8LHuffmanEncoder[] codes = histogram.Codes();
        foreach (Vp8LHuffmanEncoder code in codes) code.Store(writer);
        WriteTokens(writer, refs, width, codes);
        if (level0 && method > 0 && width * height >= 256)
        {
            var cluster = Vp8LHistogramCluster.Build(refs, width, height, histoBits, cacheBits, quality);
            if (cluster.Groups.Length > 1)
            {
                var candidate = new Vp8LBitWriter();
                candidate.Put(cacheBits > 0 ? 1 : 0, 1);
                if (cacheBits > 0) candidate.Put(cacheBits, 4);
                candidate.Put(1, 1); candidate.Put(histoBits - 2, 3);
                uint[] map = cluster.Symbols.Select(i => (uint)i << 8).ToArray();
                int tw = Vp8LDecoder.SubSampleSize(width, histoBits);
                using (Vp8LBitWriter encoded = Encode(map, tw, Vp8LDecoder.SubSampleSize(height, histoBits), quality, method, false, 0)) candidate.Append(encoded);
                Vp8LHuffmanEncoder[][] trees = cluster.Groups.Select(h => h.Codes()).ToArray();
                foreach (Vp8LHuffmanEncoder[] set in trees) foreach (Vp8LHuffmanEncoder code in set) code.Store(candidate);
                int pos = 0;
                foreach (Vp8LToken token in refs.Tokens)
                {
                    int tile = ((pos / width) >> histoBits) * tw + ((pos % width) >> histoBits);
                    WriteToken(candidate, token, width, trees[cluster.Symbols[tile]]); pos += token.Length;
                }
                if (candidate.BitCount < writer.BitCount) { writer.Dispose(); writer = candidate; }
                else candidate.Dispose();
            }
        }
        return writer;
    }

    // port: src/enc/vp8l_enc.c StoreImageToBitMask
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

    private static void WriteTokens(Vp8LBitWriter writer, Vp8LRefs refs, int width, Vp8LHuffmanEncoder[] codes)
    {
        foreach (Vp8LToken token in refs.Tokens) WriteToken(writer, token, width, codes);
    }
}
