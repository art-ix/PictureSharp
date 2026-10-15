namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal sealed class Vp8EncoderFilter(bool autoFilter)
{
    private readonly double[]? _statistics = autoFilter ? new double[256] : null;

    public void Reset() => _statistics?.AsSpan().Clear();

    // port: src/enc/filter_enc.c VP8StoreFilterStats, DoFilter, GetILevel
    public void AddStatistic(int index, double value) => _statistics![index] += value;

    public void Store(Vp8EncoderIterator it, Vp8TokenRecorder? deferred = null)
    {
        if (_statistics is null || it.Block.Type == 1 && it.Block.Skip) return;
        int segment = it.Block.Segment, strength = it.Context.Segments[segment].FilterStrength;
        int delta = it.Context.Segments[segment].Quant, step = 2 * delta >= 4 ? 4 : 1;
        Add(segment * 64, MacroblockSsim(it.Input, it.Output));
        for (int d = -delta; d <= delta; d += step)
        {
            int level = strength + d;
            if (level <= 0 || level >= 64) continue;
            it.Output.AsSpan(0, 512).CopyTo(it.Output2);
            int sharpness = it.Context.Config.FilterSharpness, inner = level;
            if (sharpness > 0) { inner >>= sharpness > 4 ? 2 : 1; inner = Math.Min(inner, 9 - sharpness); }
            inner = Math.Max(inner, 1);
            int limit = 2 * level + inner, hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
            Vp8Dsp.HFilter16i(it.Output2, 0, 32, limit, inner, hev);
            Vp8Dsp.HFilter8i(it.Output2, 16, it.Output2, 24, 32, limit, inner, hev);
            Vp8Dsp.VFilter16i(it.Output2, 0, 32, limit, inner, hev);
            Vp8Dsp.VFilter8i(it.Output2, 16, it.Output2, 24, 32, limit, inner, hev);
            Add(segment * 64 + level, MacroblockSsim(it.Input, it.Output2));
        }

        void Add(int index, double value)
        {
            if (deferred is not null) deferred.AddFilterStatistic(index, value);
            else _statistics![index] += value;
        }
    }

    // port: src/enc/filter_enc.c VP8AdjustFilterStrength
    public void Adjust(Vp8EncoderContext context)
    {
        if (_statistics is not null)
        {
            for (int s = 0; s < 4; s++)
            {
                double best = 1.00001 * _statistics[s * 64]; int level = 0;
                for (int i = 1; i < 64; i++) if (_statistics[s * 64 + i] > best) { best = _statistics[s * 64 + i]; level = i; }
                context.Segments[s].FilterStrength = level;
            }
            return;
        }
        if (context.Config.FilterStrength == 0) return;
        int maximum = 0;
        foreach (Vp8Segment segment in context.Segments)
        {
            int delta = segment.MaxEdge * segment.Y2.Q[1] >> 3;
            segment.FilterStrength = Math.Max(segment.FilterStrength, Vp8Quantization.FilterStrengthFromDelta(context.FilterSharpness, delta));
            maximum = Math.Max(maximum, segment.FilterStrength);
        }
        context.FilterLevel = maximum;
    }

    // port: src/enc/filter_enc.c GetMBSSIM
    private static double MacroblockSsim(ReadOnlySpan<byte> source, ReadOnlySpan<byte> reconstructed)
    {
        double sum = 0;
        for (int y = 3; y < 13; y++)
            for (int x = 3; x < 13; x++) sum += Ssim(source, reconstructed, x, y, 16);
        for (int x = 1; x < 7; x++)
            for (int y = 1; y < 7; y++)
            {
                sum += Ssim(source[16..], reconstructed[16..], x, y, 8);
                sum += Ssim(source[24..], reconstructed[24..], x, y, 8);
            }
        return sum;
    }

    // port: src/dsp/ssim.c SSIMGetClipped_C, SSIMCalculation
    private static double Ssim(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int xo, int yo, int size)
    {
        ReadOnlySpan<uint> weights = [1, 2, 3, 4, 3, 2, 1];
        uint n = 0, xm = 0, ym = 0, xxm = 0, yym = 0, xym = 0;
        for (int y = Math.Max(yo - 3, 0); y <= Math.Min(yo + 3, size - 1); y++)
            for (int x = Math.Max(xo - 3, 0); x <= Math.Min(xo + 3, size - 1); x++)
            {
                uint w = weights[3 + x - xo] * weights[3 + y - yo], s1 = a[32 * y + x], s2 = b[32 * y + x];
                n += w; xm += w * s1; ym += w * s2; xxm += w * s1 * s1; yym += w * s2 * s2; xym += w * s1 * s2;
            }
        uint w2 = n * n, c1 = 20 * w2, c2 = 60 * w2, c3 = 64 * w2;
        ulong xmxm = (ulong)xm * xm, ymym = (ulong)ym * ym;
        if (xmxm + ymym < c3) return 1;
        long xmym = (long)xm * ym, sxy = (long)xym * n - xmym;
        ulong sxx = (ulong)xxm * n - xmxm, syy = (ulong)yym * n - ymym;
        ulong numeratorS = (2 * (ulong)Math.Max(0, sxy) + c2) >> 8, denominatorS = (sxx + syy + c2) >> 8;
        ulong numerator = (ulong)(2 * xmym + c1) * numeratorS, denominator = (xmxm + ymym + c1) * denominatorS;
        return (double)numerator / denominator;
    }
}
