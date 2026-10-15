using System.Buffers;
using System.Runtime.CompilerServices;
using static PictureSharp.Formats.WebP.Lossless.LosslessTransforms;

namespace PictureSharp.Formats.WebP.Lossless;

internal static partial class Vp8LForwardTransforms
{
    private const int HistoSize = 4 * 256, NumPredModes = 14, PredLowEffort = 11;
    private const long SpatialPredictorBias = 15L << 23;
    private const uint MaskAlpha = 0xff000000u;

    // port: src/enc/predictor_enc.c VP8LResidualImage
    // Picks the predictor of each tile and its best sampling, then replaces argb with the residuals.
    // Near-lossless (quality < 100) quantizes the residuals further.
    public static uint[] ResidualImage(int width, int height, int minBits, int maxBits, bool lowEffort, uint[] argb,
        int nearLosslessQuality, bool exact, bool usedSubtractGreen, out int bestBits)
    {
        int maxQuantization = 1 << (5 - nearLosslessQuality / 20);
        uint[] image;
        if (lowEffort)
        {
            image = new uint[Vp8LDecoder.SubSampleSize(width, maxBits) * Vp8LDecoder.SubSampleSize(height, maxBits)];
            image.AsSpan().Fill(WebPConstants.ArgbBlack | (PredLowEffort << 8));
            bestBits = maxBits;
        }
        else
        {
            image = GetBestPredictorsAndSubSampling(width, height, minBits, maxBits, argb, maxQuantization, exact, usedSubtractGreen, out bestBits);
        }
        CopyImageWithPrediction(width, height, bestBits, image, argb, lowEffort, maxQuantization, exact, usedSubtractGreen);
        return image;
    }

