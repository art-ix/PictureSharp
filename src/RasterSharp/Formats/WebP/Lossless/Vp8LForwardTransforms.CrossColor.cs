namespace RasterSharp.Formats.WebP.Lossless;

internal static partial class Vp8LForwardTransforms
{
    private const long CrossColorBias = 3L << 23;

    // port: src/enc/predictor_enc.c VP8LColorSpaceTransform, GetBestColorTransformForTile, CopyTileWithColorTransform
    public static uint[] ColorSpaceTransform(int width, int height, int bits, int quality, uint[] argb, out int bestBits)
    {
        int tileSize = 1 << bits;
        int tilesX = Vp8LDecoder.SubSampleSize(width, bits), tilesY = Vp8LDecoder.SubSampleSize(height, bits);
        var image = new uint[tilesX * tilesY];
        Span<uint> accumulatedRed = stackalloc uint[256], accumulatedBlue = stackalloc uint[256];
        accumulatedRed.Clear(); accumulatedBlue.Clear();
        uint previousX = 0, previousY = 0;
        for (int tileY = 0; tileY < tilesY; tileY++)
        for (int tileX = 0; tileX < tilesX; tileX++)
        {
            int startX = tileX * tileSize, startY = tileY * tileSize;
            int endX = Math.Min(startX + tileSize, width), endY = Math.Min(startY + tileSize, height);
            int offset = tileY * tilesX + tileX;
            if (tileY != 0) previousY = image[offset - tilesX];
            int greenToRed = GetBestGreenToRed(argb, width, startX, startY, endX, endY, previousX, previousY, quality, accumulatedRed);
            var (greenToBlue, redToBlue) = GetBestGreenRedToBlue(argb, width, startX, startY, endX, endY, previousX, previousY, quality, accumulatedBlue);
            previousX = 0xff000000u | ((uint)(redToBlue & 0xff) << 16) | ((uint)(greenToBlue & 0xff) << 8) | (uint)(greenToRed & 0xff);
            image[offset] = previousX;
            for (int y = startY; y < endY; y++)
            for (int x = startX; x < endX; x++)
            {
                // port: src/dsp/lossless_enc.c VP8LTransformColor_C
                int i = y * width + x;
                uint p = argb[i];
                sbyte green = (sbyte)(p >> 8), red = (sbyte)(p >> 16);
                int newRed = (red & 0xff) - LosslessTransforms.ColorTransformDelta((sbyte)greenToRed, green);
                int newBlue = (int)(p & 0xff) - LosslessTransforms.ColorTransformDelta((sbyte)greenToBlue, green)
                    - LosslessTransforms.ColorTransformDelta((sbyte)redToBlue, red);
                argb[i] = (p & 0xff00ff00u) | ((uint)(newRed & 0xff) << 16) | (uint)(newBlue & 0xff);
            }
            // Gather accumulated histogram data; repeated pixels are handled by backward references.
            for (int y = startY; y < endY; y++)
            for (int i = y * width + startX, end = y * width + endX; i < end; i++)
            {
                uint pix = argb[i];
                if (i >= 2 && pix == argb[i - 2] && pix == argb[i - 1]) continue;
                if (i >= width + 2 && argb[i - 2] == argb[i - width - 2] && argb[i - 1] == argb[i - width - 1] && pix == argb[i - width]) continue;
                accumulatedRed[(int)((pix >> 16) & 0xff)]++;
                accumulatedBlue[(int)(pix & 0xff)]++;
            }
        }
        OptimizeSampling(image, width, height, bits, Vp8LEncoder.MaxTransformBits, out bestBits);
        return image;
    }

    // port: src/enc/predictor_enc.c GetBestGreenToRed
    private static int GetBestGreenToRed(uint[] argb, int stride, int startX, int startY, int endX, int endY,
        uint previousX, uint previousY, int quality, ReadOnlySpan<uint> accumulated)
    {
        int maxIters = 4 + ((7 * quality) >> 8);
        int best = 0;
        long bestDiff = CostRed(argb, stride, startX, startY, endX, endY, previousX, previousY, best, accumulated);
        for (int iter = 0; iter < maxIters; iter++)
        {
            // ColorTransformDelta is 3.5 fixed point: an initial delta of 32 explores (-2, 2).
            int delta = 32 >> iter;
            for (int offset = -delta; offset <= delta; offset += 2 * delta)
            {
                int candidate = offset + best;
                long diff = CostRed(argb, stride, startX, startY, endX, endY, previousX, previousY, candidate, accumulated);
                if (diff < bestDiff) { bestDiff = diff; best = candidate; }
            }
        }
        return best;
    }

