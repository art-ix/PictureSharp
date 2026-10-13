using System.Buffers;
using static RasterSharp.Formats.WebP.Lossless.LosslessTransforms;

namespace RasterSharp.Formats.WebP.Lossless;

internal static class Vp8LEncoder
{
    public static byte[] Encode(Image image, WebPEncoderOptions options, uint[]? paletteOverride = null)
    {
        int width = image.Width, height = image.Height, size = checked(width * height);
        uint[] source = ArrayPool<uint>.Shared.Rent(size), work = ArrayPool<uint>.Shared.Rent(size);
        Vp8LBitWriter? best = null;
        try
        {
            ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan();
            bool alpha = false;
            for (int i = 0; i < size; i++)
            {
                Rgba32 p = pixels[i];
                source[i] = ((uint)p.A << 24) | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                alpha |= p.A != 255;
            }
            uint[]? palette = paletteOverride ?? Palette(source.AsSpan(0, size), options.Method != 0);
            int histogramBits = Math.Clamp((palette is null ? 7 : 9) - options.Method, 2, 9);
            while (Vp8LDecoder.SubSampleSize(width, histogramBits) * Vp8LDecoder.SubSampleSize(height, histogramBits) > 2600 && histogramBits < 9) histogramBits++;
            int transformBits = Math.Min(histogramBits, options.Method < 4 ? 6 : options.Method > 4 ? 4 : 5);
            int quality = (int)options.Quality;
            // Transform trials are independent and compared using the actual encoded size.
            int[] modes = options.Method == 6 && quality == 100 ? palette is null ? [0, 1, 2, 3] : [4, 5, 0, 1, 2, 3] : options.Method == 0 ? [palette is null ? 3 : 4] : palette is { Length: <= 16 } ? [4, 5] : palette is null ? [0, 1, 2, 3] : [4, 5, 0, 1, 2, 3];
            foreach (int mode in modes)
            {
                using var writer = new Vp8LBitWriter();
                writer.Put(0x2f, 8); writer.Put(width - 1, 14); writer.Put(height - 1, 14);
                writer.Put(alpha ? 1 : 0, 1); writer.Put(0, 3);
                int currentWidth = width;
                bool predict = mode is 1 or 3 or 5;
                bool subtract = mode is 2 or 3;
                if (mode >= 4)
                {
                    writer.Put(1, 1); writer.Put(3, 2); writer.Put(palette!.Length - 1, 8);
                    uint[] delta = new uint[palette.Length];
                    uint last = 0;
                    for (int i = 0; i < palette.Length; i++) { delta[i] = SubPixels(palette[i], last); last = palette[i]; }
                    using (Vp8LBitWriter encoded = Vp8LEntropyEncoder.Encode(delta, palette.Length, 1, quality, options.Method, false, 0)) writer.Append(encoded);
                    int pack = palette.Length <= 2 ? 3 : palette.Length <= 4 ? 2 : palette.Length <= 16 ? 1 : 0;
                    currentWidth = Vp8LDecoder.SubSampleSize(width, pack);
                    var indices = new Dictionary<uint, int>();
                    for (int i = 0; i < palette.Length; i++) indices[palette[i]] = i;
                    work.AsSpan(0, currentWidth * height).Fill(0xff000000u);
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        work[y * currentWidth + (x >> pack)] |= (uint)indices[source[y * width + x]] << (8 + (x & ((1 << pack) - 1)) * (8 >> pack));
                }
                else
                {
                    source.AsSpan(0, size).CopyTo(work);
                    if (options.NearLossless < 100 && !predict) Vp8LNearLossless.Apply(work, width, height, options.NearLossless);
                }
                if (subtract)
                {
                    writer.Put(1, 1); writer.Put(2, 2);
                    Vp8LForwardTransforms.SubtractGreen(work.AsSpan(0, currentWidth * height));
                }
                if (predict)
                {
                    uint[] map = Vp8LForwardTransforms.Predictor(work, currentWidth, height, transformBits, options.Method == 0, options.Exact || mode >= 4, mode >= 4 ? 100 : options.NearLossless, subtract);
                    WriteTransform(writer, map, currentWidth, height, transformBits, 0, quality, options.Method);
                    if (options.Method > 0 && mode < 4)
                    {
                        uint[] colorMap = Vp8LForwardTransforms.CrossColor(work, currentWidth, height, transformBits, quality);
                        WriteTransform(writer, colorMap, currentWidth, height, transformBits, 1, quality, options.Method);
                    }
                }
                writer.Put(0, 1);
                using (Vp8LBitWriter data = Vp8LEntropyEncoder.Encode(work, currentWidth, height, quality, options.Method, true, histogramBits)) writer.Append(data);
                if (best is null || writer.BitCount < best.BitCount)
                {
                    best?.Dispose(); best = new Vp8LBitWriter(); best.Append(writer);
                }
            }
            byte[] result = best!.ToArray();
            if (paletteOverride is null && options.Method == 6 && options.Quality == 100 && palette is { Length: > 1 })
            {
                uint[] sorted = Vp8LPalette.SortModifiedZeng(source.AsSpan(0, size), width, height, palette.Order().ToArray());
                byte[] candidate = Encode(image, options, sorted);
                if (candidate.Length < result.Length) result = candidate;
            }
            return result;
        }
        finally { best?.Dispose(); ArrayPool<uint>.Shared.Return(source); ArrayPool<uint>.Shared.Return(work); }
    }

    // port: src/enc/vp8l_enc.c ApplyPredictFilter, ApplyCrossColorFilter
    private static void WriteTransform(Vp8LBitWriter writer, uint[] map, int width, int height, int bits, int type, int quality, int method)
    {
        writer.Put(1, 1); writer.Put(type, 2); writer.Put(bits - 2, 3);
        using Vp8LBitWriter encoded = Vp8LEntropyEncoder.Encode(map, Vp8LDecoder.SubSampleSize(width, bits), Vp8LDecoder.SubSampleSize(height, bits), quality, method, false, 0);
        writer.Append(encoded);
    }

    // port: src/enc/vp8l_enc.c PaletteSortMinimizeDeltas, PaletteHasNonMonotonousDeltas
    private static uint[]? Palette(ReadOnlySpan<uint> pixels, bool minimize)
    {
        var colors = new HashSet<uint>();
        foreach (uint pixel in pixels) if (colors.Add(pixel) && colors.Count > 256) return null;
        uint[] palette = colors.Order().ToArray();
        uint previous = 0; int signs = 0;
        foreach (uint color in palette)
        {
            uint diff = SubPixels(color, previous); previous = color;
            for (int c = 0; c < 3; c++)
            {
                int value = (int)((diff >> (c * 8)) & 255);
                if (value != 0) signs |= (value < 128 ? 1 : 2) << (c * 3);
            }
        }
        if (!minimize || (signs & (signs << 1)) == 0) return palette;
        previous = 0;
        for (int i = 0; i < palette.Length; i++)
        {
            int best = i, score = int.MaxValue;
            for (int j = i; j < palette.Length; j++)
            {
                uint diff = SubPixels(palette[j], previous); int distance = 0;
                for (int c = 0; c < 4; c++) { int value = (int)((diff >> (c * 8)) & 255); distance += Math.Min(value, 256 - value) * (c == 3 ? 1 : 9); }
                if (distance < score) { score = distance; best = j; }
            }
            (palette[i], palette[best]) = (palette[best], palette[i]); previous = palette[i];
        }
        return palette;
    }
}