    // port: src/enc/predictor_enc.c GetBestPredictorsAndSubSampling
    // Tiles of 2^minBits are visited in Z order so that super-tiles (up to 2^maxBits) complete as soon as possible;
    // finished tiles feed the histograms of their super-tiles.
    private static uint[] GetBestPredictorsAndSubSampling(int width, int height, int minBits, int maxBits, uint[] argb,
        int maxQuantization, bool exact, bool usedSubtractGreen, out int bestBits)
    {
        int tilesPerRow = Vp8LDecoder.SubSampleSize(width, minBits), tilesPerCol = Vp8LDecoder.SubSampleSize(height, minBits);
        int maxSubsamplingIndex = maxBits - minBits;
        int argbCount = (maxSubsamplingIndex + 1) * NumPredModes * HistoSize;
        uint[] allArgb = ArrayPool<uint>.Shared.Rent(argbCount);
        uint[] rows = ArrayPool<uint>.Shared.Rent(2 * (width + 1));
        byte[] maxDiffs = ArrayPool<byte>.Shared.Rent(width);
        uint[] residuals = ArrayPool<uint>.Shared.Rent(1 << Vp8LEncoder.MaxTransformBits);
        // Entropy terms of the accumulated histograms (see Vp8LEntropy.PrepareCombined), all zero at first.
        long[] sLogAccumulated = ArrayPool<long>.Shared.Rent((maxSubsamplingIndex + 1) * HistoSize);
        long[] totalAccumulated = ArrayPool<long>.Shared.Rent((maxSubsamplingIndex + 1) * 4);
        try
        {
            allArgb.AsSpan(0, argbCount).Clear();
            sLogAccumulated.AsSpan(0, (maxSubsamplingIndex + 1) * HistoSize).Clear();
            totalAccumulated.AsSpan(0, (maxSubsamplingIndex + 1) * 4).Clear();
            var accumulated = new uint[(maxSubsamplingIndex + 1) * HistoSize];
            var predictorHistos = new uint[(maxSubsamplingIndex + 1) * NumPredModes];
            var sumAccumulated = new uint[(maxSubsamplingIndex + 1) * 4];
            var allModes = new uint[maxSubsamplingIndex + 1][];
            for (int i = 0; i <= maxSubsamplingIndex; i++)
                allModes[i] = new uint[Vp8LDecoder.SubSampleSize(width, minBits + i) * Vp8LDecoder.SubSampleSize(height, minBits + i)];
            int maxTileSize = 1 << maxSubsamplingIndex;
            // Residuals are added one by one to super-tile histograms up to this index (faster until 2^4 tiles),
            // above it whole tile histograms are added.
            int updateUpToIndex = Math.Max(Math.Min(4, maxBits), minBits) - minBits;
            int localTileX = 0, localTileY = 0, maxTileX = 0, maxTileY = 0, tileX = 0, tileY = 0;
            while (tileY < tilesPerCol)
            {
                ComputeResidualsForTile(width, height, tileX, tileY, minBits, updateUpToIndex, allArgb, rows, maxDiffs, residuals,
                    argb, maxQuantization, exact, usedSubtractGreen);
                // Update all the super-tiles that are complete.
                int subsamplingIndex = 0;
                while (true)
                {
                    int superTilesPerRow = Vp8LDecoder.SubSampleSize(width, minBits + subsamplingIndex);
                    GetBestPredictorForTile(allArgb, subsamplingIndex, tileX >> subsamplingIndex, tileY >> subsamplingIndex,
                        superTilesPerRow, accumulated, sLogAccumulated, sumAccumulated, totalAccumulated, allModes, predictorHistos);
                    if (subsamplingIndex == maxSubsamplingIndex) break;
                    subsamplingIndex++;
                    if (subsamplingIndex > updateUpToIndex && subsamplingIndex <= maxSubsamplingIndex)
                    {
                        AddVectorEq(allArgb.AsSpan((subsamplingIndex - 1) * NumPredModes * HistoSize, HistoSize * NumPredModes),
                            allArgb.AsSpan(subsamplingIndex * NumPredModes * HistoSize, HistoSize * NumPredModes));
                    }
                    // Stop unless the tile ends a line/column or the super-tile of size 1 << subsamplingIndex.
                    if (!((tileX == tilesPerRow - 1 || (localTileX + 1) % (1 << subsamplingIndex) == 0)
                        && (tileY == tilesPerCol - 1 || (localTileY + 1) % (1 << subsamplingIndex) == 0)))
                    {
                        subsamplingIndex--;
                        break;
                    }
                }
                // Reset the histograms of finished tiles.
                allArgb.AsSpan(0, HistoSize * NumPredModes * (subsamplingIndex + 1)).Clear();
                if (subsamplingIndex == maxSubsamplingIndex)
                {
                    // A new max-tile starts.
                    if (tileX == tilesPerRow - 1) { maxTileX = 0; maxTileY++; }
                    else maxTileX++;
                    localTileX = 0;
                    localTileY = 0;
                }
                else
                {
                    // Proceed with the Z traversal.
                    int coordX = localTileX >> subsamplingIndex, coordY = localTileY >> subsamplingIndex;
                    if (tileX == tilesPerRow - 1 && coordX % 2 == 0) coordY++;
                    else if (coordX % 2 == 0) coordX++;
                    else { coordY++; coordX--; }
                    localTileX = coordX << subsamplingIndex;
                    localTileY = coordY << subsamplingIndex;
                }
                tileX = maxTileX * maxTileSize + localTileX;
                tileY = maxTileY * maxTileSize + localTileY;
            }
            // Figure out the best sampling.
            long bestCost = long.MaxValue;
            uint[] bestMode = allModes[0];
            bestBits = minBits;
            for (int i = 0; i <= maxSubsamplingIndex; i++)
            {
                long cost = Vp8LEntropy.Shannon(predictorHistos.AsSpan(i * NumPredModes, NumPredModes));
                for (int plane = 0; plane < 4; plane++) cost += Vp8LEntropy.Shannon(accumulated.AsSpan(i * HistoSize + plane * 256, 256));
                if (cost < bestCost) { bestCost = cost; bestBits = minBits + i; bestMode = allModes[i]; }
            }
            OptimizeSampling(bestMode, width, height, bestBits, Vp8LEncoder.MaxTransformBits, out bestBits);
            return bestMode;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(allArgb); ArrayPool<uint>.Shared.Return(rows);
            ArrayPool<byte>.Shared.Return(maxDiffs); ArrayPool<uint>.Shared.Return(residuals);
            ArrayPool<long>.Shared.Return(sLogAccumulated); ArrayPool<long>.Shared.Return(totalAccumulated);
        }
    }

