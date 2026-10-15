using System.Buffers;
using PictureSharp.Formats.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Lossless;

namespace PictureSharp.Infrastructure.Codecs.WebP.Alpha;

internal static class AlphaEncoder
{
    // port: src/enc/alpha_enc.c EncodeAlpha, ApplyFiltersAndEncode
    public static byte[] Encode(ReadOnlySpan<Rgba32> pixels, int width, int height, WebPEncoderOptions options,
        CancellationToken cancellationToken = default)
    {
        int size = checked(width * height);
        byte[] plane = ArrayPool<byte>.Shared.Rent(size), filtered = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            for (int i = 0; i < size; i++) plane[i] = pixels[i].A;
            if (options.AlphaQuality < 100)
                Quantize(plane.AsSpan(0, size), options.AlphaQuality <= 70 ? 2 + options.AlphaQuality / 5 : 16 + (options.AlphaQuality - 70) * 8);
            int filterMap = options.AlphaCompression ? GetFilterMap(plane.AsSpan(0, size), width, height, options.AlphaFilter, options.Method) : 1;
            byte[]? best = null;
            for (int filter = 0; filter < 4; filter++)
            {
                if ((filterMap & (1 << filter)) == 0) continue;
                cancellationToken.ThrowIfCancellationRequested();
                Filter(plane, filtered, width, height, filter);
                byte[]? compressed = options.AlphaCompression ? EncodeLossless(filtered.AsSpan(0, size), width, height, options.Method, options.AlphaQuality == 100, cancellationToken) : null;
                bool useCompression = compressed is not null && compressed.Length <= size;
                byte[] candidate = new byte[1 + (useCompression ? compressed!.Length : size)];
                candidate[0] = (byte)((useCompression ? 1 : 0) | (filter << 2) | (options.AlphaQuality < 100 ? 16 : 0));
                if (useCompression) compressed!.CopyTo(candidate, 1);
                else filtered.AsSpan(0, size).CopyTo(candidate.AsSpan(1));
                if (best is null || candidate.Length < best.Length) best = candidate;
            }
            return best!;
        }
        finally { ArrayPool<byte>.Shared.Return(plane); ArrayPool<byte>.Shared.Return(filtered); }
    }

    // port: src/enc/alpha_enc.c EncodeLossless
    private static byte[] EncodeLossless(ReadOnlySpan<byte> alpha, int width, int height, int method, bool fullQuality,
        CancellationToken cancellationToken)
    {
        // The alpha values go to the green channel of transparent black pixels (exact, so they stay as they are).
        uint[] argb = ArrayPool<uint>.Shared.Rent(alpha.Length);
        try
        {
            for (int i = 0; i < alpha.Length; i++) argb[i] = (uint)alpha[i] << 8;
            var options = new WebPEncoderOptions { Lossless = true, Exact = true, Method = method, Quality = fullQuality && method == 6 ? 100 : 8 * method };
            return Vp8LEncoder.Encode(argb, width, height, alpha: true, options, header: false, cancellationToken);
        }
        finally { ArrayPool<uint>.Shared.Return(argb); }
    }

    // port: src/dsp/filters.c HorizontalFilter_C, VerticalFilter_C, GradientFilter_C
    internal static void Filter(ReadOnlySpan<byte> source, Span<byte> output, int width, int height, int filter)
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, prediction = 0;
                if (filter != 0)
                {
                    if (y == 0) prediction = x == 0 ? 0 : source[i - 1];
                    else if (x == 0) prediction = source[i - width];
                    else prediction = filter switch
                    {
                        1 => source[i - 1],
                        2 => source[i - width],
                        _ => Math.Clamp(source[i - 1] + source[i - width] - source[i - width - 1], 0, 255),
                    };
                }
                output[i] = (byte)(source[i] - prediction);
            }
    }

    // port: src/enc/alpha_enc.c GetFilterMap; src/utils/filters_utils.c WebPEstimateBestFilter
    private static int GetFilterMap(ReadOnlySpan<byte> alpha, int width, int height, WebPAlphaFilter filter, int effort)
    {
        if (filter == WebPAlphaFilter.None) return 1;
        if (filter == WebPAlphaFilter.Best) return 15;
        Span<bool> used = stackalloc bool[256]; used.Clear();
        foreach (byte a in alpha) used[a] = true;
        int colors = 0;
        foreach (bool value in used) if (value) colors++;
        int best = 0;
        if (colors > 16)
        {
            Span<bool> bins = stackalloc bool[64]; bins.Clear();
            for (int y = 2; y < height - 1; y += 2)
            {
                int mean = alpha[y * width];
                for (int x = 2; x < width - 1; x += 2)
                {
                    int i = y * width + x, value = alpha[i];
                    bins[Math.Abs(value - mean) >> 4] = true;
                    bins[16 + (Math.Abs(value - alpha[i - 1]) >> 4)] = true;
                    bins[32 + (Math.Abs(value - alpha[i - width]) >> 4)] = true;
                    int gradient = Math.Clamp(alpha[i - 1] + alpha[i - width] - alpha[i - width - 1], 0, 255);
                    bins[48 + (Math.Abs(value - gradient) >> 4)] = true;
                    mean = (3 * mean + value + 2) >> 2;
                }
            }
            int bestScore = int.MaxValue;
            for (int f = 0; f < 4; f++)
            {
                int score = 0;
                for (int i = 0; i < 16; i++) if (bins[f * 16 + i]) score += i;
                if (score < bestScore) { bestScore = score; best = f; }
            }
        }
        return (1 << best) | (effort > 3 || colors > 192 ? 1 : 0);
    }

    // port: src/utils/quant_levels_utils.c QuantizeLevels
    internal static void Quantize(Span<byte> data, int levels)
    {
        Span<int> frequencies = stackalloc int[256], classes = stackalloc int[256];
        Span<double> centers = stackalloc double[256], sums = stackalloc double[256], counts = stackalloc double[256];
        frequencies.Clear(); classes.Clear(); centers.Clear();
        int min = 255, max = 0, colors = 0;
        foreach (byte value in data)
        {
            if (frequencies[value]++ == 0) colors++;
            min = Math.Min(min, value); max = Math.Max(max, value);
        }
        if (colors <= levels) return;
        for (int i = 0; i < levels; i++) centers[i] = min + (double)(max - min) * i / (levels - 1);
        classes[max] = levels - 1;
        double lastError = 1e38;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            sums.Clear(); counts.Clear(); int slot = 0;
            for (int s = min; s <= max; s++)
            {
                while (slot < levels - 1 && 2 * s > centers[slot] + centers[slot + 1]) slot++;
                if (frequencies[s] > 0) { sums[slot] += (double)s * frequencies[s]; counts[slot] += frequencies[s]; }
                classes[s] = slot;
            }
            for (slot = 1; slot < levels - 1; slot++) if (counts[slot] > 0) centers[slot] = sums[slot] / counts[slot];
            double error = 0;
            for (int s = min; s <= max; s++) { double d = s - centers[classes[s]]; error += frequencies[s] * d * d; }
            if (lastError - error < 1e-4 * data.Length) break;
            lastError = error;
        }
        Span<byte> map = stackalloc byte[256];
        for (int s = min; s <= max; s++) map[s] = (byte)(centers[classes[s]] + 0.5);
        foreach (ref byte value in data) value = map[value];
    }
}
