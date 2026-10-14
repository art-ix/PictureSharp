using RasterSharp.Formats.WebP.ColorConversion;

namespace RasterSharp.Formats.WebP.Lossy;

internal static partial class Vp8Encoder
{
    private const long Partition0Limit = ((1L << 19) - 2048) << 11;

    // port: src/enc/webp_enc.c WebPEncode, InitVP8Encoder
    public static byte[] Encode(Image image, WebPEncoderOptions options)
    {
        var config = new Vp8EncoderConfig(options);
        using var source = new RgbToYuv(image.Width, image.Height);
        if (options.UseSharpYuv) SharpYuv.Convert(image.GetPixelSpan(), source);
        else source.Convert(image.GetPixelSpan(), config.Dithering);
        if (!options.Exact) source.CleanupTransparent(image.GetPixelSpan());
        using var context = new Vp8EncoderContext(source, config);
        Vp8Analysis.Analyze(context);
        var probabilities = new Vp8EncoderProba();
        var filter = new Vp8EncoderFilter(options.AutoFilter);
        int maxHeaderBits = 65536;
        byte[][] parts = config.RdLevel > 0
            ? TokenLoop(context, probabilities, filter, ref maxHeaderBits)
            : DirectLoop(context, probabilities, filter, ref maxHeaderBits);
        filter.Adjust(context);
        return Vp8Syntax.Encode(context, probabilities, parts);
    }

    // port: src/enc/frame_enc.c SetLoopParams
    private static void SetLoopParams(Vp8EncoderContext context, Vp8EncoderProba probabilities, float quality)
    {
        Vp8Quantization.SetSegmentParams(context, Math.Clamp(quality, 0, 100));
        probabilities.SetSegmentProbas(context);
        probabilities.CalculateLevelCosts(); probabilities.SkipCount = 0;
    }

    // port: src/enc/frame_enc.c RecordResiduals, RecordTokens, CodeResiduals
    private static void Residuals(Vp8EncoderIterator it, in Vp8ModeScore rd, Vp8CoefficientWriter writer)
    {
        it.NzToBytes(); bool i16 = it.Block.Type == 1;
        if (i16) it.TopNz[8] = it.LeftNz[8] = writer.Write(1, 0, it.TopNz[8] + it.LeftNz[8], rd.Dc) ? 1 : 0;
        for (int n = 0; n < 16; n++)
        {
            int x = n & 3, y = n >> 2;
            it.TopNz[x] = it.LeftNz[y] = writer.Write(i16 ? 0 : 3, i16 ? 1 : 0, it.TopNz[x] + it.LeftNz[y], ((ReadOnlySpan<short>)rd.Ac).Slice(n * 16, 16)) ? 1 : 0;
        }
        for (int n = 0; n < 8; n++)
        {
            int channel = (n >> 2) * 2, x = n & 1, y = (n >> 1) & 1;
            it.TopNz[4 + channel + x] = it.LeftNz[4 + channel + y] = writer.Write(2, 0, it.TopNz[4 + channel + x] + it.LeftNz[4 + channel + y], ((ReadOnlySpan<short>)rd.Uv).Slice(n * 16, 16)) ? 1 : 0;
        }
        it.BytesToNz();
    }

    // port: src/enc/frame_enc.c VP8EncTokenLoop
    private static byte[][] TokenLoop(Vp8EncoderContext context, Vp8EncoderProba probabilities, Vp8EncoderFilter filter, ref int maxHeaderBits)
    {
        var stats = new PassStats(context.Config.Options);
        int remaining = context.Config.Options.Passes, refresh = Math.Max(context.MbCount >> 3, 96);
        using var tokens = new Vp8TokenBuffer();
        using var it = new Vp8EncoderIterator(context);
        while (remaining-- > 0)
        {
            bool last = Math.Abs(stats.Delta) <= .4 || remaining == 0 || maxHeaderBits == 0;
            it.Reset(); SetLoopParams(context, probabilities, stats.Quality);
            if (last) { probabilities.Statistics.AsSpan().Clear(); filter.Reset(); }
            tokens.Clear(); long header = 0, distortion = 0;
            if (UseParallelPass(context))
            {
                ParallelPass(context, probabilities, filter, tokens, refresh, last, maxHeaderBits, out header, out distortion);
            }
            else
            {
                int count = refresh;
                var writer = new Vp8CoefficientWriter(probabilities, tokens);
                do
                {
                    it.Import();
                    if (--count < 0) { probabilities.FinalizeTokenProbas(); probabilities.CalculateLevelCosts(); count = refresh; }
                    Vp8ModeScore rd = default;
                    Vp8ModeDecision.Decimate(it, probabilities, ref rd, context.Config.RdLevel, maxHeaderBits);
                    Residuals(it, rd, writer); header += rd.H; distortion += rd.D;
                    if (last) filter.Store(it);
                    it.SaveBoundary();
                } while (it.Next());
            }
            header += probabilities.SegmentSize;
            if (stats.SizeSearch)
            {
                long size = probabilities.FinalizeTokenProbas() + tokens.Emit(null, probabilities.Coefficients);
                stats.Value = ((size + header + 1024) >> 11) + 30;
            }
            else stats.Value = Psnr(distortion, 384L * context.MbCount);
            if (maxHeaderBits > 0 && header > Partition0Limit) { remaining++; maxHeaderBits >>= 1; continue; }
            if (last) break;
            if (stats.Search) stats.NextQuality();
        }
        if (!stats.SizeSearch) probabilities.FinalizeTokenProbas();
        using var bitWriter = new Vp8BitWriter();
        tokens.Emit(bitWriter, probabilities.Coefficients);
        return [bitWriter.Finish()];
    }