    // port: src/enc/predictor_enc.c GetBestPredictorForTile, PredictionCostSpatialHistogram
    private static void GetBestPredictorForTile(uint[] allArgb, int subsamplingIndex, int tileX, int tileY, int tilesPerRow,
        uint[] allAccumulated, long[] allSLogAccumulated, uint[] allSumAccumulated, long[] allTotalAccumulated,
        uint[][] allModes, uint[] predictorHistos)
    {
        Span<uint> accumulated = allAccumulated.AsSpan(subsamplingIndex * HistoSize, HistoSize);
        uint[] modes = allModes[subsamplingIndex];
        int leftMode = tileX > 0 ? (int)((modes[tileY * tilesPerRow + tileX - 1] >> 8) & 0xff) : 0xff;
        int aboveMode = tileY > 0 ? (int)((modes[(tileY - 1) * tilesPerRow + tileX] >> 8) & 0xff) : 0xff;
        Span<long> sLogAccumulated = allSLogAccumulated.AsSpan(subsamplingIndex * HistoSize, HistoSize);
        Span<uint> sumAccumulated = allSumAccumulated.AsSpan(subsamplingIndex * 4, 4);
        Span<long> totalAccumulated = allTotalAccumulated.AsSpan(subsamplingIndex * 4, 4);
        long bestDiff = long.MaxValue;
        int bestMode = 0, bestOffset = 0;
        for (int mode = 0; mode < NumPredModes; mode++)
        {
            int offset = (subsamplingIndex * NumPredModes + mode) * HistoSize;
            ReadOnlySpan<uint> tile = allArgb.AsSpan(offset, HistoSize);
            long cost = 0;
            for (int i = 0; i < 4; i++)
            {
                cost += Vp8LEntropy.Spatial(tile.Slice(i * 256, 256), 1, 94);
                cost += Vp8LEntropy.Combined(tile.Slice(i * 256, 256), accumulated.Slice(i * 256, 256),
                    sLogAccumulated.Slice(i * 256, 256), sumAccumulated[i], totalAccumulated[i]);
            }
            // Favor keeping the areas locally similar.
            if (mode == leftMode) cost -= SpatialPredictorBias;
            if (mode == aboveMode) cost -= SpatialPredictorBias;
            if (cost < bestDiff) { bestDiff = cost; bestMode = mode; bestOffset = offset; }
        }
        ReadOnlySpan<uint> best = allArgb.AsSpan(bestOffset, HistoSize);
        for (int i = 0; i < 4; i++)
        {
            Vp8LEntropy.AddToCombined(best.Slice(i * 256, 256), accumulated.Slice(i * 256, 256), sLogAccumulated.Slice(i * 256, 256),
                ref sumAccumulated[i], ref totalAccumulated[i]);
        }
        modes[tileY * tilesPerRow + tileX] = WebPConstants.ArgbBlack | ((uint)bestMode << 8);
        predictorHistos[subsamplingIndex * NumPredModes + bestMode]++;
    }

