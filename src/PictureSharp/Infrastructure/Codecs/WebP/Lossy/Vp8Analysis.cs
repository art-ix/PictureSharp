using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal static class Vp8Analysis
{
    // port: src/enc/analysis_enc.c GetAlpha
    private static int GetAlpha((int MaxValue, int LastNonZero) histogram) => histogram.MaxValue > 1 ? 510 * histogram.LastNonZero / histogram.MaxValue : 0;

    // port: src/enc/analysis_enc.c VP8EncAnalyze, MBAnalyze, DoSegmentsJob
    // Macroblocks are analyzed independently from the source, so rows run in parallel; the sums are integers.
    public static void Analyze(Vp8EncoderContext context)
    {
        if (context.SegmentCount == 1 && context.Config.Options.Method > 1)
        {
            for (int i = 0; i < context.MbCount; i++) context.Blocks[i] = new Vp8Macroblock { Type = 1 };
            context.Alpha = context.UvAlpha = 0;
            context.Segments[0].Alpha = context.Segments[0].Beta = 0;
            return;
        }
        int[] histogram = new int[256];
        long alphaSum = 0, uvAlphaSum = 0;
        Lock gate = new();
        int degree = context.Config.Options.MaxDegreeOfParallelism;
        // Below 256 macroblocks (the VP8 decoder's threshold) extra workers cost more iterator setup than they save.
        if (!Parallelism.IsWorthwhile(degree, (long)context.MbWidth * context.MbHeight, 256)) degree = 1;
        Parallel.For(0, context.MbHeight, Parallelism.Options(degree, context.CancellationToken),
            () => (Iterator: new Vp8EncoderIterator(context, resetContext: false), Histogram: new int[256], Alpha: 0L, UvAlpha: 0L),
            (y, _, local) =>
            {
                Vp8EncoderIterator iterator = local.Iterator;
                iterator.StartRow(y);
                for (int x = 0; x < context.MbWidth; x++, iterator.Advance())
                {
                    iterator.Import(analysis: true); iterator.MakePredictions();
                    iterator.SetIntra16Mode(0); iterator.Block.Skip = false; iterator.Block.Segment = 0;
                    int alpha = context.Config.Options.Method <= 1 ? FastAnalyze(iterator) : AnalyzeLuma(iterator);
                    int uvAlpha = AnalyzeUv(iterator);
                    alpha = Math.Clamp(255 - ((3 * alpha + uvAlpha + 2) >> 2), 0, 255);
                    local.Histogram[alpha]++;
                    iterator.Block.Alpha = (byte)alpha;
                    local.Alpha += alpha; local.UvAlpha += uvAlpha;
                }
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    for (int i = 0; i < 256; i++) histogram[i] += local.Histogram[i];
                    alphaSum += local.Alpha; uvAlphaSum += local.UvAlpha;
                }
                local.Iterator.Dispose();
            });
        context.Alpha = (int)(alphaSum / context.MbCount); context.UvAlpha = (int)(uvAlphaSum / context.MbCount);
        AssignSegments(context, histogram);
    }

    // port: src/enc/analysis_enc.c MBAnalyzeBestIntra16Mode
    private static int AnalyzeLuma(Vp8EncoderIterator iterator)
    {
        int bestAlpha = -1, bestMode = 0;
        for (int mode = 0; mode < 2; mode++)
        {
            int alpha = GetAlpha(Vp8EncoderDsp.CollectHistogram(iterator.Input, iterator.Predictions.AsSpan(Vp8EncoderDsp.Pred16Offsets[mode]), 0, 16));
            if (alpha > bestAlpha) { bestAlpha = alpha; bestMode = mode; }
        }
        iterator.SetIntra16Mode(bestMode);
        return bestAlpha;
    }

    // port: src/enc/analysis_enc.c FastMBAnalyze; src/dsp/enc.c Mean16x4_C
    private static int FastAnalyze(Vp8EncoderIterator iterator)
    {
        uint threshold = (uint)(8 + 9 * (int)iterator.Context.Config.Options.Quality / 100), mean = 0, square = 0;
        for (int block = 0; block < 16; block++)
        {
            uint sum = 0;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++) sum += iterator.Input[Vp8EncoderDsp.Scan[block] + 32 * y + x];
            mean += sum; square += sum * sum;
        }
        if (threshold * square < mean * mean) iterator.SetIntra16Mode(0);
        else { Span<byte> modes = stackalloc byte[16]; modes.Clear(); iterator.SetIntra4Mode(modes); }
        return 0;
    }

    // port: src/enc/analysis_enc.c MBAnalyzeBestUVMode
    private static int AnalyzeUv(Vp8EncoderIterator iterator)
    {
        int bestAlpha = -1, smallestAlpha = 0, bestMode = 0;
        for (int mode = 0; mode < 2; mode++)
        {
            int alpha = GetAlpha(Vp8EncoderDsp.CollectHistogram(iterator.Input.AsSpan(16), iterator.Predictions.AsSpan(Vp8EncoderDsp.Pred8Offsets[mode]), 16, 24));
            bestAlpha = Math.Max(alpha, bestAlpha);
            if (mode == 0 || alpha < smallestAlpha) { smallestAlpha = alpha; bestMode = mode; }
        }
        iterator.Block.UvMode = (byte)bestMode;
        return bestAlpha;
    }

    // port: src/enc/analysis_enc.c AssignSegments, SetSegmentAlphas
    private static void AssignSegments(Vp8EncoderContext context, ReadOnlySpan<int> histogram)
    {
        int count = context.SegmentCount, min = 0, max = 255;
        while (histogram[min] == 0) min++;
        while (max > min && histogram[max] == 0) max--;
        Span<int> centers = stackalloc int[4], map = stackalloc int[256];
        Span<int> accumulated = stackalloc int[4], distances = stackalloc int[4];
        for (int i = 0; i < count; i++) centers[i] = min + (2 * i + 1) * (max - min) / (2 * count);
        int weightedAverage = 0;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            accumulated.Clear(); distances.Clear(); int n = 0;
            for (int a = min; a <= max; a++)
            {
                if (histogram[a] == 0) continue;
                while (n + 1 < count && Math.Abs(a - centers[n + 1]) < Math.Abs(a - centers[n])) n++;
                map[a] = n; distances[n] += a * histogram[a]; accumulated[n] += histogram[a];
            }
            int displaced = 0, totalWeight = 0;
            weightedAverage = 0;
            for (n = 0; n < count; n++)
            {
                if (accumulated[n] == 0) continue;
                int center = (distances[n] + accumulated[n] / 2) / accumulated[n];
                displaced += Math.Abs(centers[n] - center); centers[n] = center;
                weightedAverage += center * accumulated[n]; totalWeight += accumulated[n];
            }
            weightedAverage = (weightedAverage + totalWeight / 2) / totalWeight;
            if (displaced < 5) break;
        }
        for (int i = 0; i < context.MbCount; i++)
        {
            ref Vp8Macroblock block = ref context.Blocks[i];
            block.Segment = (byte)map[block.Alpha]; block.Alpha = (byte)centers[block.Segment];
        }
        min = max = centers[0];
        for (int i = 1; i < count; i++) { min = Math.Min(min, centers[i]); max = Math.Max(max, centers[i]); }
        if (max == min) max = min + 1;
        for (int i = 0; i < count; i++)
        {
            context.Segments[i].Alpha = Math.Clamp(255 * (centers[i] - weightedAverage) / (max - min), -127, 127);
            context.Segments[i].Beta = Math.Clamp(255 * (centers[i] - min) / (max - min), 0, 255);
        }
    }
}
