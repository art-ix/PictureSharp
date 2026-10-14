using System.Buffers;
using System.Numerics;
using static RasterSharp.Formats.WebP.Lossless.LosslessTransforms;

namespace RasterSharp.Formats.WebP.Lossless;

internal static class Vp8LEncoder
{
    public const int MinHuffmanBits = 2, MaxHuffmanBits = 9, MinTransformBits = 2, MaxTransformBits = 9;
    public const int MaxColorCacheBits = 10;
    private const int MaxHuffImageSize = 2600, MaxPredictorImageSize = 1 << 14, MaxPaletteSize = 256;

    private enum EntropyIx { Direct, Spatial, SubGreen, SpatialSubGreen, Palette, PaletteAndSpatial }

    private enum PaletteSorting { SortedDefault, MinimizeDelta, ModifiedZeng, Unused }

    private sealed record CrunchConfig(EntropyIx Entropy, PaletteSorting Sorting, Vp8LCrunchSubConfig[] SubConfigs);

    // port: src/enc/webp_enc.c WebPEncode (lossless branch); src/enc/vp8l_enc.c VP8LEncodeImage
    public static byte[] Encode(Image image, WebPEncoderOptions options)
    {
        int width = image.Width, height = image.Height, size = checked(width * height);
        uint[] argb = ArrayPool<uint>.Shared.Rent(size);
        try
        {
            ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan();
            bool alpha = false;
            for (int i = 0; i < size; i++)
            {
                Rgba32 p = pixels[i];
                uint value = ((uint)p.A << 24) | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                // port: src/enc/picture_tools_enc.c WebPReplaceTransparentPixels
                argb[i] = !options.Exact && p.A == 0 ? 0 : value;
                alpha |= p.A != 255;
            }
            return Encode(argb, width, height, alpha, options, header: true);
        }
        finally { ArrayPool<uint>.Shared.Return(argb); }
    }

    // Encodes argb[..width * height] in place; without the 5-byte header for the alpha plane (ALPH chunk).
    internal static byte[] Encode(uint[] argb, int width, int height, bool alpha, WebPEncoderOptions options, bool header)
    {
        using var writer = new Vp8LBitWriter();
        if (header)
        {
            writer.Put(0x2f, 8); writer.Put(width - 1, 14); writer.Put(height - 1, 14);
            writer.Put(alpha ? 1 : 0, 1); writer.Put(0, 3);
        }
        using (Vp8LBitWriter stream = EncodeStream(argb, width, height, options)) writer.Append(stream);
        return writer.ToArray();
    }