    // port: src/enc/predictor_enc.c GetBestGreenRedToBlue
    private static (int GreenToBlue, int RedToBlue) GetBestGreenRedToBlue(uint[] argb, int stride, int startX, int startY, int endX, int endY,
        uint previousX, uint previousY, int quality, ReadOnlySpan<uint> accumulated)
    {
        ReadOnlySpan<sbyte> offsets = [0, -1, 0, 1, -1, 0, 1, 0, -1, -1, -1, 1, 1, -1, 1, 1];
        ReadOnlySpan<sbyte> deltas = [16, 16, 8, 4, 2, 2, 2];
        int iters = quality < 25 ? 1 : quality > 50 ? 7 : 4;
        int greenToBlue = 0, redToBlue = 0;
        long bestDiff = CostBlue(argb, stride, startX, startY, endX, endY, previousX, previousY, 0, 0, accumulated);
        for (int iter = 0; iter < iters; iter++)
        {
            int delta = deltas[iter];
            for (int axis = 0; axis < 8; axis++)
            {
                int g = offsets[2 * axis] * delta + greenToBlue, r = offsets[2 * axis + 1] * delta + redToBlue;
                long diff = CostBlue(argb, stride, startX, startY, endX, endY, previousX, previousY, g, r, accumulated);
                if (diff < bestDiff) { bestDiff = diff; greenToBlue = g; redToBlue = r; }
                // Only axis aligned diffs for lower quality.
                if (quality < 25 && iter == 4) break;
            }
            // Further iterations would not help.
            if (delta == 2 && greenToBlue == 0 && redToBlue == 0) break;
        }
        return (greenToBlue, redToBlue);
    }

    // port: src/enc/predictor_enc.c GetPredictionCostCrossColorRed; src/dsp/lossless_enc.c VP8LCollectColorRedTransforms_C
    private static long CostRed(uint[] argb, int stride, int startX, int startY, int endX, int endY,
        uint previousX, uint previousY, int greenToRed, ReadOnlySpan<uint> accumulated)
    {
        Span<uint> histo = stackalloc uint[256];
        histo.Clear();
        for (int y = startY; y < endY; y++)
        for (int i = y * stride + startX, end = y * stride + endX; i < end; i++)
        {
            uint p = argb[i];
            histo[((int)(p >> 16) - LosslessTransforms.ColorTransformDelta((sbyte)greenToRed, (sbyte)(p >> 8))) & 0xff]++;
        }
        long cost = CostCrossColor(accumulated, histo);
        if ((byte)greenToRed == (byte)previousX) cost -= CrossColorBias;
        if ((byte)greenToRed == (byte)previousY) cost -= CrossColorBias;
        if (greenToRed == 0) cost -= CrossColorBias;
        return cost;
    }

    // port: src/enc/predictor_enc.c GetPredictionCostCrossColorBlue; src/dsp/lossless_enc.c VP8LCollectColorBlueTransforms_C
    private static long CostBlue(uint[] argb, int stride, int startX, int startY, int endX, int endY,
        uint previousX, uint previousY, int greenToBlue, int redToBlue, ReadOnlySpan<uint> accumulated)
    {
        Span<uint> histo = stackalloc uint[256];
        histo.Clear();
        for (int y = startY; y < endY; y++)
        for (int i = y * stride + startX, end = y * stride + endX; i < end; i++)
        {
            uint p = argb[i];
            int blue = (int)(p & 0xff) - LosslessTransforms.ColorTransformDelta((sbyte)greenToBlue, (sbyte)(p >> 8))
                - LosslessTransforms.ColorTransformDelta((sbyte)redToBlue, (sbyte)(p >> 16));
            histo[blue & 0xff]++;
        }
        long cost = CostCrossColor(accumulated, histo);
        if ((byte)greenToBlue == (byte)(previousX >> 8)) cost -= CrossColorBias;
        if ((byte)greenToBlue == (byte)(previousY >> 8)) cost -= CrossColorBias;
        if ((byte)redToBlue == (byte)(previousX >> 16)) cost -= CrossColorBias;
        if ((byte)redToBlue == (byte)(previousY >> 16)) cost -= CrossColorBias;
        if (greenToBlue == 0) cost -= CrossColorBias;
        if (redToBlue == 0) cost -= CrossColorBias;
        return cost;
    }

    // port: src/enc/predictor_enc.c PredictionCostCrossColor
    private static long CostCrossColor(ReadOnlySpan<uint> accumulated, ReadOnlySpan<uint> counts) =>
        Vp8LEntropy.Combined(counts, accumulated) + Vp8LEntropy.Spatial(counts, 3, 240);
}