    // port: src/enc/predictor_enc.c ComputeResidualsForTile, UpdateHisto
    private static void ComputeResidualsForTile(int width, int height, int tileX, int tileY, int minBits, int updateUpToIndex,
        uint[] allArgb, uint[] rows, byte[] maxDiffs, uint[] residuals, uint[] argb, int maxQuantization, bool exact, bool usedSubtractGreen)
    {
        int startX = tileX << minBits, startY = tileY << minBits, tileSize = 1 << minBits;
        int maxY = Math.Min(tileSize, height - startY), maxX = Math.Min(tileSize, width - startX);
        int haveLeft = startX > 0 ? 1 : 0;
        // The strip covering the tile and its adjacent columns, if they exist.
        int contextStartX = startX - haveLeft;
        int contextWidth = maxX + haveLeft + (maxX < width - startX ? 1 : 0);
        // Rows are one pixel wider than the image: the top-right pixel of the last column is the next row's first pixel.
        int upper = 0, current = width + 1;
        for (int mode = 0; mode < NumPredModes; mode++)
        {
            if (startY > 0)
                argb.AsSpan((startY - 1) * width + contextStartX, maxX + haveLeft + 1).CopyTo(rows.AsSpan(current + contextStartX));
            for (int relativeY = 0; relativeY < maxY; relativeY++)
            {
                int y = startY + relativeY;
                (upper, current) = (current, upper);
                argb.AsSpan(y * width + contextStartX, maxX + haveLeft + (y + 1 < height ? 1 : 0)).CopyTo(rows.AsSpan(current + contextStartX));
                if (maxQuantization > 1 && y >= 1 && y + 1 < height)
                    MaxDiffsForRow(contextWidth, width, argb, y * width + contextStartX, maxDiffs, contextStartX, usedSubtractGreen);
                GetResidual(width, height, rows, upper, current, maxDiffs, mode, startX, startX + maxX, y, maxQuantization, exact, usedSubtractGreen, residuals);
                for (int subsamplingIndex = 0; subsamplingIndex <= updateUpToIndex; subsamplingIndex++)
                {
                    Span<uint> histo = allArgb.AsSpan((subsamplingIndex * NumPredModes + mode) * HistoSize, HistoSize);
                    for (int relativeX = 0; relativeX < maxX; relativeX++)
                    {
                        uint residual = residuals[relativeX];
                        histo[(int)(residual >> 24)]++;
                        histo[256 + (int)((residual >> 16) & 0xff)]++;
                        histo[512 + (int)((residual >> 8) & 0xff)]++;
                        histo[768 + (int)(residual & 0xff)]++;
                    }
                }
            }
        }
    }