    // port: src/enc/vp8l_enc.c VP8LEncodeStream
    // Crunch configurations are independent; the smallest result wins and ties go to the earliest
    // configuration, as in the sequential order, so the output does not depend on threading.
    private static Vp8LBitWriter EncodeStream(uint[] argb, int width, int height, WebPEncoderOptions options)
    {
        uint[]? paletteSorted = GetColorPalette(argb.AsSpan(0, width * height));
        CrunchConfig[] configs = Analyze(argb, width, height, options, paletteSorted, out int histoBits, out int transformBits, out bool redAndBlueAlwaysZero);
        var results = new Vp8LBitWriter[configs.Length];
        try
        {
            if (configs.Length > 1 && options.MaxDegreeOfParallelism != 1)
            {
                // The configurations already use the threads; nested parallelism would only add buffers.
                WebPEncoderOptions inner = options with { MaxDegreeOfParallelism = 1 };
                Parallel.For(0, configs.Length, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism },
                    i => results[i] = EncodeConfig(argb, width, height, inner, configs[i], paletteSorted, histoBits, transformBits, redAndBlueAlwaysZero));
            }
            else
            {
                for (int i = 0; i < configs.Length; i++)
                    results[i] = EncodeConfig(argb, width, height, options, configs[i], paletteSorted, histoBits, transformBits, redAndBlueAlwaysZero);
            }
            int best = 0;
            for (int i = 1; i < results.Length; i++) if (results[i].ByteCount < results[best].ByteCount) best = i;
            Vp8LBitWriter result = results[best];
            results[best] = null!;
            return result;
        }
        finally { foreach (Vp8LBitWriter? r in results) r?.Dispose(); }
    }

    // port: src/enc/vp8l_enc.c EncoderAnalyze
    private static CrunchConfig[] Analyze(uint[] argb, int width, int height, WebPEncoderOptions options, uint[]? paletteSorted,
        out int histoBits, out int transformBits, out bool redAndBlueAlwaysZero)
    {
        int method = options.Method, quality = (int)options.Quality;
        bool lowEffort = method == 0, usePalette = paletteSorted is not null;
        int paletteSize = paletteSorted?.Length ?? 0;
        histoBits = GetHistoBits(method, usePalette, width, height);
        transformBits = GetTransformBits(method, histoBits);
        redAndBlueAlwaysZero = false;
        var configs = new List<(EntropyIx Entropy, PaletteSorting Sorting)>();
        int lz77s;
        bool doNoCache = false;
        if (lowEffort)
        {
            configs.Add(usePalette ? (EntropyIx.Palette, PaletteSorting.SortedDefault) : (EntropyIx.SpatialSubGreen, PaletteSorting.Unused));
            lz77s = 1;
        }
        else
        {
            lz77s = paletteSize is > 0 and <= 16 ? 2 : 1;
            EntropyIx minEntropy = AnalyzeEntropy(argb, width, height, usePalette, paletteSize, transformBits, out redAndBlueAlwaysZero);
            if (method == 6 && quality == 100)
            {
                doNoCache = true;
                for (var i = EntropyIx.Direct; i <= EntropyIx.PaletteAndSpatial; i++)
                {
                    if (i is EntropyIx.Palette or EntropyIx.PaletteAndSpatial)
                    {
                        if (!usePalette) continue;
                        // kSortedDefault is omitted, as in libwebp, for backward compatibility.
                        configs.Add((i, PaletteSorting.MinimizeDelta));
                        configs.Add((i, PaletteSorting.ModifiedZeng));
                    }
                    else configs.Add((i, PaletteSorting.Unused));
                }
            }
            else
            {
                configs.Add((minEntropy, usePalette ? PaletteSorting.MinimizeDelta : PaletteSorting.Unused));
                if (quality >= 75 && method == 5)
                {
                    doNoCache = true;
                    if (minEntropy == EntropyIx.Palette) configs.Add((EntropyIx.PaletteAndSpatial, PaletteSorting.MinimizeDelta));
                }
            }
        }
        var subConfigs = new Vp8LCrunchSubConfig[lz77s];
        for (int j = 0; j < lz77s; j++)
            subConfigs[j] = new Vp8LCrunchSubConfig(j == 0 ? Vp8LLz77.Standard | Vp8LLz77.Rle : Vp8LLz77.Box, doNoCache);
        return configs.Select(c => new CrunchConfig(c.Entropy, c.Sorting, subConfigs)).ToArray();
    }

    // port: src/enc/vp8l_enc.c AnalyzeEntropy, AddSingle, AddSingleSubGreen, HashPix
    private static EntropyIx AnalyzeEntropy(uint[] argb, int width, int height, bool usePalette, int paletteSize, int transformBits, out bool redAndBlueAlwaysZero)
    {
        redAndBlueAlwaysZero = true;
        // Small palettes pack 2, 4 or 8 pixels together and beat any other transform in practice.
        if (usePalette && paletteSize <= 16) return EntropyIx.Palette;
        const int Alpha = 0, AlphaPred = 1, Green = 2, GreenPred = 3, Red = 4, RedPred = 5, Blue = 6, BluePred = 7;
        const int RedSubGreen = 8, RedPredSubGreen = 9, BlueSubGreen = 10, BluePredSubGreen = 11, PaletteHisto = 12, Total = 13;
        uint[] histo = ArrayPool<uint>.Shared.Rent(Total * 256);
        try
        {
            Span<uint> h = histo.AsSpan(0, Total * 256);
            h.Clear();
            uint previous = argb[0];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    uint pix = argb[row + x];
                    uint diff = SubPixels(pix, previous);
                    previous = pix;
                    if (diff == 0 || (y > 0 && pix == argb[row - width + x])) continue;
                    h[Alpha * 256 + (int)(pix >> 24)]++; h[Red * 256 + (int)((pix >> 16) & 255)]++;
                    h[Green * 256 + (int)((pix >> 8) & 255)]++; h[Blue * 256 + (int)(pix & 255)]++;
                    h[AlphaPred * 256 + (int)(diff >> 24)]++; h[RedPred * 256 + (int)((diff >> 16) & 255)]++;
                    h[GreenPred * 256 + (int)((diff >> 8) & 255)]++; h[BluePred * 256 + (int)(diff & 255)]++;
                    int green = (int)pix >> 8, greenDiff = (int)diff >> 8;
                    h[RedSubGreen * 256 + ((((int)pix >> 16) - green) & 255)]++;
                    h[BlueSubGreen * 256 + (((int)pix - green) & 255)]++;
                    h[RedPredSubGreen * 256 + ((((int)diff >> 16) - greenDiff) & 255)]++;
                    h[BluePredSubGreen * 256 + (((int)diff - greenDiff) & 255)]++;
                    // Approximate the palette by the entropy of the multiplicative hash.
                    h[PaletteHisto * 256 + (int)((uint)(((ulong)pix + (pix >> 19)) * 0x39c5fba7ul) >> 24)]++;
                }
            }
            // The zeros are removed too efficiently by the pix_diff == 0 comparison; at least one is likely to exist.
            h[RedPredSubGreen * 256]++; h[BluePredSubGreen * 256]++; h[RedPred * 256]++;
            h[GreenPred * 256]++; h[BluePred * 256]++; h[AlphaPred * 256]++;
            Span<long> component = stackalloc long[Total];
            for (int j = 0; j < Total; j++) component[j] = Vp8LEntropy.Bits(h.Slice(j * 256, 256));
            Span<long> entropy = stackalloc long[6];
            entropy[(int)EntropyIx.Direct] = component[Alpha] + component[Red] + component[Green] + component[Blue];
            entropy[(int)EntropyIx.Spatial] = component[AlphaPred] + component[RedPred] + component[GreenPred] + component[BluePred];
            entropy[(int)EntropyIx.SubGreen] = component[Alpha] + component[RedSubGreen] + component[Green] + component[BlueSubGreen];
            entropy[(int)EntropyIx.SpatialSubGreen] = component[AlphaPred] + component[RedPredSubGreen] + component[GreenPred] + component[BluePredSubGreen];
            entropy[(int)EntropyIx.Palette] = component[PaletteHisto];
            // Transforms cost bits to store; this matters for small images.
            long tiles = (long)Vp8LDecoder.SubSampleSize(width, transformBits) * Vp8LDecoder.SubSampleSize(height, transformBits);
            entropy[(int)EntropyIx.Spatial] += tiles * Vp8LEntropy.Log(14);
            entropy[(int)EntropyIx.SpatialSubGreen] += tiles * Vp8LEntropy.Log(24);
            entropy[(int)EntropyIx.Palette] += (paletteSize * 8L) << 23;
            var min = EntropyIx.Direct;
            for (var k = EntropyIx.Spatial; k <= (usePalette ? EntropyIx.Palette : EntropyIx.SpatialSubGreen); k++)
                if (entropy[(int)min] > entropy[(int)k]) min = k;
            ReadOnlySpan<int> pairs = [Red, Blue, RedPred, BluePred, RedSubGreen, BlueSubGreen, RedPredSubGreen, BluePredSubGreen, Red, Blue];
            Span<uint> redHisto = h.Slice(pairs[2 * (int)min] * 256, 256), blueHisto = h.Slice(pairs[2 * (int)min + 1] * 256, 256);
            for (int i = 1; i < 256; i++)
            {
                if ((redHisto[i] | blueHisto[i]) != 0) { redAndBlueAlwaysZero = false; break; }
            }
            return min;
        }
        finally { ArrayPool<uint>.Shared.Return(histo); }
    }

    // port: src/enc/vp8l_enc.c ClampBits
    public static int ClampBits(int width, int height, int bits, int minBits, int maxBits, int imageSizeMax)
    {
        bits = Math.Clamp(bits, minBits, maxBits);
        int imageSize = Vp8LDecoder.SubSampleSize(width, bits) * Vp8LDecoder.SubSampleSize(height, bits);
        while (bits < maxBits && imageSize > imageSizeMax)
        {
            bits++;
            imageSize = Vp8LDecoder.SubSampleSize(width, bits) * Vp8LDecoder.SubSampleSize(height, bits);
        }
        // If the bits reduce the image too much, choose the smallest value giving a histogram image of size 1.
        while (bits > minBits && imageSize == 1)
        {
            imageSize = Vp8LDecoder.SubSampleSize(width, bits - 1) * Vp8LDecoder.SubSampleSize(height, bits - 1);
            if (imageSize != 1) break;
            bits--;
        }
        return bits;
    }

    // port: src/enc/vp8l_enc.c GetHistoBits
    private static int GetHistoBits(int method, bool usePalette, int width, int height) =>
        ClampBits(width, height, (usePalette ? 9 : 7) - method, MinHuffmanBits, MaxHuffmanBits, MaxHuffImageSize);

    // port: src/enc/vp8l_enc.c GetTransformBits
    private static int GetTransformBits(int method, int histoBits) => Math.Min(histoBits, method < 4 ? 6 : method > 4 ? 4 : 5);

    // port: src/enc/vp8l_enc.c EncodeStreamHook
    private static Vp8LBitWriter EncodeConfig(uint[] argb, int width, int height, WebPEncoderOptions options, CrunchConfig config,
        uint[]? paletteSorted, int histoBits, int transformBits, bool redAndBlueAlwaysZero)
    {
        int method = options.Method, quality = (int)options.Quality;
        bool lowEffort = method == 0;
        bool usePalette = config.Entropy is EntropyIx.Palette or EntropyIx.PaletteAndSpatial;
        bool useSubtractGreen = config.Entropy is EntropyIx.SubGreen or EntropyIx.SpatialSubGreen;
        bool usePredict = config.Entropy is EntropyIx.Spatial or EntropyIx.SpatialSubGreen or EntropyIx.PaletteAndSpatial;
        // With a palette R/B are zero, so cross-color is pointless.
        bool useCrossColor = !lowEffort && !usePalette && !redAndBlueAlwaysZero && usePredict;
        int size = width * height, currentWidth = width, cacheBits = 0;
        var writer = new Vp8LBitWriter();
        uint[] work = ArrayPool<uint>.Shared.Rent(size);
        try
        {
            if (usePalette)
            {
                uint[] palette = PaletteSort(config.Sorting, argb, width, height, paletteSorted!);
                EncodePalette(writer, lowEffort, palette);
                currentWidth = MapImageFromPalette(argb, width, height, palette, work);
                // A color cache bigger than the number of colors is useless.
                if (palette.Length < 1 << MaxColorCacheBits) cacheBits = BitOperations.Log2((uint)palette.Length) + 1;
            }
            else
            {
                argb.AsSpan(0, size).CopyTo(work);
                if (options.NearLossless < 100 && !usePredict) Vp8LNearLossless.Apply(work, width, height, options.NearLossless);
            }
            if (useSubtractGreen)
            {
                writer.Put(1, 1); writer.Put(2, 2);
                Vp8LForwardTransforms.SubtractGreen(work.AsSpan(0, currentWidth * height));
            }
            if (usePredict)
            {
                // port: src/enc/vp8l_enc.c ApplyPredictFilter
                int maxBits = ClampBits(currentWidth, height, transformBits, MinTransformBits, MaxTransformBits, MaxPredictorImageSize);
                int minBits = ClampBits(currentWidth, height, maxBits - 2 * (method > 4 ? method - 4 : 0), MinTransformBits, MaxTransformBits, MaxPredictorImageSize);
                uint[] modes = Vp8LForwardTransforms.ResidualImage(currentWidth, height, minBits, maxBits, lowEffort, work,
                    usePalette ? 100 : options.NearLossless, options.Exact, useSubtractGreen, out int bits);
                WriteTransform(writer, modes, currentWidth, height, bits, 0, quality, lowEffort);
            }
            if (useCrossColor)
            {
                // port: src/enc/vp8l_enc.c ApplyCrossColorFilter
                uint[] colors = Vp8LForwardTransforms.ColorSpaceTransform(currentWidth, height, transformBits, quality, work, out int bits);
                WriteTransform(writer, colors, currentWidth, height, bits, 1, quality, lowEffort);
            }
            writer.Put(0, 1);
            Vp8LEntropyEncoder.EncodeImageInternal(writer, work, currentWidth, height, quality, lowEffort, config.SubConfigs, ref cacheBits, histoBits, options.MaxDegreeOfParallelism);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
        finally { ArrayPool<uint>.Shared.Return(work); }
    }

    private static void WriteTransform(Vp8LBitWriter writer, uint[] image, int width, int height, int bits, int type, int quality, bool lowEffort)
    {
        writer.Put(1, 1); writer.Put(type, 2); writer.Put(bits - MinTransformBits, 3);
        Vp8LEntropyEncoder.EncodeImageNoHuffman(writer, image, Vp8LDecoder.SubSampleSize(width, bits), Vp8LDecoder.SubSampleSize(height, bits), quality, lowEffort);
    }

    // port: src/utils/palette.c GetColorPalette
    private static uint[]? GetColorPalette(ReadOnlySpan<uint> pixels)
    {
        var colors = new HashSet<uint>();
        uint last = ~pixels[0];
        foreach (uint pixel in pixels)
        {
            if (pixel == last) continue;
            last = pixel;
            if (colors.Add(pixel) && colors.Count > MaxPaletteSize) return null;
        }
        uint[] palette = [.. colors];
        Array.Sort(palette);
        return palette;
    }

    // port: src/utils/palette.c PaletteSort
    private static uint[] PaletteSort(PaletteSorting method, uint[] argb, int width, int height, uint[] sorted)
    {
        int count = sorted.Length;
        switch (method)
        {
            case PaletteSorting.SortedDefault:
                if (sorted[0] == 0 && count > 17) return [.. sorted.AsSpan(1), 0u];
                return (uint[])sorted.Clone();
            case PaletteSorting.MinimizeDelta:
                return PaletteSortMinimizeDeltas(sorted);
            case PaletteSorting.ModifiedZeng:
                return Vp8LPalette.SortModifiedZeng(argb.AsSpan(0, width * height), width, height, sorted);
            default:
                throw new InvalidOperationException();
        }
    }

    // port: src/utils/palette.c PaletteHasNonMonotonousDeltas, PaletteSortMinimizeDeltas, PaletteColorDistance
    private static uint[] PaletteSortMinimizeDeltas(uint[] sorted)
    {
        uint[] palette = (uint[])sorted.Clone();
        uint predict = 0;
        int signs = 0;
        foreach (uint color in sorted)
        {
            uint diff = SubPixels(color, predict);
            int rd = (int)((diff >> 16) & 255), gd = (int)((diff >> 8) & 255), bd = (int)(diff & 255);
            if (rd != 0) signs |= rd < 0x80 ? 1 : 2;
            if (gd != 0) signs |= gd < 0x80 ? 8 : 16;
            if (bd != 0) signs |= bd < 0x80 ? 64 : 128;
            predict = color;
        }
        if ((signs & (signs << 1)) == 0) return palette;
        int count = palette.Length;
        // Keep a leading transparent black at the end, where the decoder fills it in for free.
        if (count > 17 && palette[0] == 0)
        {
            count--;
            (palette[count], palette[0]) = (palette[0], palette[count]);
        }
        predict = 0;
        for (int i = 0; i < count; i++)
        {
            int best = i;
            uint bestScore = uint.MaxValue;
            for (int k = i; k < count; k++)
            {
                uint diff = SubPixels(palette[k], predict);
                uint score = (Distance(diff & 255) + Distance((diff >> 8) & 255) + Distance((diff >> 16) & 255)) * 9 + Distance(diff >> 24);
                if (bestScore > score) { bestScore = score; best = k; }
            }
            (palette[best], palette[i]) = (palette[i], palette[best]);
            predict = palette[i];
        }
        return palette;

        static uint Distance(uint v) => v <= 128 ? v : 256 - v;
    }

    // port: src/enc/vp8l_enc.c EncodePalette
    private static void EncodePalette(Vp8LBitWriter writer, bool lowEffort, uint[] palette)
    {
        // A trailing zero is filled in automatically by the decoder; only possible without pixel packing.
        int encodedSize = palette[^1] == 0 && palette.Length > 17 ? palette.Length - 1 : palette.Length;
        writer.Put(1, 1); writer.Put(3, 2); writer.Put(encodedSize - 1, 8);
        uint[] deltas = new uint[encodedSize];
        for (int i = encodedSize - 1; i >= 1; i--) deltas[i] = SubPixels(palette[i], palette[i - 1]);
        deltas[0] = palette[0];
        Vp8LEntropyEncoder.EncodeImageNoHuffman(writer, deltas, encodedSize, 1, 20, lowEffort);
    }

    // port: src/enc/vp8l_enc.c MapImageFromPalette, ApplyPalette; src/dsp/lossless_enc.c VP8LBundleColorMap_C
    private static int MapImageFromPalette(uint[] argb, int width, int height, uint[] palette, uint[] destination)
    {
        int xbits = palette.Length <= 2 ? 3 : palette.Length <= 4 ? 2 : palette.Length <= 16 ? 1 : 0;
        int packedWidth = Vp8LDecoder.SubSampleSize(width, xbits), bitDepth = 1 << (3 - xbits), mask = (1 << xbits) - 1;
        uint[] sortedColors = (uint[])palette.Clone();
        int[] sortedIndices = new int[palette.Length];
        for (int i = 0; i < sortedIndices.Length; i++) sortedIndices[i] = i;
        Array.Sort(sortedColors, sortedIndices);
        uint previousPixel = palette[0];
        uint previousIndex = 0;
        for (int y = 0; y < height; y++)
        {
            int src = y * width, dst = y * packedWidth;
            uint code = 0xff000000u;
            for (int x = 0; x < width; x++)
            {
                uint pixel = argb[src + x];
                if (pixel != previousPixel)
                {
                    previousIndex = (uint)sortedIndices[Array.BinarySearch(sortedColors, pixel)];
                    previousPixel = pixel;
                }
                int sub = x & mask;
                if (sub == 0) code = 0xff000000u;
                code |= previousIndex << (8 + bitDepth * sub);
                destination[dst + (x >> xbits)] = code;
            }
        }
        return packedWidth;
    }
}