    // port: src/enc/frame_enc.c StatLoop, OneStatPass
    private static void StatLoop(Vp8EncoderContext context, Vp8EncoderProba probabilities, ref int maxHeaderBits)
    {
        var stats = new PassStats(context.Config.Options);
        int method = context.Config.Options.Method, remaining = context.Config.Options.Passes, count = context.MbCount;
        if (method is 0 or 3 && !stats.Search) count = method == 3 ? count > 200 ? count >> 1 : 100 : count > 200 ? count >> 2 : 50;
        int rdLevel = method >= 3 || stats.Search ? 1 : 0;
        using var it = new Vp8EncoderIterator(context);
        probabilities.Statistics.AsSpan().Clear();
        while (remaining-- > 0)
        {
            bool last = Math.Abs(stats.Delta) <= .4 || remaining == 0 || maxHeaderBits == 0;
            it.Reset(); SetLoopParams(context, probabilities, stats.Quality);
            int left = count; long size = 0, header = 0, distortion = 0;
            var writer = new Vp8CoefficientWriter(probabilities);
            do
            {
                it.Import(); Vp8ModeScore rd = default;
                if (Vp8ModeDecision.Decimate(it, probabilities, ref rd, rdLevel, maxHeaderBits)) probabilities.SkipCount++;
                Residuals(it, rd, writer); size += rd.R + rd.H; header += rd.H; distortion += rd.D;
                it.SaveBoundary();
            } while (it.Next() && --left > 0);
            header += probabilities.SegmentSize;
            if (stats.SizeSearch)
            {
                size += probabilities.FinalizeSkipProba(context.MbCount) + probabilities.FinalizeTokenProbas();
                stats.Value = ((size + header + 1024) >> 11) + 30;
            }
            else stats.Value = Psnr(distortion, 384L * count);
            if (header == 0) return;
            if (maxHeaderBits > 0 && header > Partition0Limit) { remaining++; maxHeaderBits >>= 1; continue; }
            if (last) break;
            if (stats.Search) { stats.NextQuality(); if (Math.Abs(stats.Delta) <= .4) break; }
        }
        if (!stats.Search || !stats.SizeSearch) { probabilities.FinalizeSkipProba(context.MbCount); probabilities.FinalizeTokenProbas(); }
        probabilities.CalculateLevelCosts();
    }

    // port: src/enc/frame_enc.c VP8EncLoop, ResetAfterSkip
    private static byte[][] DirectLoop(Vp8EncoderContext context, Vp8EncoderProba probabilities, Vp8EncoderFilter filter, ref int maxHeaderBits)
    {
        StatLoop(context, probabilities, ref maxHeaderBits);
        int count = 1 << context.Config.Options.Partitions;
        var writers = new Vp8BitWriter[count];
        try
        {
            for (int i = 0; i < count; i++) writers[i] = new Vp8BitWriter();
            using var it = new Vp8EncoderIterator(context); filter.Reset();
            do
            {
                it.Import(); Vp8ModeScore rd = default;
                if (!Vp8ModeDecision.Decimate(it, probabilities, ref rd, context.Config.RdLevel, maxHeaderBits) || !probabilities.UseSkip)
                    Residuals(it, rd, new Vp8CoefficientWriter(probabilities, writer: writers[it.Y & (count - 1)], record: false));
                else if (it.Block.Type == 1) { context.Nonzero[it.X + 1] = 0; it.LeftNz[8] = 0; }
                else context.Nonzero[it.X + 1] &= 1u << 24;
                filter.Store(it); it.SaveBoundary();
            } while (it.Next());
            var parts = new byte[count][];
            for (int i = 0; i < count; i++) parts[i] = writers[i].Finish();
            return parts;
        }
        finally { foreach (Vp8BitWriter? writer in writers) writer?.Dispose(); }
    }

    // port: src/enc/frame_enc.c GetPSNR
    private static double Psnr(long error, long pixels) => error > 0 && pixels > 0 ? 10 * Math.Log10(255.0 * 255 * pixels / error) : 99;

    // port: src/enc/frame_enc.c InitPassStats, ComputeNextQ
    private sealed class PassStats(WebPEncoderOptions options)
    {
        public float Quality { get; private set; } = options.Quality;
        public float Delta { get; private set; } = 10;
        public double Value { get; set; }
        public bool SizeSearch => options.TargetSize > 0;
        public bool Search => SizeSearch || options.TargetPsnr > 0;
        private float _lastQuality = options.Quality;
        private double _lastValue;
        private bool _first = true;
        public void NextQuality()
        {
            double target = SizeSearch ? options.TargetSize : options.TargetPsnr > 0 ? options.TargetPsnr : 40;
            float delta;
            if (_first) { delta = Value > target ? -Delta : Delta; _first = false; }
            else delta = Value != _lastValue ? (float)((target - Value) / (_lastValue - Value) * (_lastQuality - Quality)) : 0;
            Delta = Math.Clamp(delta, -30, 30); _lastQuality = Quality; _lastValue = Value;
            Quality = Math.Clamp(Quality + Delta, 0, 100);
        }
    }
}