    // port: src/enc/predictor_enc.c CopyImageWithPrediction
    private static void CopyImageWithPrediction(int width, int height, int bits, uint[] modes, uint[] argb, bool lowEffort,
        int maxQuantization, bool exact, bool usedSubtractGreen)
    {
        int tilesPerRow = Vp8LDecoder.SubSampleSize(width, bits);
        uint[] rows = ArrayPool<uint>.Shared.Rent(2 * (width + 1));
        byte[] currentMaxDiffs = ArrayPool<byte>.Shared.Rent(width), lowerMaxDiffs = ArrayPool<byte>.Shared.Rent(width);
        try
        {
            int upper = 0, current = width + 1;
            for (int y = 0; y < height; y++)
            {
                (upper, current) = (current, upper);
                argb.AsSpan(y * width, width + (y + 1 < height ? 1 : 0)).CopyTo(rows.AsSpan(current));
                if (lowEffort)
                {
                    GetResidual(width, height, rows, upper, current, currentMaxDiffs, PredLowEffort, 0, width, y, 1, true, false, argb.AsSpan(y * width, width));
                    continue;
                }
                if (maxQuantization > 1)
                {
                    // The lower row's max diffs need the original current row, which is overwritten with residuals below.
                    (currentMaxDiffs, lowerMaxDiffs) = (lowerMaxDiffs, currentMaxDiffs);
                    if (y + 2 < height) MaxDiffsForRow(width, width, argb, (y + 1) * width, lowerMaxDiffs, 0, usedSubtractGreen);
                }
                for (int x = 0; x < width;)
                {
                    int mode = (int)((modes[(y >> bits) * tilesPerRow + (x >> bits)] >> 8) & 0xff);
                    int xEnd = Math.Min(x + (1 << bits), width);
                    GetResidual(width, height, rows, upper, current, currentMaxDiffs, mode, x, xEnd, y, maxQuantization, exact, usedSubtractGreen, argb.AsSpan(y * width + x, xEnd - x));
                    x = xEnd;
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rows);
            ArrayPool<byte>.Shared.Return(currentMaxDiffs); ArrayPool<byte>.Shared.Return(lowerMaxDiffs);
        }
    }

    // port: src/enc/predictor_enc.c GetResidual, PredictBatch
    // Without 'exact', updates the row so that near-lossless deviations and cleaned transparent pixels propagate.
    private static void GetResidual(int width, int height, uint[] rows, int upper, int current, byte[] maxDiffs, int mode,
        int xStart, int xEnd, int y, int maxQuantization, bool exact, bool usedSubtractGreen, Span<uint> output)
    {
        // Without near-lossless or transparent pixels to clean, the row is not modified (PredictBatch).
        if (y > 0 && (exact || (maxQuantization == 1 && !AnyTransparent(rows.AsSpan(current + xStart, xEnd - xStart)))))
        {
            PredictBatch(rows, upper, current, mode, xStart, xEnd, output);
            return;
        }
        for (int x = xStart; x < xEnd; x++)
        {
            uint predict = y == 0 ? x == 0 ? WebPConstants.ArgbBlack : rows[current + x - 1]
                : x == 0 ? rows[upper + x]
                : Predict(mode, rows[current + x - 1], rows, upper + x);
            uint residual;
            if (exact)
            {
                residual = SubPixels(rows[current + x], predict);
            }
            else
            {
                if (maxQuantization == 1 || mode == 0 || y == 0 || y == height - 1 || x == 0 || x == width - 1)
                {
                    residual = SubPixels(rows[current + x], predict);
                }
                else
                {
                    residual = NearLossless(rows[current + x], predict, maxQuantization, maxDiffs[x], usedSubtractGreen);
                    rows[current + x] = AddPixels(predict, residual);
                }
                if ((rows[current + x] & MaskAlpha) == 0)
                {
                    // Fully transparent: choose RGB residual 0, keeping the alpha prediction.
                    residual &= MaskAlpha;
                    rows[current + x] = predict & ~MaskAlpha;
                    // The rightmost pixel of the row above uses this leftmost pixel as its top-right context.
                    if (x == 0 && y != 0) rows[upper + width] = rows[current];
                }
            }
            output[x - xStart] = residual;
        }
    }

    // port: src/enc/predictor_enc.c PredictBatch; src/dsp/lossless_enc.c VP8LPredictorsSub_C
    // Residuals of the pixels xStart..xEnd of a row below the first one.
    // Reads rows[upper + xStart - 1 .. upper + xEnd] and rows[current + xStart - 1 .. current + xEnd - 1].
    internal static void PredictBatch(uint[] rows, int upper, int current, int mode, int xStart, int xEnd, Span<uint> output)
    {
        int x = xStart;
        if (x >= xEnd) return;
        if (x == 0)
        {
            output[0] = SubPixels(rows[current], rows[upper]);
            x = 1;
        }
        if (x < xEnd)
        {
            x += PredictorSubSimd(mode, ref rows[current + x], ref rows[upper + x], xEnd - x, ref output[x - xStart]);
        }
        PredictBatchScalar(rows, upper, current, mode, x, xEnd, output[(x - xStart)..]);
    }

    internal static void PredictBatchScalar(uint[] rows, int upper, int current, int mode, int xStart, int xEnd, Span<uint> output)
    {
        for (int x = xStart; x < xEnd; x++)
            output[x - xStart] = x == 0 ? SubPixels(rows[current], rows[upper]) : SubPixels(rows[current + x], Predict(mode, rows[current + x - 1], rows, upper + x));
    }

    private static bool AnyTransparent(ReadOnlySpan<uint> pixels)
    {
        if (AnyTransparentSimd(pixels, out int i)) return true;
        for (; i < pixels.Length; i++) if ((pixels[i] & MaskAlpha) == 0) return true;
        return false;
    }

    // port: src/dsp/lossless.c VP8LPredictor0_C .. VP8LPredictor13_C
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Predict(int mode, uint left, uint[] rows, int top) => mode switch
    {
        1 => left,
        2 => rows[top],
        3 => rows[top + 1],
        4 => rows[top - 1],
        5 => Average3(left, rows[top], rows[top + 1]),
        6 => Average2(left, rows[top - 1]),
        7 => Average2(left, rows[top]),
        8 => Average2(rows[top - 1], rows[top]),
        9 => Average2(rows[top], rows[top + 1]),
        10 => Average4(left, rows[top - 1], rows[top], rows[top + 1]),
        11 => Select(rows[top], left, rows[top - 1]),
        12 => ClampedAddSubtractFull(left, rows[top], rows[top - 1]),
        13 => ClampedAddSubtractHalf(left, rows[top], rows[top - 1]),
        _ => WebPConstants.ArgbBlack,
    };

    // port: src/enc/predictor_enc.c MaxDiffsForRow, MaxDiffAroundPixel, MaxDiffBetweenPixels, AddGreenToBlueAndRed
    private static void MaxDiffsForRow(int width, int stride, uint[] argb, int offset, byte[] maxDiffs, int diffOffset, bool usedSubtractGreen)
    {
        if (width <= 2) return;
        uint current = Restore(argb[offset], usedSubtractGreen), right = Restore(argb[offset + 1], usedSubtractGreen);
        // maxDiffs[0] and maxDiffs[width - 1] are never used.
        for (int x = 1; x < width - 1; x++)
        {
            uint up = Restore(argb[offset - stride + x], usedSubtractGreen), down = Restore(argb[offset + stride + x], usedSubtractGreen);
            uint left = current;
            current = right;
            right = Restore(argb[offset + x + 1], usedSubtractGreen);
            int diff = Math.Max(Math.Max(MaxDiff(current, up), MaxDiff(current, down)), Math.Max(MaxDiff(current, left), MaxDiff(current, right)));
            maxDiffs[diffOffset + x] = (byte)diff;
        }

        static uint Restore(uint argb, bool usedSubtractGreen)
        {
            if (!usedSubtractGreen) return argb;
            uint green = (argb >> 8) & 0xff;
            return (argb & 0xff00ff00u) | (((argb & 0x00ff00ffu) + ((green << 16) | green)) & 0x00ff00ffu);
        }

        static int MaxDiff(uint p1, uint p2) => Math.Max(
            Math.Max(Math.Abs((int)(p1 >> 24) - (int)(p2 >> 24)), Math.Abs((int)((p1 >> 16) & 0xff) - (int)((p2 >> 16) & 0xff))),
            Math.Max(Math.Abs((int)((p1 >> 8) & 0xff) - (int)((p2 >> 8) & 0xff)), Math.Abs((int)(p1 & 0xff) - (int)(p2 & 0xff))));
    }

    // port: src/enc/predictor_enc.c NearLossless
    // Quantizes each component of the residual to a power of two below max_diff; with subtract-green, red and blue
    // compensate for the green adjustment to avoid accumulating two quantization errors.
    private static uint NearLossless(uint value, uint predict, int maxQuantization, int maxDiff, bool usedSubtractGreen)
    {
        if (maxDiff <= 2) return SubPixels(value, predict);
        int quantization = maxQuantization;
        while (quantization >= maxDiff) quantization >>= 1;
        int alpha = (int)(value >> 24), predictAlpha = (int)(predict >> 24);
        // Fully transparent and fully opaque pixels keep their transparency.
        int a = alpha is 0 or 0xff ? (alpha - predictAlpha) & 0xff : NearLosslessComponent(alpha, predictAlpha, 0xff, quantization);
        int g = NearLosslessComponent((int)(value >> 8) & 0xff, (int)(predict >> 8) & 0xff, 0xff, quantization);
        int newGreen = 0, greenDiff = 0;
        if (usedSubtractGreen)
        {
            newGreen = ((int)(predict >> 8) + g) & 0xff;
            greenDiff = (newGreen - (int)((value >> 8) & 0xff)) & 0xff;
        }
        int r = NearLosslessComponent(((int)(value >> 16) - greenDiff) & 0xff, (int)(predict >> 16) & 0xff, 0xff - newGreen, quantization);
        int b = NearLosslessComponent(((int)value - greenDiff) & 0xff, (int)predict & 0xff, 0xff - newGreen, quantization);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    // port: src/enc/predictor_enc.c NearLosslessComponent
    // Quantizes value - predict modulo 256 without crossing the (inclusive) boundary.
    private static int NearLosslessComponent(int value, int predict, int boundary, int quantization)
    {
        int residual = (value - predict) & 0xff, boundaryResidual = (boundary - predict) & 0xff;
        int lower = residual & ~(quantization - 1), upper = lower + quantization;
        // Resolve ties towards a value closer to the prediction.
        int bias = ((boundary - value) & 0xff) < boundaryResidual ? 1 : 0;
        if (residual - lower < upper - residual + bias)
            return residual > boundaryResidual && lower <= boundaryResidual ? lower + (quantization >> 1) : lower;
        return residual <= boundaryResidual && upper > boundaryResidual ? lower + (quantization >> 1) : upper & 0xff;
    }

    // port: src/dsp/lossless_enc.c AddVectorEq_C
    private static void AddVectorEq(ReadOnlySpan<uint> source, Span<uint> destination)
    {
        for (int i = 0; i < source.Length; i++) destination[i] += source[i];
    }
}
